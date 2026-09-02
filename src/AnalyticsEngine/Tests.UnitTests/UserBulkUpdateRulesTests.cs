using Common.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the bulk existing-user update rules: which users make it into a batch, what every
    /// column gets, and the manager foreign-key precedence chain.
    ///
    /// All of this used to live inside <c>UserBatchProcessor</c> next to a <c>SqlConnection</c> and a
    /// <c>SqlBulkCopy</c>, so none of it could be asserted without a live SQL Server and none of it
    /// had a test - despite being the write path a ~200,000-user tenant actually takes. See issues
    /// #371 / #381.
    /// </summary>
    [TestClass]
    public class UserBulkUpdateRulesTests
    {
        private const string ManagerAadId = "00000000-0000-0000-0000-0000000000aa";
        private static readonly DateTime Stamp = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);

        #region Fixtures

        private static GraphUser GraphUserWithEverything()
        {
            return new GraphUser
            {
                Id = "00000000-0000-0000-0000-000000000001",
                UserPrincipalName = "jane.doe@contoso.com",
                Mail = "jane.doe@contoso.com",
                AccountEnabled = true,
                PostalCode = "SW1A 1AA",
                Department = "Engineering",
                JobTitle = "Principal Engineer",
                OfficeLocation = "London",
                UsageLocation = "GB",
                Country = "United Kingdom",
                State = "Greater London",
                CompanyName = "Contoso",
            };
        }

        /// <summary>Lookup maps where every name in <see cref="GraphUserWithEverything"/> has a saved row.</summary>
        private static LookupEntityMaps SavedLookupsForEverything()
        {
            var maps = new LookupEntityMaps();
            maps.Departments["Engineering"] = new UserDepartment { ID = 11, Name = "Engineering" };
            maps.JobTitles["Principal Engineer"] = new UserJobTitle { ID = 12, Name = "Principal Engineer" };
            maps.OfficeLocations["London"] = new UserOfficeLocation { ID = 13, Name = "London" };
            maps.UsageLocations["GB"] = new UserUsageLocation { ID = 14, Name = "GB" };
            maps.Countries["United Kingdom"] = new CountryOrRegion { ID = 15, Name = "United Kingdom" };
            maps.StatesOrProvinces["Greater London"] = new StateOrProvince { ID = 16, Name = "Greater London" };
            maps.CompanyNames["Contoso"] = new CompanyName { ID = 17, Name = "Contoso" };
            return maps;
        }

        private static Dictionary<string, Common.Entities.User> UsersByUpn(params Common.Entities.User[] users)
        {
            var d = new Dictionary<string, Common.Entities.User>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users) d[u.UserPrincipalName] = u;
            return d;
        }

        private static Dictionary<string, Common.Entities.User> UsersByAadId(params Common.Entities.User[] users)
        {
            var d = new Dictionary<string, Common.Entities.User>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users) d[u.AzureAdId] = u;
            return d;
        }

        private static Dictionary<string, GraphUser> GraphUsersByAadId(params GraphUser[] users)
        {
            var d = new Dictionary<string, GraphUser>(StringComparer.OrdinalIgnoreCase);
            foreach (var u in users) d[u.Id] = u;
            return d;
        }

        private static GraphUser WithManager(GraphUser user, string managerAadId)
        {
            user.ManagerInfo = new List<ManagerInfo> { new ManagerInfo { Id = managerAadId } };
            return user;
        }

        private static DataRow SingleRowFor(GraphUser graphUser, LookupEntityMaps maps, Common.Entities.User dbUser)
        {
            var table = UserBulkUpdateRules.BuildUpdateTable(
                new List<GraphUser> { graphUser },
                maps,
                UsersByAadId(),
                UsersByUpn(dbUser),
                GraphUsersByAadId(),
                Stamp);

            Assert.AreEqual(1, table.Rows.Count, "Expected exactly one row for one mapped user.");
            return table.Rows[0];
        }

        #endregion

        #region Table shape - the three copies of the column list must agree

        [TestMethod]
        public void UserBulkUpdate_DataTableShape_MatchesTheDeclaredColumnList()
        {
            var table = UserBulkUpdateRules.CreateUpdateTable();

            CollectionAssert.AreEqual(
                UserBulkUpdateRules.UpdateTableColumns.ToArray(),
                table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToArray(),
                "The batch table's columns must match UpdateTableColumns exactly, in order - " +
                "SqlUserBulkUpdateWriter maps them positionally from that same list.");
        }

        [TestMethod]
        public void UserBulkUpdate_TempTableDdl_DeclaresExactlyTheColumnsTheBatchCarries()
        {
            // The temp table, the DataTable and the SqlBulkCopy mappings used to be three
            // independently maintained lists of the same fourteen names. Adding a column to one and
            // forgetting the others is a runtime failure inside a customer import, so pin them here.
            //
            // Matched as whole identifiers, not substrings: "id" occurs inside azure_ad_id,
            // department_id, manager_id and six others, so a substring check would stay green even
            // if the join-key column were deleted from the DDL outright.
            CollectionAssert.AreEquivalent(
                UserBulkUpdateRules.UpdateTableColumns.ToArray(),
                DeclaredTempTableColumns(SqlUserBulkUpdateWriter.CREATE_TEMP_TABLE_SQL),
                "#user_updates must declare exactly the columns the batch carries - no more, no fewer.");
        }

        [TestMethod]
        public void UserBulkUpdate_UpdateStatement_WritesEveryColumnExceptTheJoinKey()
        {
            var assignments = Assignments(SqlUserBulkUpdateWriter.UPDATE_FROM_TEMP_SQL);

            CollectionAssert.AreEquivalent(
                UserBulkUpdateRules.UpdateTableColumns.Where(c => c != "id").ToArray(),
                assignments.Select(a => a.Target).ToArray(),
                "The UPDATE ... FROM JOIN must assign every batch column except the join key - a column " +
                "the batch carries but the statement never writes is silently dropped.");

            // Checking only the target would let u.mail = t.postalcode through: every column would
            // still be assigned, from the wrong source.
            var crossed = assignments.Where(a => a.Target != a.Source).ToArray();
            Assert.AreEqual(0, crossed.Length,
                "Each column must be written from the batch column of the same name. Crossed: " +
                string.Join(", ", crossed.Select(a => $"u.{a.Target} = t.{a.Source}")));

            StringAssert.Contains(SqlUserBulkUpdateWriter.UPDATE_FROM_TEMP_SQL, "ON u.id = t.id",
                "'id' is the join key, not an updated column.");
        }

        /// <summary>Column identifiers declared by the CREATE TABLE, taken from the start of each line.</summary>
        private static string[] DeclaredTempTableColumns(string createTableSql)
        {
            var body = createTableSql.Substring(createTableSql.IndexOf('(') + 1);
            return Regex.Matches(body, @"^\s*(?<name>[a-z_][a-z0-9_]*)\s+(INT|BIT|DATETIME|NVARCHAR)",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => m.Groups["name"].Value)
                .ToArray();
        }

        /// <summary>The "u.x = t.y" pairs in the UPDATE's SET clause.</summary>
        private static (string Target, string Source)[] Assignments(string updateSql)
        {
            // Only the SET clause: the ON clause also matches "u.x = t.x", and counting the join key
            // as an assignment would hide a column the statement never actually writes.
            var setStart = updateSql.IndexOf("SET ", System.StringComparison.Ordinal);
            var fromStart = updateSql.IndexOf("FROM dbo.users", System.StringComparison.Ordinal);
            var setClause = updateSql.Substring(setStart, fromStart - setStart);

            return Regex.Matches(setClause, @"u\.(?<target>[a-z_][a-z0-9_]*)\s*=\s*t\.(?<source>[a-z_][a-z0-9_]*)")
                .Cast<Match>()
                .Select(m => (Target: m.Groups["target"].Value, Source: m.Groups["source"].Value))
                .ToArray();
        }

        #endregion

        #region Column mapping

        [TestMethod]
        public void UserBulkUpdate_MapsDirectFieldsAndResolvedForeignKeys()
        {
            var dbUser = new Common.Entities.User { ID = 42, UserPrincipalName = "jane.doe@contoso.com" };
            var row = SingleRowFor(GraphUserWithEverything(), SavedLookupsForEverything(), dbUser);

            Assert.AreEqual(42, row["id"]);
            Assert.AreEqual("00000000-0000-0000-0000-000000000001", row["azure_ad_id"]);
            Assert.AreEqual(true, row["account_enabled"]);
            Assert.AreEqual("jane.doe@contoso.com", row["mail"]);
            Assert.AreEqual("SW1A 1AA", row["postalcode"]);
            Assert.AreEqual(11, row["department_id"]);
            Assert.AreEqual(12, row["job_title_id"]);
            Assert.AreEqual(13, row["office_location_id"]);
            Assert.AreEqual(14, row["usage_location_id"]);
            Assert.AreEqual(15, row["country_or_region_id"]);
            Assert.AreEqual(16, row["state_or_province_id"]);
            Assert.AreEqual(17, row["company_name_id"]);
            Assert.AreEqual(Stamp, row["last_updated"]);
        }

        [TestMethod]
        public void UserBulkUpdate_NonAsciiLookupValues_ResolveWithoutMangling()
        {
            // Display names, departments, offices and company names are customer text and are
            // routinely non-Latin. The lookup maps are keyed by the normalised name, so a mapping
            // change that mangled the characters would silently resolve every one of them to NULL.
            // (UPNs are excluded on purpose - Entra restricts them to ASCII.)
            var graphUser = GraphUserWithEverything();
            graphUser.Department = "Καλημέρα κόσμε";
            graphUser.JobTitle = "Μηχανικός Λογισμικού";
            graphUser.OfficeLocation = "Αθήνα";
            graphUser.Country = "Ελλάδα";
            graphUser.State = "Αττική";
            graphUser.CompanyName = "Κόντοσο";

            var maps = SavedLookupsForEverything();
            maps.Departments["Καλημέρα κόσμε"] = new UserDepartment { ID = 21, Name = "Καλημέρα κόσμε" };
            maps.JobTitles["Μηχανικός Λογισμικού"] = new UserJobTitle { ID = 22, Name = "Μηχανικός Λογισμικού" };
            maps.OfficeLocations["Αθήνα"] = new UserOfficeLocation { ID = 23, Name = "Αθήνα" };
            maps.Countries["Ελλάδα"] = new CountryOrRegion { ID = 24, Name = "Ελλάδα" };
            maps.StatesOrProvinces["Αττική"] = new StateOrProvince { ID = 25, Name = "Αττική" };
            maps.CompanyNames["Κόντοσο"] = new CompanyName { ID = 26, Name = "Κόντοσο" };

            var dbUser = new Common.Entities.User { ID = 42, UserPrincipalName = "jane.doe@contoso.com" };
            var row = SingleRowFor(graphUser, maps, dbUser);

            Assert.AreEqual(21, row["department_id"]);
            Assert.AreEqual(22, row["job_title_id"]);
            Assert.AreEqual(23, row["office_location_id"]);
            Assert.AreEqual(24, row["country_or_region_id"]);
            Assert.AreEqual(25, row["state_or_province_id"]);
            Assert.AreEqual(26, row["company_name_id"]);
        }

        [TestMethod]
        public void UserBulkUpdate_ValueGraphNoLongerReports_ClearsTheForeignKey()
        {
            // Graph sending nothing means "clear it", exactly as the EF path does - the pipeline
            // deliberately nulls the lookup rather than leaving the previous value in place.
            var graphUser = GraphUserWithEverything();
            graphUser.Department = null;
            graphUser.JobTitle = "   ";

            var dbUser = new Common.Entities.User { ID = 42, UserPrincipalName = "jane.doe@contoso.com" };
            var row = SingleRowFor(graphUser, SavedLookupsForEverything(), dbUser);

            Assert.AreEqual(DBNull.Value, row["department_id"]);
            Assert.AreEqual(DBNull.Value, row["job_title_id"], "A whitespace-only value normalises away and must clear the lookup.");
            Assert.AreEqual(13, row["office_location_id"], "Only the values Graph stopped reporting should be cleared.");
        }

        [TestMethod]
        public void UserBulkUpdate_NullDirectFields_AreWrittenAsDbNullNotEmptyStrings()
        {
            var graphUser = GraphUserWithEverything();
            graphUser.Mail = null;
            graphUser.PostalCode = null;
            graphUser.Id = null;
            graphUser.AccountEnabled = null;

            var dbUser = new Common.Entities.User { ID = 42, UserPrincipalName = "jane.doe@contoso.com" };
            var row = SingleRowFor(graphUser, SavedLookupsForEverything(), dbUser);

            Assert.AreEqual(DBNull.Value, row["mail"]);
            Assert.AreEqual(DBNull.Value, row["postalcode"]);
            Assert.AreEqual(DBNull.Value, row["azure_ad_id"]);
            Assert.AreEqual(DBNull.Value, row["account_enabled"]);
        }

        [TestMethod]
        public void UserBulkUpdate_OverLengthLookupValue_UsesTheSameCapAsTheMappingRule()
        {
            var longDepartment = new string('D', UserMetadataMappingRules.LookupNameMaxLength + 50);
            var capped = UserMetadataMappingRules.NormaliseLookupName(longDepartment);
            Assert.AreEqual(UserMetadataMappingRules.LookupNameMaxLength, capped.Length,
                "Precondition: the mapping rule caps the value.");

            var graphUser = GraphUserWithEverything();
            graphUser.Department = longDepartment;

            var maps = SavedLookupsForEverything();
            maps.Departments[capped] = new UserDepartment { ID = 99, Name = capped };

            var dbUser = new Common.Entities.User { ID = 42, UserPrincipalName = "jane.doe@contoso.com" };
            var row = SingleRowFor(graphUser, maps, dbUser);

            Assert.AreEqual(99, row["department_id"],
                "The batch must look the lookup up under the capped name, or every over-length value silently clears its column.");
        }

        [TestMethod]
        public void UserBulkUpdate_UnsavedLookupEntity_ResolvesToDbNull()
        {
            // A lookup that has not been through SaveChanges has ID == 0, which is not a usable
            // foreign key - writing it would violate the FK constraint.
            var maps = SavedLookupsForEverything();
            maps.Departments["Engineering"] = new UserDepartment { ID = 0, Name = "Engineering" };

            var dbUser = new Common.Entities.User { ID = 42, UserPrincipalName = "jane.doe@contoso.com" };
            var row = SingleRowFor(GraphUserWithEverything(), maps, dbUser);

            Assert.AreEqual(DBNull.Value, row["department_id"]);
        }

        [TestMethod]
        public void UserBulkUpdate_LastUpdated_IsTheSuppliedStampOnEveryRow()
        {
            var a = new GraphUser { Id = "a", UserPrincipalName = "a@contoso.com" };
            var b = new GraphUser { Id = "b", UserPrincipalName = "b@contoso.com" };

            var table = UserBulkUpdateRules.BuildUpdateTable(
                new List<GraphUser> { a, b },
                new LookupEntityMaps(),
                UsersByAadId(),
                UsersByUpn(
                    new Common.Entities.User { ID = 1, UserPrincipalName = "a@contoso.com" },
                    new Common.Entities.User { ID = 2, UserPrincipalName = "b@contoso.com" }),
                GraphUsersByAadId(),
                Stamp);

            Assert.AreEqual(2, table.Rows.Count);
            foreach (DataRow row in table.Rows)
            {
                Assert.AreEqual(Stamp, row["last_updated"]);
            }
        }

        #endregion

        #region Which users make it into the batch

        [TestMethod]
        public void UserBulkUpdate_UserWithNoUpn_IsSkipped()
        {
            var table = UserBulkUpdateRules.BuildUpdateTable(
                new List<GraphUser> { new GraphUser { Id = "x", UserPrincipalName = null } },
                new LookupEntityMaps(), UsersByAadId(), UsersByUpn(), GraphUsersByAadId(), Stamp);

            Assert.AreEqual(0, table.Rows.Count);
        }

        [TestMethod]
        public void UserBulkUpdate_UserWithNoMatchingDbRow_IsSkipped()
        {
            // The UPDATE joins on the primary key, so a Graph user with no users row has nothing to
            // update. Emitting a row would just be a batch entry that matches nothing.
            var table = UserBulkUpdateRules.BuildUpdateTable(
                new List<GraphUser> { GraphUserWithEverything() },
                SavedLookupsForEverything(), UsersByAadId(), UsersByUpn(), GraphUsersByAadId(), Stamp);

            Assert.AreEqual(0, table.Rows.Count);
        }

        [TestMethod]
        public void UserBulkUpdate_DbUserWithNoIdYet_IsSkipped()
        {
            var unsaved = new Common.Entities.User { ID = 0, UserPrincipalName = "jane.doe@contoso.com" };

            var table = UserBulkUpdateRules.BuildUpdateTable(
                new List<GraphUser> { GraphUserWithEverything() },
                SavedLookupsForEverything(), UsersByAadId(), UsersByUpn(unsaved), GraphUsersByAadId(), Stamp);

            Assert.AreEqual(0, table.Rows.Count);
        }

        [TestMethod]
        public void UserBulkUpdate_EmptyBatch_ProducesAnEmptyTableWithTheRightShape()
        {
            var table = UserBulkUpdateRules.BuildUpdateTable(
                new List<GraphUser>(), new LookupEntityMaps(), UsersByAadId(), UsersByUpn(), GraphUsersByAadId(), Stamp);

            Assert.AreEqual(0, table.Rows.Count);
            Assert.AreEqual(UserBulkUpdateRules.UpdateTableColumns.Count, table.Columns.Count);
        }

        #endregion

        #region Manager precedence chain

        [TestMethod]
        public void ManagerResolution_PrefersTheDbUserFoundByEntraId()
        {
            var managerByAadId = new Common.Entities.User { ID = 7, AzureAdId = ManagerAadId, UserPrincipalName = "boss@contoso.com" };
            var differentManagerByUpn = new Common.Entities.User { ID = 8, UserPrincipalName = "boss@contoso.com" };
            var managerGraphUser = new GraphUser { Id = ManagerAadId, UserPrincipalName = "boss@contoso.com" };

            var resolved = UserBulkUpdateRules.ResolveManagerId(
                ManagerAadId,
                UsersByAadId(managerByAadId),
                UsersByUpn(differentManagerByUpn),
                GraphUsersByAadId(managerGraphUser));

            Assert.AreEqual(7, resolved, "The Entra-id match must win over the UPN fallback.");
        }

        [TestMethod]
        public void ManagerResolution_FallsBackToTheGraphBatchUpnThenTheDbUpnMap()
        {
            var managerGraphUser = new GraphUser { Id = ManagerAadId, UserPrincipalName = "boss@contoso.com" };
            var managerByUpn = new Common.Entities.User { ID = 8, UserPrincipalName = "boss@contoso.com" };

            var resolved = UserBulkUpdateRules.ResolveManagerId(
                ManagerAadId,
                UsersByAadId(),                       // no Entra-id match
                UsersByUpn(managerByUpn),
                GraphUsersByAadId(managerGraphUser));

            Assert.AreEqual(8, resolved);
        }

        [TestMethod]
        public void ManagerResolution_NoManagerReportedByGraph_ClearsTheColumn()
        {
            var resolved = UserBulkUpdateRules.ResolveManagerId(
                null, UsersByAadId(), UsersByUpn(), GraphUsersByAadId());

            Assert.AreEqual(DBNull.Value, resolved);
        }

        [TestMethod]
        public void ManagerResolution_ManagerNotInTheBatchOrTheDatabase_ClearsTheColumn()
        {
            var resolved = UserBulkUpdateRules.ResolveManagerId(
                ManagerAadId, UsersByAadId(), UsersByUpn(), GraphUsersByAadId());

            Assert.AreEqual(DBNull.Value, resolved);
        }

        [TestMethod]
        public void ManagerResolution_ManagerNotSavedYet_ClearsTheColumnRatherThanWritingZero()
        {
            // manager_id is a foreign key; 0 is not a users row, so an unsaved manager must produce
            // NULL rather than a constraint violation.
            var unsavedByAadId = new Common.Entities.User { ID = 0, AzureAdId = ManagerAadId, UserPrincipalName = "boss@contoso.com" };
            var unsavedByUpn = new Common.Entities.User { ID = 0, UserPrincipalName = "boss@contoso.com" };
            var managerGraphUser = new GraphUser { Id = ManagerAadId, UserPrincipalName = "boss@contoso.com" };

            var resolved = UserBulkUpdateRules.ResolveManagerId(
                ManagerAadId,
                UsersByAadId(unsavedByAadId),
                UsersByUpn(unsavedByUpn),
                GraphUsersByAadId(managerGraphUser));

            Assert.AreEqual(DBNull.Value, resolved);
        }

        [TestMethod]
        public void ManagerResolution_ManagerInTheGraphBatchWithNoUpn_ClearsTheColumn()
        {
            var managerGraphUser = new GraphUser { Id = ManagerAadId, UserPrincipalName = null };

            var resolved = UserBulkUpdateRules.ResolveManagerId(
                ManagerAadId, UsersByAadId(), UsersByUpn(), GraphUsersByAadId(managerGraphUser));

            Assert.AreEqual(DBNull.Value, resolved);
        }

        [TestMethod]
        public void UserBulkUpdate_ManagerIdReachesTheRow()
        {
            // The chain above is only useful if BuildUpdateTable actually calls it for the user's
            // own manager id rather than, say, always writing NULL.
            var graphUser = WithManager(GraphUserWithEverything(), ManagerAadId);
            var manager = new Common.Entities.User { ID = 7, AzureAdId = ManagerAadId, UserPrincipalName = "boss@contoso.com" };
            var dbUser = new Common.Entities.User { ID = 42, UserPrincipalName = "jane.doe@contoso.com" };

            var table = UserBulkUpdateRules.BuildUpdateTable(
                new List<GraphUser> { graphUser },
                SavedLookupsForEverything(),
                UsersByAadId(manager),
                UsersByUpn(dbUser),
                GraphUsersByAadId(),
                Stamp);

            Assert.AreEqual(1, table.Rows.Count);
            Assert.AreEqual(7, table.Rows[0]["manager_id"]);
        }

        [TestMethod]
        public void UserBulkUpdate_UserWhoseManagerWasRemoved_ClearsManagerId()
        {
            var graphUser = GraphUserWithEverything();   // no ManagerInfo entries
            var dbUser = new Common.Entities.User { ID = 42, UserPrincipalName = "jane.doe@contoso.com" };
            var row = SingleRowFor(graphUser, SavedLookupsForEverything(), dbUser);

            Assert.AreEqual(DBNull.Value, row["manager_id"]);
        }

        #endregion
    }
}
