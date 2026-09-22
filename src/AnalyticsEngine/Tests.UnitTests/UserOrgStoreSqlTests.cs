using Common.Entities.Migrations;
using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The user-org SQL adapters, exercised against a real SQL Server built from the migration's own
    /// <see cref="UserOrganisations.Up_Sql"/>.
    /// </summary>
    /// <remarks>
    /// A scratch database per test class rather than the shared unit-test database: these tests delete
    /// whole org types and every assignment under them, which would be destructive to anything else
    /// running concurrently.
    ///
    /// <c>dbo.users</c> is created with production's exact column types - notably
    /// <c>user_name varchar(250)</c>. Declaring it <c>nvarchar</c> here would make the UPN-matching
    /// assertions self-fulfilling and prove nothing about the real schema.
    /// </remarks>
    [TestClass]
    public class UserOrgStoreSqlTests
    {
        private const string GreekOrgName = "Καλημέρα κόσμε";

        private static ScratchDatabase _db;
        private static IUserOrgTypeStore _types;
        private static IUserOrgAssignmentStore _assignments;
        private static IUserOrgImportJobStore _jobs;

        [ClassInitialize]
        public static void ClassInit(TestContext context)
        {
            _db = ScratchDatabase.Create("userorgs");

            // Production shape: user_name is varchar(250), because an Entra UPN is ASCII by policy.
            _db.Execute(@"
CREATE TABLE dbo.users (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_users PRIMARY KEY CLUSTERED,
    user_name varchar(250) NOT NULL
);");

            _db.Execute(UserOrganisations.Up_Sql);

            _types = UserOrgStores.CreateTypeStore(_db.ConnectionString);
            _assignments = UserOrgStores.CreateAssignmentStore(_db.ConnectionString);
            _jobs = UserOrgStores.CreateImportJobStore(_db.ConnectionString);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _db?.Dispose();
        }

        [TestInitialize]
        public void Reset()
        {
            _db.Execute(@"
DELETE FROM dbo.user_org_import_staging;
DELETE FROM dbo.user_org_import_jobs;
DELETE FROM dbo.user_org_assignments;
DELETE FROM dbo.user_org_values;
DELETE FROM dbo.user_org_types;
DELETE FROM dbo.users;");
        }

        private int AddUser(string upn)
        {
            return Convert.ToInt32(_db.Scalar(
                $"INSERT INTO dbo.users (user_name) OUTPUT INSERTED.id VALUES ('{upn.Replace("'", "''")}');"));
        }

        private static UserOrgType CsvType(string name)
        {
            return new UserOrgType { Name = name, SourceKind = UserOrgSourceKind.CsvUpload, IsEnabled = true };
        }

        private int Count(string sql)
        {
            return Convert.ToInt32(_db.Scalar(sql));
        }

        private void Execute(string sql)
        {
            _db.Execute(sql);
        }

        #region Org type CRUD

        [TestMethod]
        public async Task CreateAndGet_RoundTripsAnOrgType()
        {
            var id = await _types.CreateAsync(new UserOrgType
            {
                Name = "Cost Centre",
                SourceKind = UserOrgSourceKind.EntraAttribute,
                EntraAttributeName = "EXTENSIONATTRIBUTE7",
                IsEnabled = true,
            });

            var loaded = await _types.GetAsync(id);

            Assert.AreEqual("Cost Centre", loaded.Name);
            Assert.AreEqual(UserOrgSourceKind.EntraAttribute, loaded.SourceKind);
            Assert.AreEqual(
                "extensionAttribute7",
                loaded.EntraAttributeName,
                "The attribute must be stored canonically, or the same slot could be configured twice under two spellings.");
            Assert.IsTrue(loaded.IsEnabled);
            Assert.IsNull(loaded.ModifiedUtc);
        }

        [TestMethod]
        public async Task Create_RejectsADuplicateNameWithAnAdminReadableMessage()
        {
            await _types.CreateAsync(CsvType("Business Unit"));

            try
            {
                await _types.CreateAsync(CsvType("business unit"));
                Assert.Fail("A duplicate name should have been rejected.");
            }
            catch (UserOrgValidationException ex)
            {
                StringAssert.Contains(ex.Message, "already exists");
            }
        }

        [TestMethod]
        public async Task Create_RejectsAnInvalidEntraAttribute()
        {
            try
            {
                await _types.CreateAsync(new UserOrgType
                {
                    Name = "Bad",
                    SourceKind = UserOrgSourceKind.EntraAttribute,
                    EntraAttributeName = "extensionAttribute99",
                });
                Assert.Fail("An out-of-range slot should have been rejected before it reached SQL.");
            }
            catch (UserOrgValidationException ex)
            {
                StringAssert.Contains(ex.Message, "extensionAttribute15");
            }
        }

        [TestMethod]
        public async Task Create_ClearsAnyAttributeOnACsvSourcedType()
        {
            // Leaving a stale attribute behind would quietly add a property to the Graph $select for a
            // type that no longer reads from Graph - and therefore change the delta-token key too.
            var id = await _types.CreateAsync(new UserOrgType
            {
                Name = "From spreadsheet",
                SourceKind = UserOrgSourceKind.CsvUpload,
                EntraAttributeName = "extensionAttribute2",
            });

            Assert.IsNull((await _types.GetAsync(id)).EntraAttributeName);
        }

        [TestMethod]
        public async Task Update_ChangesTheTypeAndStampsModified()
        {
            var id = await _types.CreateAsync(CsvType("Initial"));
            var type = await _types.GetAsync(id);

            type.Name = "Renamed";
            type.IsEnabled = false;
            await _types.UpdateAsync(type, false);

            var loaded = await _types.GetAsync(id);
            Assert.AreEqual("Renamed", loaded.Name);
            Assert.IsFalse(loaded.IsEnabled);
            Assert.IsNotNull(loaded.ModifiedUtc);
        }

        [TestMethod]
        public async Task Update_WithClearDiscardsEverythingTheOldSourcePutThere()
        {
            var user = AddUser("a@contoso.com");
            var typeId = await _types.CreateAsync(new UserOrgType
            {
                Name = "Cost Centre",
                SourceKind = UserOrgSourceKind.EntraAttribute,
                EntraAttributeName = "extensionAttribute1",
                IsEnabled = true,
            });

            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(user, typeId, "From Entra") });
            Assert.AreEqual(1, (await _assignments.GetForUserAsync(user)).Count);

            var type = await _types.GetAsync(typeId);
            type.SourceKind = UserOrgSourceKind.CsvUpload;
            type.EntraAttributeName = null;
            await _types.UpdateAsync(type, true);

            Assert.AreEqual(
                0,
                (await _assignments.GetForUserAsync(user)).Count,
                "Values read from a source that is no longer the source of truth must not survive the switch.");
            Assert.AreEqual(
                0,
                Count($"SELECT COUNT(*) FROM dbo.user_org_values WHERE org_type_id = {typeId}"),
                "The old source's value list would otherwise keep offering organisations nothing is in.");
            Assert.AreEqual(UserOrgSourceKind.CsvUpload, (await _types.GetAsync(typeId)).SourceKind);
        }

        [TestMethod]
        public async Task Update_OfAMissingTypeClearsNothing()
        {
            // The clear and the update are one transaction precisely so neither can happen alone.
            var user = AddUser("a@contoso.com");
            var survivor = await _types.CreateAsync(CsvType("Survivor"));
            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(user, survivor, "Kept") });

            try
            {
                await _types.UpdateAsync(
                    new UserOrgType { Id = 987654, Name = "Ghost", SourceKind = UserOrgSourceKind.CsvUpload },
                    true);
                Assert.Fail("Updating a deleted type should be reported.");
            }
            catch (UserOrgValidationException)
            {
            }

            Assert.AreEqual(1, (await _assignments.GetForUserAsync(user)).Count);
        }

        [TestMethod]
        public async Task Update_OfAMissingTypeIsReportedRatherThanSilentlyIgnored()
        {
            try
            {
                await _types.UpdateAsync(new UserOrgType
                {
                    Id = 987654,
                    Name = "Ghost",
                    SourceKind = UserOrgSourceKind.CsvUpload,
                }, false);
                Assert.Fail("Updating a deleted type should be reported.");
            }
            catch (UserOrgValidationException ex)
            {
                StringAssert.Contains(ex.Message, "no longer exists");
            }
        }

        [TestMethod]
        public async Task GetEnabledEntraTypes_ExcludesDisabledAndCsvTypes()
        {
            await _types.CreateAsync(new UserOrgType
            {
                Name = "Enabled Entra",
                SourceKind = UserOrgSourceKind.EntraAttribute,
                EntraAttributeName = "extensionAttribute1",
                IsEnabled = true,
            });
            await _types.CreateAsync(new UserOrgType
            {
                Name = "Disabled Entra",
                SourceKind = UserOrgSourceKind.EntraAttribute,
                EntraAttributeName = "extensionAttribute2",
                IsEnabled = false,
            });
            await _types.CreateAsync(CsvType("A CSV one"));

            var enabled = await _types.GetEnabledEntraTypesAsync();

            CollectionAssert.AreEqual(new[] { "Enabled Entra" }, enabled.Select(t => t.Name).ToArray());
        }

        [TestMethod]
        public async Task Delete_RemovesEverythingHangingOffTheType()
        {
            var userId = AddUser("person@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Doomed"));
            var keptTypeId = await _types.CreateAsync(CsvType("Survivor"));

            await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(userId, typeId, "Value"),
                new UserOrgAssignmentUpdate(userId, keptTypeId, "Keep me"),
            });

            await _jobs.CreateJobWithRowsAsync(
                new UserOrgImportJob { OrgTypeId = typeId, Mode = UserOrgImportMode.Merge, StartedBy = "admin" },
                new[] { new UserOrgStagedRow(1, "person@contoso.com", "Value") });

            await _types.DeleteAsync(typeId);

            Assert.IsNull(await _types.GetAsync(typeId));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_assignments WHERE org_type_id = {typeId}"));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_values WHERE org_type_id = {typeId}"));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_import_jobs WHERE org_type_id = {typeId}"));
            Assert.AreEqual(0, Count("SELECT COUNT(*) FROM dbo.user_org_import_staging"));

            Assert.AreEqual(
                1,
                Count($"SELECT COUNT(*) FROM dbo.user_org_assignments WHERE org_type_id = {keptTypeId}"),
                "Deleting one org type must not touch another.");
        }

        [TestMethod]
        public async Task GetSummaries_ReportsCountsAndTheLatestImport()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Counted"));
            await _types.CreateAsync(CsvType("Empty"));

            await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(a, typeId, "One"),
                new UserOrgAssignmentUpdate(b, typeId, "Two"),
            });

            var firstJob = await _jobs.CreateJobWithRowsAsync(
                new UserOrgImportJob { OrgTypeId = typeId, Mode = UserOrgImportMode.Merge, StartedBy = "admin" },
                new UserOrgStagedRow[0]);

            // Finished before the next one is queued: a job that is still pending or running now blocks
            // a second import for the same org type, which is the point of that guard.
            await _jobs.CompleteJobAsync(firstJob, UserOrgImportStatus.Succeeded, null);

            var latestJob = await _jobs.CreateJobWithRowsAsync(
                new UserOrgImportJob { OrgTypeId = typeId, Mode = UserOrgImportMode.Replace, StartedBy = "admin" },
                new UserOrgStagedRow[0]);

            var summaries = await _types.GetSummariesAsync();

            var counted = summaries.Single(s => s.Type.Name == "Counted");
            Assert.AreEqual(2, counted.AssignedUserCount);
            Assert.AreEqual(2, counted.DistinctValueCount);
            Assert.AreEqual(latestJob, counted.LastImport.Id, "The most recent job should win.");
            Assert.AreNotEqual(firstJob, counted.LastImport.Id);

            var empty = summaries.Single(s => s.Type.Name == "Empty");
            Assert.AreEqual(0, empty.AssignedUserCount);
            Assert.AreEqual(0, empty.DistinctValueCount);
            Assert.IsNull(empty.LastImport, "A type that has never been imported has no last import.");
        }

        #endregion

        #region Assignment merge

        [TestMethod]
        public async Task Merge_CreatesValuesAndAssignsThem()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            var result = await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(a, typeId, "Retail"),
                new UserOrgAssignmentUpdate(b, typeId, "Retail"),
            });

            Assert.AreEqual(2, result.Applied);
            Assert.AreEqual(1, result.ValuesCreated, "One distinct value shared by two users.");
            Assert.AreEqual(0, result.Cleared);
            Assert.AreEqual(2, Count("SELECT COUNT(*) FROM dbo.user_org_assignments"));
        }

        [TestMethod]
        public async Task Merge_IsIdempotentAndDoesNotDuplicateValues()
        {
            var a = AddUser("a@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var update = new[] { new UserOrgAssignmentUpdate(a, typeId, "Retail") };

            await _assignments.MergeAsync(update);
            var second = await _assignments.MergeAsync(update);

            Assert.AreEqual(0, second.Applied, "Re-applying an unchanged value should be a no-op.");
            Assert.AreEqual(0, second.ValuesCreated);
            Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.user_org_values"));
        }

        [TestMethod]
        public async Task Merge_RepointsAChangedValue()
        {
            var a = AddUser("a@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, "Retail") });
            var result = await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, "Wholesale") });

            Assert.AreEqual(1, result.Applied);
            Assert.AreEqual(
                1,
                Count("SELECT COUNT(*) FROM dbo.user_org_assignments"),
                "A user still has exactly one value per org type.");

            var values = await _assignments.GetForUserAsync(a);
            Assert.AreEqual("Wholesale", values.Single().Value);
        }

        [TestMethod]
        public async Task Merge_NullValueClearsTheAssignment()
        {
            var a = AddUser("a@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, "Retail") });
            var result = await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, null) });

            Assert.AreEqual(1, result.Cleared);
            Assert.AreEqual(0, (await _assignments.GetForUserAsync(a)).Count);
        }

        [TestMethod]
        public async Task Merge_LeavesUsersAbsentFromTheBatchAlone()
        {
            // This is the single most important property of the merge. /users/delta only returns the
            // users that changed, so if absence meant "no value" a routine delta cycle would wipe the
            // org values of the entire tenant.
            var changed = AddUser("changed@contoso.com");
            var untouched = AddUser("untouched@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(changed, typeId, "Before"),
                new UserOrgAssignmentUpdate(untouched, typeId, "Kept"),
            });

            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(changed, typeId, "After") });

            Assert.AreEqual("After", (await _assignments.GetForUserAsync(changed)).Single().Value);
            Assert.AreEqual(
                "Kept",
                (await _assignments.GetForUserAsync(untouched)).Single().Value,
                "A user missing from the batch must keep their value.");
        }

        [TestMethod]
        public async Task Merge_CollapsesDuplicateSlotsAndReportsThem()
        {
            var a = AddUser("a@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            var result = await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(a, typeId, "First"),
                new UserOrgAssignmentUpdate(a, typeId, "Second"),
            });

            Assert.AreEqual(1, result.DuplicatesCollapsed);
            Assert.AreEqual("Second", (await _assignments.GetForUserAsync(a)).Single().Value);
        }

        [TestMethod]
        public async Task Merge_KeepsDifferentOrgTypesIndependent()
        {
            var a = AddUser("a@contoso.com");
            var costCentre = await _types.CreateAsync(CsvType("Cost Centre"));
            var businessUnit = await _types.CreateAsync(CsvType("Business Unit"));

            await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(a, costCentre, "CC-1"),
                new UserOrgAssignmentUpdate(a, businessUnit, "Retail"),
            });

            var values = await _assignments.GetForUserAsync(a);

            Assert.AreEqual(2, values.Count, "A user can be in one org per type, across many types.");
            CollectionAssert.AreEqual(
                new[] { "Business Unit", "Cost Centre" },
                values.Select(v => v.OrgTypeName).ToArray(),
                "Ordered by org type name.");
        }

        [TestMethod]
        public async Task Merge_PreservesNonLatinOrgNames()
        {
            var a = AddUser("a@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, GreekOrgName) });

            Assert.AreEqual(
                GreekOrgName,
                (await _assignments.GetForUserAsync(a)).Single().Value,
                "Org values are nvarchar precisely so a non-Latin name survives storage.");
        }

        [TestMethod]
        public async Task Merge_SkipsAUserDeletedMidImportRatherThanFailingTheBatch()
        {
            // A user removed between the caller building the batch and the merge running is not worth
            // failing a 200,000-row import over.
            var present = AddUser("present@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            var result = await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(present, typeId, "Fine"),
                new UserOrgAssignmentUpdate(999999, typeId, "Vanished"),
            });

            Assert.AreEqual(1, result.Applied);
            Assert.AreEqual("Fine", (await _assignments.GetForUserAsync(present)).Single().Value);
        }

        [TestMethod]
        public async Task Merge_TruncatesAnOverLongValueRatherThanFailing()
        {
            var a = AddUser("a@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var tooLong = new string('x', UserOrgRules.MaxOrgValueLength + 25);

            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, tooLong) });

            Assert.AreEqual(
                UserOrgRules.MaxOrgValueLength,
                (await _assignments.GetForUserAsync(a)).Single().Value.Length);
        }

        [TestMethod]
        public async Task Merge_OfAnEmptyBatchDoesNothing()
        {
            var result = await _assignments.MergeAsync(new UserOrgAssignmentUpdate[0]);

            Assert.AreEqual(0, result.Applied);
            Assert.AreEqual(0, result.Cleared);
            Assert.AreEqual(0, result.ValuesCreated);
        }

        [TestMethod]
        public async Task ClearAllForType_RemovesOnlyThatTypesAssignments()
        {
            var a = AddUser("a@contoso.com");
            var one = await _types.CreateAsync(CsvType("One"));
            var two = await _types.CreateAsync(CsvType("Two"));

            await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(a, one, "X"),
                new UserOrgAssignmentUpdate(a, two, "Y"),
            });

            var cleared = await _assignments.ClearAllForTypeAsync(one);

            Assert.AreEqual(1, cleared);
            Assert.AreEqual("Y", (await _assignments.GetForUserAsync(a)).Single().Value);
        }

        #endregion

        #region CSV import jobs

        private async Task<int> QueueJob(int typeId, UserOrgImportMode mode, params UserOrgStagedRow[] rows)
        {
            return await _jobs.CreateJobWithRowsAsync(
                new UserOrgImportJob
                {
                    OrgTypeId = typeId,
                    Mode = mode,
                    StartedBy = "admin@contoso.com",
                    FileName = "orgs.csv",
                },
                rows);
        }

        [TestMethod]
        public async Task CreateJob_StagesItsRowsAndStartsPending()
        {
            var typeId = await _types.CreateAsync(CsvType("Team"));

            var jobId = await QueueJob(
                typeId,
                UserOrgImportMode.Merge,
                new UserOrgStagedRow(1, "a@contoso.com", "Retail"),
                new UserOrgStagedRow(2, "b@contoso.com", "Wholesale"));

            var job = await _jobs.GetJobAsync(jobId);

            Assert.AreEqual(UserOrgImportStatus.Pending, job.Status);
            Assert.AreEqual(2, job.RowsTotal);
            Assert.AreEqual("orgs.csv", job.FileName);
            Assert.AreEqual(2, Count($"SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = {jobId}"));
        }

        [TestMethod]
        public async Task TryClaimJob_SucceedsOnceAndThenRefuses()
        {
            // Two web instances polling the same pending job must not both import the file.
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var jobId = await QueueJob(typeId, UserOrgImportMode.Merge);

            Assert.IsTrue(await _jobs.TryClaimJobAsync(jobId));
            Assert.IsFalse(await _jobs.TryClaimJobAsync(jobId), "A claimed job must not be claimable again.");

            var job = await _jobs.GetJobAsync(jobId);
            Assert.AreEqual(UserOrgImportStatus.Running, job.Status);
            Assert.IsNotNull(job.StartedUtc);
            Assert.IsNotNull(job.HeartbeatUtc);
        }

        [TestMethod]
        public async Task Apply_MergeMode_OnlyTouchesTheUsersInTheFile()
        {
            var inFile = AddUser("infile@contoso.com");
            var notInFile = AddUser("notinfile@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(inFile, typeId, "Old"),
                new UserOrgAssignmentUpdate(notInFile, typeId, "Untouched"),
            });

            var jobId = await QueueJob(typeId, UserOrgImportMode.Merge, new UserOrgStagedRow(1, "infile@contoso.com", "New"));
            await _jobs.TryClaimJobAsync(jobId);
            var job = await _jobs.ApplyAsync(jobId);

            Assert.AreEqual(1, job.RowsApplied);
            Assert.AreEqual(0, job.RowsCleared);
            Assert.AreEqual("New", (await _assignments.GetForUserAsync(inFile)).Single().Value);
            Assert.AreEqual(
                "Untouched",
                (await _assignments.GetForUserAsync(notInFile)).Single().Value,
                "Merge must leave users the file does not mention alone.");
        }

        [TestMethod]
        public async Task Apply_ReplaceMode_ClearsUsersMissingFromTheFile()
        {
            var inFile = AddUser("infile@contoso.com");
            var notInFile = AddUser("notinfile@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(inFile, typeId, "Old"),
                new UserOrgAssignmentUpdate(notInFile, typeId, "Will be cleared"),
            });

            var jobId = await QueueJob(typeId, UserOrgImportMode.Replace, new UserOrgStagedRow(1, "infile@contoso.com", "New"));
            await _jobs.TryClaimJobAsync(jobId);
            var job = await _jobs.ApplyAsync(jobId);

            Assert.AreEqual(1, job.RowsApplied);
            Assert.AreEqual(1, job.RowsCleared);
            Assert.AreEqual("New", (await _assignments.GetForUserAsync(inFile)).Single().Value);
            Assert.AreEqual(
                0,
                (await _assignments.GetForUserAsync(notInFile)).Count,
                "Replace treats the file as the complete membership of the org type.");
        }

        [TestMethod]
        public async Task Apply_ReplaceMode_DoesNotTouchOtherOrgTypes()
        {
            var user = AddUser("a@contoso.com");
            var replaced = await _types.CreateAsync(CsvType("Replaced"));
            var other = await _types.CreateAsync(CsvType("Other"));

            await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(user, replaced, "Gone"),
                new UserOrgAssignmentUpdate(user, other, "Safe"),
            });

            var jobId = await QueueJob(replaced, UserOrgImportMode.Replace);
            await _jobs.TryClaimJobAsync(jobId);
            await _jobs.ApplyAsync(jobId);

            var values = await _assignments.GetForUserAsync(user);
            Assert.AreEqual("Safe", values.Single().Value);
        }

        [TestMethod]
        public async Task Apply_BlankValueClearsInBothModes()
        {
            foreach (var mode in new[] { UserOrgImportMode.Merge, UserOrgImportMode.Replace })
            {
                Reset();
                var user = AddUser("a@contoso.com");
                var typeId = await _types.CreateAsync(CsvType("Team"));
                await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(user, typeId, "Before") });

                var jobId = await QueueJob(typeId, mode, new UserOrgStagedRow(1, "a@contoso.com", "   "));
                await _jobs.TryClaimJobAsync(jobId);
                var job = await _jobs.ApplyAsync(jobId);

                Assert.AreEqual(1, job.RowsCleared, $"{mode}: a blank value should clear.");
                Assert.AreEqual(0, (await _assignments.GetForUserAsync(user)).Count, mode.ToString());
            }
        }

        [TestMethod]
        public async Task Apply_CountsUnknownUpnsWithoutFailing()
        {
            var known = AddUser("known@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            var jobId = await QueueJob(
                typeId,
                UserOrgImportMode.Merge,
                new UserOrgStagedRow(1, "known@contoso.com", "Retail"),
                new UserOrgStagedRow(2, "ghost@contoso.com", "Nowhere"),
                new UserOrgStagedRow(3, "another-ghost@contoso.com", "Nowhere"));

            await _jobs.TryClaimJobAsync(jobId);
            var job = await _jobs.ApplyAsync(jobId);

            Assert.AreEqual(2, job.RowsUnknownUpn);
            Assert.AreEqual(1, job.RowsApplied);
            Assert.AreEqual("Retail", (await _assignments.GetForUserAsync(known)).Single().Value);
        }

        [TestMethod]
        public async Task Apply_MatchesUpnsCaseInsensitivelyAndTakesTheLastDuplicateLine()
        {
            var user = AddUser("Person@Contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            var jobId = await QueueJob(
                typeId,
                UserOrgImportMode.Merge,
                new UserOrgStagedRow(1, "person@contoso.com", "First"),
                new UserOrgStagedRow(2, "PERSON@CONTOSO.COM", "Second"));

            await _jobs.TryClaimJobAsync(jobId);
            var job = await _jobs.ApplyAsync(jobId);

            Assert.AreEqual(0, job.RowsUnknownUpn, "UPN matching follows the database's case-insensitive collation.");
            Assert.AreEqual(
                "Second",
                (await _assignments.GetForUserAsync(user)).Single().Value,
                "A person listed twice is treated as a correction - the later line wins.");
        }

        [TestMethod]
        public async Task Apply_PreservesNonLatinOrgNamesFromAFile()
        {
            var user = AddUser("a@contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));

            var jobId = await QueueJob(typeId, UserOrgImportMode.Merge, new UserOrgStagedRow(1, "a@contoso.com", GreekOrgName));
            await _jobs.TryClaimJobAsync(jobId);
            await _jobs.ApplyAsync(jobId);

            Assert.AreEqual(GreekOrgName, (await _assignments.GetForUserAsync(user)).Single().Value);
        }

        [TestMethod]
        public async Task CompleteJob_RecordsTheOutcomeAndDiscardsStagedRows()
        {
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var jobId = await QueueJob(typeId, UserOrgImportMode.Merge, new UserOrgStagedRow(1, "a@contoso.com", "X"));
            await _jobs.TryClaimJobAsync(jobId);

            await _jobs.CompleteJobAsync(jobId, UserOrgImportStatus.Succeeded, null);

            var job = await _jobs.GetJobAsync(jobId);
            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status);
            Assert.IsNotNull(job.FinishedUtc);
            Assert.AreEqual(
                0,
                Count($"SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = {jobId}"),
                "Staged rows are a work queue, not a permanent copy of the customer's file.");
        }

        [TestMethod]
        public async Task CompleteJob_TruncatesAnOverLongErrorInsteadOfFailing()
        {
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var jobId = await QueueJob(typeId, UserOrgImportMode.Merge);

            await _jobs.CompleteJobAsync(jobId, UserOrgImportStatus.Failed, new string('e', 5000));

            var job = await _jobs.GetJobAsync(jobId);
            Assert.AreEqual(UserOrgImportStatus.Failed, job.Status);
            Assert.AreEqual(2000, job.ErrorMessage.Length);
        }

        [TestMethod]
        public async Task CompleteJob_CannotRewriteAJobThatHasAlreadyFinished()
        {
            // A worker that was superseded because it stopped reporting progress must not be able to
            // report its own outcome over the top of that verdict. Without this a job declared dead -
            // and whose replacement has already been admitted and may already have run - could still
            // flip itself to Succeeded, leaving the portal claiming two finished imports for the same
            // org type with no way to tell which one the data came from.
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var jobId = await QueueJob(typeId, UserOrgImportMode.Merge);

            await _jobs.CompleteJobAsync(jobId, UserOrgImportStatus.Failed, "the first verdict");
            await _jobs.CompleteJobAsync(jobId, UserOrgImportStatus.Succeeded, null);

            var job = await _jobs.GetJobAsync(jobId);
            Assert.AreEqual(UserOrgImportStatus.Failed, job.Status);
            Assert.AreEqual("the first verdict", job.ErrorMessage);
        }

        [TestMethod]
        public async Task QueueingOverAStaleJobRetiresItRatherThanLeavingTwoLive()
        {
            // The guard lets a new import through once the previous one has gone quiet for longer than
            // StaleHeartbeatThreshold. Leaving that one Pending would let a late dispatch still claim
            // and apply a file the admin has already superseded, because TryClaimJobAsync accepts any
            // job that is still Pending. Moving it off Pending here is what actually fences that.
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var stale = await QueueJob(typeId, UserOrgImportMode.Replace, new UserOrgStagedRow(1, "a@contoso.com", "X"));

            Execute(
                "UPDATE dbo.user_org_import_jobs SET queued_utc = DATEADD(MINUTE, -30, SYSUTCDATETIME()) "
                + $"WHERE id = {stale}");

            var replacement = await QueueJob(typeId, UserOrgImportMode.Merge, new UserOrgStagedRow(1, "a@contoso.com", "Y"));

            var staleJob = await _jobs.GetJobAsync(stale);
            Assert.AreEqual(UserOrgImportStatus.Failed, staleJob.Status, "The abandoned job must not stay claimable.");
            StringAssert.Contains(staleJob.ErrorMessage, "overtaken");
            Assert.IsFalse(
                await _jobs.TryClaimJobAsync(stale),
                "A superseded job must not be claimable by a late dispatch.");

            Assert.AreEqual(
                0,
                Count($"SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = {stale}"),
                "The superseded job's staged rows are dead weight and must go with it.");
            Assert.AreEqual(
                1,
                Count($"SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = {replacement}"),
                "The replacement's own rows must survive.");
        }

        [TestMethod]
        public async Task FindAssignedUpns_ReportsOnlyUsersWhoHoldAValueForThatType()
        {
            // The Replace preflight depends on this. Counting the users a file COVERS instead gives the
            // wrong answer precisely when it matters: a file covering a large population that barely
            // overlaps the assigned one would subtract to zero and suppress the warning at the moment
            // it is about to wipe everybody.
            var assigned = AddUser("assigned@contoso.com");
            AddUser("exists-but-unassigned@contoso.com");
            var otherType = AddUser("other-type@contoso.com");

            var typeId = await _types.CreateAsync(CsvType("Team"));
            var second = await _types.CreateAsync(CsvType("Other"));

            await _assignments.MergeAsync(new[]
            {
                new UserOrgAssignmentUpdate(assigned, typeId, "Retail"),
                new UserOrgAssignmentUpdate(otherType, second, "Elsewhere"),
            });

            var lookup = UserOrgStores.CreateUserLookup(_db.ConnectionString);

            var found = await lookup.FindAssignedUpnsAsync(typeId, new[]
            {
                "assigned@contoso.com",
                "exists-but-unassigned@contoso.com",
                "other-type@contoso.com",
                "ghost@contoso.com",
            });

            CollectionAssert.AreEqual(
                new[] { "assigned@contoso.com" },
                found.ToArray(),
                "Only a user who holds a value for THIS org type counts as kept.");
        }

        [TestMethod]
        public async Task FindAssignedUpns_MatchesCaseInsensitively()
        {
            var userId = AddUser("Person@Contoso.com");
            var typeId = await _types.CreateAsync(CsvType("Team"));
            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(userId, typeId, "Retail") });

            var lookup = UserOrgStores.CreateUserLookup(_db.ConnectionString);
            var found = await lookup.FindAssignedUpnsAsync(typeId, new[] { "person@contoso.com" });

            Assert.AreEqual(1, found.Count);
        }

        [TestMethod]
        public async Task FindAssignedUpns_HandlesAnEmptyRequest()
        {
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var lookup = UserOrgStores.CreateUserLookup(_db.ConnectionString);

            Assert.AreEqual(0, (await lookup.FindAssignedUpnsAsync(typeId, new string[0])).Count);
            Assert.AreEqual(0, (await lookup.FindAssignedUpnsAsync(typeId, null)).Count);
        }

        [TestMethod]
        public async Task QueueingASecondJobForTheSameTypeIsRefusedAtomically()
        {
            // The check and the insert happen in one transaction holding a range lock, because the
            // caller's pre-check had the whole CSV parse between it and the insert - a window wide
            // enough for two admins uploading at once to both see no active job and queue competing
            // imports for the same org type.
            var typeId = await _types.CreateAsync(CsvType("Team"));
            await QueueJob(typeId, UserOrgImportMode.Merge);

            try
            {
                await QueueJob(typeId, UserOrgImportMode.Replace);
                Assert.Fail("A second concurrent import for the same org type must be refused.");
            }
            catch (UserOrgValidationException ex)
            {
                StringAssert.Contains(ex.Message, "already in progress");
            }

            Assert.AreEqual(
                1,
                Count($"SELECT COUNT(*) FROM dbo.user_org_import_jobs WHERE org_type_id = {typeId}"),
                "The refused job must not have been created.");
        }

        [TestMethod]
        public async Task AnotherOrgTypeCanBeImportedAtTheSameTime()
        {
            var one = await _types.CreateAsync(CsvType("One"));
            var two = await _types.CreateAsync(CsvType("Two"));

            await QueueJob(one, UserOrgImportMode.Merge);
            await QueueJob(two, UserOrgImportMode.Merge);

            Assert.AreEqual(2, Count("SELECT COUNT(*) FROM dbo.user_org_import_jobs"));
        }

        [TestMethod]
        public async Task AStrandedPendingJobDoesNotBlockLaterImportsForever()
        {
            // The rows are staged and the job committed before the worker is dispatched, so a recycle
            // in that window leaves a job nobody will ever claim. Because pending counts as active, it
            // would otherwise block every later upload for this org type permanently.
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var stranded = await QueueJob(typeId, UserOrgImportMode.Merge);

            _db.Execute(
                "UPDATE dbo.user_org_import_jobs SET queued_utc = DATEADD(HOUR, -2, SYSUTCDATETIME()) "
                + $"WHERE id = {stranded};");

            var replacement = await QueueJob(typeId, UserOrgImportMode.Merge);

            Assert.AreNotEqual(stranded, replacement, "A new import must be allowed once the old one is clearly dead.");
        }

        [TestMethod]
        public async Task GetActiveJobForType_FindsPendingAndRunningOnly()
        {
            var typeId = await _types.CreateAsync(CsvType("Team"));

            Assert.IsNull(await _jobs.GetActiveJobForTypeAsync(typeId));

            var jobId = await QueueJob(typeId, UserOrgImportMode.Merge);
            Assert.AreEqual(jobId, (await _jobs.GetActiveJobForTypeAsync(typeId)).Id, "Pending counts as active.");

            await _jobs.TryClaimJobAsync(jobId);
            Assert.AreEqual(jobId, (await _jobs.GetActiveJobForTypeAsync(typeId)).Id, "Running counts as active.");

            await _jobs.CompleteJobAsync(jobId, UserOrgImportStatus.Succeeded, null);
            Assert.IsNull(await _jobs.GetActiveJobForTypeAsync(typeId), "A finished job is not active.");
        }

        [TestMethod]
        public async Task Heartbeat_MovesOnlyWhileRunning()
        {
            var typeId = await _types.CreateAsync(CsvType("Team"));
            var jobId = await QueueJob(typeId, UserOrgImportMode.Merge);

            await _jobs.HeartbeatAsync(jobId);
            Assert.IsNull(
                (await _jobs.GetJobAsync(jobId)).HeartbeatUtc,
                "A pending job has not started, so it has no heartbeat.");

            await _jobs.TryClaimJobAsync(jobId);
            var afterClaim = (await _jobs.GetJobAsync(jobId)).HeartbeatUtc;
            Assert.IsNotNull(afterClaim);

            await Task.Delay(30);
            await _jobs.HeartbeatAsync(jobId);
            Assert.IsTrue(
                (await _jobs.GetJobAsync(jobId)).HeartbeatUtc >= afterClaim,
                "A running job's heartbeat should move forward.");
        }

        #endregion
    }
}
