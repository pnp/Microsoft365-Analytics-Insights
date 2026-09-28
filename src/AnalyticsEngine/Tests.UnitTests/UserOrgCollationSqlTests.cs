using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The user organisation SQL on a database whose collation is not the server's.
    /// </summary>
    /// <remarks>
    /// A temp table's text columns take tempdb's collation - the server's - unless told otherwise, and
    /// comparing one with a column of a database created under another collation fails with Msg 468,
    /// "Cannot resolve the collation conflict". That is an ordinary state for a restored or migrated
    /// database, and a customer's own choice on SQL Server or Managed Instance. Every other test runs on
    /// a database with the server's collation, where the defect cannot show, so these tests make the
    /// two differ on purpose - and check that they do, or they would pass without proving anything.
    /// </remarks>
    [TestClass]
    public class UserOrgCollationSqlTests
    {
        private const string Admin = "admin@contoso.com";
        private const string GreekValue = "Καλημέρα κόσμε";

        private static ScratchDatabase _db;
        private static string _collation;
        private static IUserOrgTypeStore _types;
        private static IUserOrgAssignmentStore _assignments;
        private static IUserOrgImportJobStore _jobs;
        private static IUserOrgChangeOutbox _outbox;
        private static IUserOrgUserLookup _users;

        [ClassInitialize]
        public static void ClassInit(TestContext context)
        {
            _collation = ScratchDatabase.CollationUnlikeTheServers();
            _db = UserOrgImportTestSchema.Create("userorgcollation", _collation);
            _types = UserOrgStores.CreateTypeStore(_db.ConnectionString);
            _assignments = UserOrgStores.CreateAssignmentStore(_db.ConnectionString);
            _jobs = UserOrgStores.CreateImportJobStore(_db.ConnectionString);
            _outbox = UserOrgStores.CreateChangeOutbox(_db.ConnectionString);
            _users = UserOrgStores.CreateUserLookup(_db.ConnectionString);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _db?.Dispose();
        }

        [TestInitialize]
        public void Reset()
        {
            _db.Execute("DELETE FROM dbo.user_org_import_changes;");
            UserOrgImportTestSchema.Reset(_db);
        }

        private static int AddUser(string upn) => UserOrgImportTestSchema.AddUser(_db, upn);

        [TestMethod]
        public void TheDatabaseReallyIsCollatedUnlikeTheServer()
        {
            var database = (string)_db.Scalar("SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));");
            var server = (string)_db.Scalar("SELECT CONVERT(nvarchar(128), SERVERPROPERTY('Collation'));");

            Assert.AreEqual(_collation, database);
            Assert.AreNotEqual(server, database, "Without the mismatch these tests prove nothing.");
        }

        [TestMethod]
        public async Task TheEntraMergeWorks()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var typeId = await _types.CreateAsync(new UserOrgType
            {
                Name = "Cost Centre",
                SourceKind = UserOrgSourceKind.EntraAttribute,
                EntraAttributeName = "extensionAttribute1",
                IsEnabled = true,
            });
            var generation = (await _types.GetAsync(typeId)).SourceGeneration;

            await _assignments.MergeAsync(
                new[] { new UserOrgAssignmentUpdate(a, typeId, "CC-100"), new UserOrgAssignmentUpdate(b, typeId, GreekValue) },
                UserOrgSourceKind.EntraAttribute,
                new System.Collections.Generic.Dictionary<int, int> { { typeId, generation } });
            await _assignments.MergeAsync(
                new[] { new UserOrgAssignmentUpdate(a, typeId, "CC-200"), new UserOrgAssignmentUpdate(b, typeId, null) });

            Assert.AreEqual("CC-200", (await _assignments.GetForUserAsync(a)).Single().Value);
            Assert.AreEqual(0, (await _assignments.GetForUserAsync(b)).Count);
        }

        [TestMethod]
        public async Task ACsvImportPreviewsAndApplies()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var c = AddUser("c@contoso.com");
            var typeId = await _types.CreateAsync(UserOrgImportTestSchema.CsvType("Team"));
            // Written directly rather than through the Entra merge, so this test stands or falls on the
            // CSV path's own SQL.
            _db.Execute($@"
INSERT INTO dbo.user_org_values (org_type_id, name) VALUES ({typeId}, N'Old'), ({typeId}, N'Gone');
INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id)
SELECT {a}, {typeId}, id FROM dbo.user_org_values WHERE org_type_id = {typeId} AND name = N'Old'
UNION ALL
SELECT {c}, {typeId}, id FROM dbo.user_org_values WHERE org_type_id = {typeId} AND name = N'Gone';");

            var type = await _types.GetAsync(typeId);
            var draftId = await _jobs.CreateDraftAsync(
                new UserOrgImportJob { OrgTypeId = typeId, StartedBy = Admin, ExpectedGeneration = type.SourceGeneration },
                new[]
                {
                    new UserOrgStagedRow(2, "a@contoso.com", "New"),
                    new UserOrgStagedRow(3, "b@contoso.com", GreekValue),
                    new UserOrgStagedRow(4, "nobody@contoso.com", "X"),
                });

            var summary = await _jobs.SummariseDraftAsync(draftId, 10, 10);
            Assert.AreEqual(3, summary.RowsTotal);
            Assert.AreEqual(1, summary.UnknownRows);

            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Replace, int.MaxValue, Admin);
            Assert.IsTrue(await _jobs.TryClaimJobAsync(draftId));
            var job = await _jobs.ApplyAsync(draftId);

            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status);
            Assert.AreEqual("New", (await _assignments.GetForUserAsync(a)).Single().Value);
            Assert.AreEqual(GreekValue, (await _assignments.GetForUserAsync(b)).Single().Value);
            Assert.AreEqual(0, (await _assignments.GetForUserAsync(c)).Count, "Replace clears the user the file leaves out.");

            var changes = (await _outbox.ReadAsync(draftId, 0, 100)).ToDictionary(x => x.Upn);
            Assert.AreEqual(3, changes.Count);
            Assert.AreEqual("Old", changes["a@contoso.com"].OldValue);
            Assert.AreEqual(GreekValue, changes["b@contoso.com"].NewValue);
            Assert.AreEqual(UserOrgChangeKind.Cleared, changes["c@contoso.com"].Kind);
        }

        [TestMethod]
        public async Task TheUpnProbesWork()
        {
            var a = AddUser("a@contoso.com");
            AddUser("b@contoso.com");
            var typeId = await _types.CreateAsync(UserOrgImportTestSchema.CsvType("Team"));
            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, "X") });

            var upns = new[] { "A@contoso.com", "b@contoso.com", "nobody@contoso.com" };

            CollectionAssert.AreEquivalent(
                new[] { "A@contoso.com", "b@contoso.com" },
                (await _users.FindExistingUpnsAsync(upns)).ToArray(),
                "Matched in the database's own case-insensitive collation.");
            CollectionAssert.AreEquivalent(new[] { "A@contoso.com" }, (await _users.FindAssignedUpnsAsync(typeId, upns)).ToArray());
        }
    }
}
