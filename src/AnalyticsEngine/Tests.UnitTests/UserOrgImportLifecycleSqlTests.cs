extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserOrgs;
using Common.Entities.Migrations;
using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>Builds the schema the CSV import lifecycle tests run against.</summary>
    internal static class UserOrgImportTestSchema
    {
        /// <summary>
        /// A scratch database with production's <c>dbo.users</c> shape - <c>user_name varchar(250)</c>,
        /// because an Entra UPN is ASCII by policy - and the migration's own schema.
        /// </summary>
        /// <param name="collation">The database's collation, or <c>null</c> for the server's.</param>
        public static ScratchDatabase Create(string purpose, string collation = null)
        {
            var db = ScratchDatabase.Create(purpose, collation);
            db.Execute(@"
CREATE TABLE dbo.user_departments (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_user_departments PRIMARY KEY CLUSTERED,
    name nvarchar(100) NULL
);
CREATE TABLE dbo.user_job_titles (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_user_job_titles PRIMARY KEY CLUSTERED,
    name nvarchar(100) NULL
);
CREATE TABLE dbo.users (
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_users PRIMARY KEY CLUSTERED,
    user_name varchar(250) NOT NULL,
    account_enabled bit NULL,
    department_id int NULL CONSTRAINT FK_users_department REFERENCES dbo.user_departments (id),
    job_title_id int NULL CONSTRAINT FK_users_job_title REFERENCES dbo.user_job_titles (id)
);");
            db.Execute(UserOrganisations.Up_Sql);
            return db;
        }

        public static void Reset(ScratchDatabase db)
        {
            db.Execute(@"
DELETE FROM dbo.user_org_import_staging;
DELETE FROM dbo.user_org_import_jobs;
DELETE FROM dbo.user_org_assignments;
DELETE FROM dbo.user_org_values;
DELETE FROM dbo.user_org_types;
DELETE FROM dbo.users;");
        }

        public static int AddUser(ScratchDatabase db, string upn)
        {
            return Convert.ToInt32(db.Scalar(
                $"INSERT INTO dbo.users (user_name) OUTPUT INSERTED.id VALUES ('{upn.Replace("'", "''")}');"));
        }

        public static UserOrgType CsvType(string name)
        {
            return new UserOrgType { Name = name, SourceKind = UserOrgSourceKind.CsvUpload, IsEnabled = true };
        }
    }

    /// <summary>
    /// The staged-preview, commit, resume and history lifecycle of a CSV import, against real SQL Server.
    /// </summary>
    [TestClass]
    public class UserOrgImportLifecycleSqlTests
    {
        private const string Admin = "admin@contoso.com";

        private static ScratchDatabase _db;
        private static IUserOrgTypeStore _types;
        private static IUserOrgAssignmentStore _assignments;
        private static IUserOrgImportJobStore _jobs;
        private static IUserOrgChangeOutbox _outbox;

        [ClassInitialize]
        public static void ClassInit(TestContext context)
        {
            _db = UserOrgImportTestSchema.Create("userorglifecycle");
            _types = UserOrgStores.CreateTypeStore(_db.ConnectionString);
            _assignments = UserOrgStores.CreateAssignmentStore(_db.ConnectionString);
            _jobs = UserOrgStores.CreateImportJobStore(_db.ConnectionString);
            _outbox = UserOrgStores.CreateChangeOutbox(_db.ConnectionString);
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _db?.Dispose();
        }

        [TestInitialize]
        public void Reset()
        {
            UserOrgImportTestSchema.Reset(_db);
        }

        private static int AddUser(string upn) => UserOrgImportTestSchema.AddUser(_db, upn);

        private static int Count(string sql) => Convert.ToInt32(_db.Scalar(sql));

        private static async Task<int> NewType(string name = "Team")
        {
            return await _types.CreateAsync(UserOrgImportTestSchema.CsvType(name));
        }

        private static async Task<int> Draft(int typeId, string startedBy, params UserOrgStagedRow[] rows)
        {
            var type = await _types.GetAsync(typeId);
            return await _jobs.CreateDraftAsync(
                new UserOrgImportJob
                {
                    OrgTypeId = typeId,
                    FileName = "orgs.csv",
                    StartedBy = startedBy,
                    ExpectedGeneration = type.SourceGeneration,
                },
                rows);
        }

        private static async Task Assign(int typeId, params (int User, string Value)[] assignments)
        {
            await _assignments.MergeAsync(assignments.Select(a => new UserOrgAssignmentUpdate(a.User, typeId, a.Value)).ToList());
        }

        private static async Task<UserOrgValidationException> CommitRefused(
            int draftId, int typeId, UserOrgImportMode mode, int confirmed)
        {
            try
            {
                await _jobs.CommitDraftAsync(draftId, typeId, mode, confirmed, Admin);
            }
            catch (UserOrgValidationException ex)
            {
                return ex;
            }

            Assert.Fail("The commit should have been refused.");
            return null;
        }

        private static async Task<string> ValueOf(int user)
        {
            var values = await _assignments.GetForUserAsync(user);
            return values.SingleOrDefault()?.Value;
        }

        #region Drafts

        [TestMethod]
        public async Task ADraftIsStagedButIsNotAnImport()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();

            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "Retail"));

            Assert.AreEqual(UserOrgImportStatus.Draft, (await _jobs.GetJobAsync(draftId)).Status);
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = {draftId}"));
            Assert.IsNull(await _jobs.GetActiveJobForTypeAsync(typeId), "A preview must not block anyone's import.");
            Assert.AreEqual(0, (await _jobs.ListJobsAsync(typeId, 10)).Count, "Nor appear in the import history.");
            Assert.IsNull((await _types.GetSummariesAsync()).Single().LastImport, "Nor be shown as the last import.");
            Assert.IsFalse(await _jobs.TryClaimJobAsync(draftId), "Nor ever be picked up by a worker.");
        }

        [TestMethod]
        public async Task TheDraftSummaryCountsWhatTheImportWouldDo()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var c = AddUser("c@contoso.com");
            AddUser("d@contoso.com");
            var typeId = await NewType();
            await Assign(typeId, (a, "X"), (b, "Y"), (c, "Z"));

            var draftId = await Draft(
                typeId,
                Admin,
                new UserOrgStagedRow(2, "a@contoso.com", "X2"),
                new UserOrgStagedRow(3, "B@contoso.com", null),
                new UserOrgStagedRow(4, "nobody@contoso.com", "W"),
                new UserOrgStagedRow(5, "a@contoso.com", "X3"),
                new UserOrgStagedRow(6, "d@contoso.com", "V"));

            var summary = await _jobs.SummariseDraftAsync(draftId, 10, 100);

            Assert.AreEqual(5, summary.RowsTotal);
            Assert.AreEqual(1, summary.UnknownRows);
            Assert.AreEqual(3, summary.MatchedUsers, "a, b and d - a counted once, case-insensitively.");
            Assert.AreEqual(2, summary.MatchedUsersWithValue, "a and d; b is listed to be cleared.");
            Assert.AreEqual(3, summary.CurrentlyAssigned);
            Assert.AreEqual(2, summary.ReplaceWouldClear, "b (blank) and c (absent).");
            Assert.AreEqual(1, summary.MergeWouldClear, "Only b, whom the file lists with an empty value.");
            CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 6 }, summary.SampleRows.Select(r => r.LineNumber).ToArray());
            CollectionAssert.AreEqual(new[] { true, true, false, true, true }, summary.SampleRows.Select(r => r.UserExists).ToArray());
            Assert.AreEqual(4, summary.UnknownRowList.Single().LineNumber);
            Assert.AreEqual("W", summary.UnknownRowList.Single().OrgValue);
        }

        [TestMethod]
        public async Task TheSummaryListsUnknownRowsUpToItsLimitButCountsThemAll()
        {
            var typeId = await NewType();
            var rows = Enumerable.Range(0, 25).Select(i => new UserOrgStagedRow(i + 2, $"nobody{i}@contoso.com", "X")).ToArray();
            var draftId = await Draft(typeId, Admin, rows);

            var summary = await _jobs.SummariseDraftAsync(draftId, 3, 10);

            Assert.AreEqual(25, summary.UnknownRows);
            Assert.AreEqual(10, summary.UnknownRowList.Count);
            Assert.AreEqual(3, summary.SampleRows.Count);
        }

        [TestMethod]
        public async Task PreviewingAgainReplacesTheSameAdminsEarlierDraft()
        {
            // Previewing a file five times must not leave five copies of every UPN in it.
            var typeId = await NewType();
            var first = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            var colleagues = await Draft(typeId, "colleague@contoso.com", new UserOrgStagedRow(2, "a@contoso.com", "Y"));

            var second = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "Z"));

            Assert.IsNull(await _jobs.GetJobAsync(first));
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = {first}"));
            Assert.IsNotNull(await _jobs.GetJobAsync(second));
            Assert.IsNotNull(await _jobs.GetJobAsync(colleagues), "Another admin's preview is theirs to keep.");
        }

        [TestMethod]
        public async Task AnOlderPreviewFinishingLastLeavesTheNewerDraftAlone()
        {
            // A large file still uploading when its admin cleared it and chose a small one: the small
            // one's draft is what the page shows, so the large one, finishing last, must not delete it.
            var typeId = await NewType();
            var type = await _types.GetAsync(typeId);
            var now = DateTime.UtcNow;
            Func<DateTime, Task<int>> draftAskedFor = asked => _jobs.CreateDraftAsync(
                new UserOrgImportJob { OrgTypeId = typeId, StartedBy = Admin, QueuedUtc = asked, ExpectedGeneration = type.SourceGeneration },
                new[] { new UserOrgStagedRow(2, "a@contoso.com", "X") });

            var small = await draftAskedFor(now);
            var large = await draftAskedFor(now.AddSeconds(-20));

            Assert.IsNotNull(await _jobs.GetJobAsync(small), "The draft on screen survives.");
            Assert.IsNotNull(await _jobs.GetJobAsync(large), "Left to expire, or to the next preview.");

            var next = await draftAskedFor(now.AddSeconds(1));

            Assert.IsNull(await _jobs.GetJobAsync(small));
            Assert.IsNull(await _jobs.GetJobAsync(large));
            Assert.IsNotNull(await _jobs.GetJobAsync(next));
        }

        [TestMethod]
        public async Task ExpiredDraftsAreThrownAway()
        {
            var typeId = await NewType();
            var old = await Draft(typeId, "colleague@contoso.com", new UserOrgStagedRow(2, "a@contoso.com", "X"));
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET queued_utc = DATEADD(HOUR, -3, SYSUTCDATETIME()) WHERE id = {old}");

            await Draft(typeId, Admin, new UserOrgStagedRow(2, "b@contoso.com", "Y"));

            Assert.IsNull(await _jobs.GetJobAsync(old));
        }

        #endregion

        #region Commit

        [TestMethod]
        public async Task CommittingQueuesTheDraftWithWhatWasConfirmed()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var typeId = await NewType();
            await Assign(typeId, (a, "X"), (b, "Y"));
            var draftId = await Draft(typeId, "previewer@contoso.com", new UserOrgStagedRow(2, "a@contoso.com", "X2"));
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET queued_utc = DATEADD(MINUTE, -20, SYSUTCDATETIME()) WHERE id = {draftId}");

            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Replace, 1, Admin);

            var job = await _jobs.GetJobAsync(draftId);
            Assert.AreEqual(UserOrgImportStatus.Pending, job.Status);
            Assert.AreEqual(UserOrgImportMode.Replace, job.Mode);
            Assert.AreEqual(1, job.ConfirmedClearCount);
            Assert.AreEqual(Admin, job.StartedBy, "Whoever imported it, not whoever previewed it.");
            Assert.IsTrue(DateTime.UtcNow - job.QueuedUtc < TimeSpan.FromMinutes(5), "Queued when it was imported, not when it was previewed.");
        }

        [TestMethod]
        public async Task ADraftCanOnlyBeCommittedOnce()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Merge, 0, Admin);

            var refusal = await CommitRefused(draftId, typeId, UserOrgImportMode.Merge, 0);

            Assert.AreEqual(UserOrgImportRefusalCodes.DraftNotFound, refusal.Code);
        }

        [TestMethod]
        public async Task AnExpiredDraftCannotBeCommitted()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET queued_utc = DATEADD(HOUR, -3, SYSUTCDATETIME()) WHERE id = {draftId}");

            Assert.AreEqual(UserOrgImportRefusalCodes.DraftNotFound, (await CommitRefused(draftId, typeId, UserOrgImportMode.Merge, 0)).Code);
        }

        [TestMethod]
        public async Task ADraftForAnotherTypeCannotBeCommittedToThisOne()
        {
            AddUser("a@contoso.com");
            var one = await NewType("One");
            var two = await NewType("Two");
            var draftId = await Draft(one, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));

            Assert.AreEqual(UserOrgImportRefusalCodes.DraftNotFound, (await CommitRefused(draftId, two, UserOrgImportMode.Merge, 0)).Code);
        }

        [TestMethod]
        public async Task ADraftIsRefusedIfItsTypeWasResetSinceThePreview()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            _db.Execute($"UPDATE dbo.user_org_types SET source_generation = source_generation + 1 WHERE id = {typeId}");

            Assert.AreEqual(UserOrgImportRefusalCodes.TypeChanged, (await CommitRefused(draftId, typeId, UserOrgImportMode.Merge, 0)).Code);
        }

        [TestMethod]
        public async Task ADraftIsRefusedWhileAnotherImportIsRunning()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var type = await _types.GetAsync(typeId);
            await _jobs.CreateJobWithRowsAsync(
                new UserOrgImportJob { OrgTypeId = typeId, Mode = UserOrgImportMode.Merge, StartedBy = "colleague@contoso.com", ExpectedGeneration = type.SourceGeneration },
                new[] { new UserOrgStagedRow(2, "a@contoso.com", "Y") });
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));

            Assert.AreEqual(UserOrgImportRefusalCodes.ImportInProgress, (await CommitRefused(draftId, typeId, UserOrgImportMode.Merge, 0)).Code);
            Assert.AreEqual(UserOrgImportStatus.Draft, (await _jobs.GetJobAsync(draftId)).Status, "Still there to import once the other finishes.");
        }

        [TestMethod]
        public async Task AFileNamingNobodyIsRefusedRatherThanReportedAsASuccess()
        {
            // It changes nobody - except that a Replace of it clears everybody. Either way it is not the
            // file the admin meant, and a green "succeeded" would say otherwise.
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "nobody@contoso.com", "X"));

            Assert.AreEqual(UserOrgImportRefusalCodes.NoMatchingUsers, (await CommitRefused(draftId, typeId, UserOrgImportMode.Merge, 0)).Code);
        }

        [TestMethod]
        public async Task AReplaceIsRefusedIfItWouldClearMoreThanWasConfirmed()
        {
            // A confirmation given for clearing one person must not cover clearing twenty thousand.
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var c = AddUser("c@contoso.com");
            var typeId = await NewType();
            await Assign(typeId, (a, "X"), (b, "Y"), (c, "Z"));
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));

            var refusal = await CommitRefused(draftId, typeId, UserOrgImportMode.Replace, 1);

            Assert.AreEqual(UserOrgImportRefusalCodes.ClearExceedsConfirmed, refusal.Code);
            Assert.AreEqual(2, Convert.ToInt32(refusal.Values["count"]));
            Assert.AreEqual(1, Convert.ToInt32(refusal.Values["confirmed"]));
            Assert.AreEqual(UserOrgImportStatus.Draft, (await _jobs.GetJobAsync(draftId)).Status);
        }

        [TestMethod]
        public async Task AMergeIsRefusedIfItsBlankRowsWouldClearMoreThanWasConfirmed()
        {
            // Merge clears too: everyone the file lists with an empty value. That used to go through
            // with no count and no warning.
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var typeId = await NewType();
            await Assign(typeId, (a, "X"), (b, "Y"));
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", null), new UserOrgStagedRow(3, "b@contoso.com", "Y2"));

            var refusal = await CommitRefused(draftId, typeId, UserOrgImportMode.Merge, 0);

            Assert.AreEqual(UserOrgImportRefusalCodes.ClearExceedsConfirmed, refusal.Code);
            Assert.AreEqual(1, Convert.ToInt32(refusal.Values["count"]));
        }

        [TestMethod]
        public async Task AConfirmationCoveringTheClearsIsAccepted()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var typeId = await NewType();
            await Assign(typeId, (a, "X"), (b, "Y"));
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));

            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Replace, 1, Admin);

            Assert.AreEqual(UserOrgImportStatus.Pending, (await _jobs.GetJobAsync(draftId)).Status);
        }

        #endregion

        #region Apply

        [TestMethod]
        public async Task TheApplyRecordsItsOwnSuccess()
        {
            // In the same transaction as the changes, so a job that is anything but Succeeded changed
            // nothing - which is what makes an interrupted job safe to run again.
            var a = AddUser("a@contoso.com");
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "Retail"));
            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Merge, 0, Admin);
            Assert.IsTrue(await _jobs.TryClaimJobAsync(draftId));

            var applied = await _jobs.ApplyAsync(draftId);

            Assert.AreEqual(UserOrgImportStatus.Succeeded, applied.Status);
            var job = await _jobs.GetJobAsync(draftId);
            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status, "Recorded before anyone calls CompleteJobAsync.");
            Assert.IsNotNull(job.FinishedUtc);
            Assert.AreEqual("Retail", await ValueOf(a));

            await _jobs.CompleteJobAsync(draftId, UserOrgImportStatus.Failed, "late", CancellationToken.None, UserOrgImportErrorCodes.Failed);
            Assert.AreEqual(UserOrgImportStatus.Succeeded, (await _jobs.GetJobAsync(draftId)).Status, "Nothing may overwrite a success.");
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = {draftId}"), "But the rows are tidied up.");
        }

        [TestMethod]
        public async Task TheApplyReChecksAMergesClearsAgainstTheConfirmation()
        {
            // Between the commit and the worker running, another import can give someone a value that
            // this file then clears. The confirmation covered the number the admin was shown.
            var a = AddUser("a@contoso.com");
            AddUser("b@contoso.com");
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", null), new UserOrgStagedRow(3, "b@contoso.com", "Y"));
            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Merge, 0, Admin);
            await Assign(typeId, (a, "Assigned since"));
            await _jobs.TryClaimJobAsync(draftId);

            try
            {
                await _jobs.ApplyAsync(draftId);
                Assert.Fail("A clear nobody confirmed must be refused.");
            }
            catch (UserOrgValidationException ex)
            {
                Assert.AreEqual(UserOrgImportErrorCodes.ClearExceedsConfirmed, ex.Code);
            }

            Assert.AreEqual("Assigned since", await ValueOf(a), "Refused before anything was written.");
        }

        #endregion

        #region Claim and resume

        [TestMethod]
        public async Task EveryClaimIsCountedAndAStaleWorkerCanBeTakenOver()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Merge, 0, Admin);

            Assert.IsTrue(await _jobs.TryClaimJobAsync(draftId));
            Assert.IsFalse(await _jobs.TryClaimJobAsync(draftId), "A live worker keeps its job.");
            Assert.AreEqual(1, (await _jobs.GetJobAsync(draftId)).Attempts);

            _db.Execute($"UPDATE dbo.user_org_import_jobs SET heartbeat_utc = DATEADD(MINUTE, -2, SYSUTCDATETIME()) WHERE id = {draftId}");

            Assert.IsTrue(await _jobs.TryClaimJobAsync(draftId), "A worker that went quiet is taken over.");
            Assert.AreEqual(2, (await _jobs.GetJobAsync(draftId)).Attempts);
        }

        [TestMethod]
        public async Task ResumeFindsLostDispatchesAndStaleWorkersOnly()
        {
            AddUser("a@contoso.com");
            var ids = new List<int>();
            foreach (var name in new[] { "Lost", "Stale", "Fresh pending", "Fresh running" })
            {
                var typeId = await NewType(name);
                var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));
                await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Merge, 0, Admin);
                ids.Add(draftId);
            }

            _db.Execute($"UPDATE dbo.user_org_import_jobs SET queued_utc = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE id = {ids[0]}");
            await _jobs.TryClaimJobAsync(ids[1]);
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET heartbeat_utc = DATEADD(MINUTE, -2, SYSUTCDATETIME()) WHERE id = {ids[1]}");
            await _jobs.TryClaimJobAsync(ids[3]);

            var resumed = await _jobs.ResumeStaleJobsAsync();

            CollectionAssert.AreEquivalent(new[] { ids[0], ids[1] }, resumed.ToArray());
            Assert.AreEqual(UserOrgImportStatus.Pending, (await _jobs.GetJobAsync(ids[0])).Status);
            Assert.AreEqual(UserOrgImportStatus.Running, (await _jobs.GetJobAsync(ids[1])).Status, "Left for the claim to take over.");
        }

        [TestMethod]
        public async Task AJobInterruptedTooOftenIsStoppedAndSaysSo()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Merge, 0, Admin);
            await _jobs.TryClaimJobAsync(draftId);
            _db.Execute($@"UPDATE dbo.user_org_import_jobs
SET attempts = {UserOrgImportJobLimits.MaxAttempts}, heartbeat_utc = DATEADD(MINUTE, -2, SYSUTCDATETIME())
WHERE id = {draftId}");

            var resumed = await _jobs.ResumeStaleJobsAsync();

            Assert.AreEqual(0, resumed.Count);
            var job = await _jobs.GetJobAsync(draftId);
            Assert.AreEqual(UserOrgImportStatus.Failed, job.Status);
            Assert.AreEqual(UserOrgImportErrorCodes.InterruptedRepeatedly, job.ErrorCode);
            StringAssert.Contains(job.ErrorMessage, "No changes were made");
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = {draftId}"));
        }

        [TestMethod]
        public async Task AJobQueuedTooLongAgoIsStoppedRatherThanLandingNow()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Merge, 0, Admin);
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET queued_utc = DATEADD(HOUR, -3, SYSUTCDATETIME()) WHERE id = {draftId}");

            var resumed = await _jobs.ResumeStaleJobsAsync();

            Assert.AreEqual(0, resumed.Count);
            Assert.AreEqual(UserOrgImportErrorCodes.InterruptedRepeatedly, (await _jobs.GetJobAsync(draftId)).ErrorCode);
        }

        [TestMethod]
        public async Task ResumeTidiesUpFinishedJobsAndExpiredDrafts()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var finished = await Draft(typeId, "one@contoso.com", new UserOrgStagedRow(2, "a@contoso.com", "X"));
            var expired = await Draft(typeId, "two@contoso.com", new UserOrgStagedRow(2, "a@contoso.com", "X"));
            var live = await Draft(typeId, "three@contoso.com", new UserOrgStagedRow(2, "a@contoso.com", "X"));
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET status = 3 WHERE id = {finished}");
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET queued_utc = DATEADD(HOUR, -3, SYSUTCDATETIME()) WHERE id = {expired}");

            await _jobs.ResumeStaleJobsAsync();

            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_import_staging WHERE job_id = {finished}"));
            Assert.IsNotNull(await _jobs.GetJobAsync(finished), "A finished job is history; only its rows go.");
            Assert.IsNull(await _jobs.GetJobAsync(expired));
            Assert.IsNotNull(await _jobs.GetJobAsync(live));
        }

        #endregion

        #region Change log

        /// <summary>Previews, imports and applies a file, as the portal and the worker would.</summary>
        private static async Task<int> Applied(int typeId, UserOrgImportMode mode, params UserOrgStagedRow[] rows)
        {
            var draftId = await Draft(typeId, Admin, rows);
            await _jobs.CommitDraftAsync(draftId, typeId, mode, int.MaxValue, Admin);
            Assert.IsTrue(await _jobs.TryClaimJobAsync(draftId));
            await _jobs.ApplyAsync(draftId);
            return draftId;
        }

        private static UserOrgChangeLogShipper Shipper(IUserOrgChangeLog log)
        {
            return new UserOrgChangeLogShipper(_jobs, _outbox, _types, () => log);
        }

        [TestMethod]
        public async Task TheApplyCapturesEveryChangeWithItsBeforeAndAfter()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var c = AddUser("c@contoso.com");
            AddUser("d@contoso.com");
            var same = AddUser("same@contoso.com");
            var typeId = await NewType();
            await Assign(typeId, (a, "X"), (b, "Y"), (c, "Z"), (same, "Kept"));

            var jobId = await Applied(
                typeId,
                UserOrgImportMode.Replace,
                new UserOrgStagedRow(2, "a@contoso.com", "X2"),
                new UserOrgStagedRow(3, "b@contoso.com", null),
                new UserOrgStagedRow(4, "d@contoso.com", "Καλημέρα κόσμε"),
                new UserOrgStagedRow(5, "same@contoso.com", "Kept"),
                new UserOrgStagedRow(6, "nobody@contoso.com", "W"));

            var job = await _jobs.GetJobAsync(jobId);
            Assert.AreEqual(UserOrgChangeLogStatus.Pending, job.ChangeLogStatus, "Captured with the changes, in the same transaction.");

            var changes = (await _outbox.ReadAsync(jobId, 0, 100)).ToDictionary(x => x.Upn);
            Assert.AreEqual(job.RowsApplied + job.RowsCleared, changes.Count, "One change per user the import touched - and no others.");
            Assert.IsFalse(changes.ContainsKey("same@contoso.com"), "A user whose value did not change was not changed.");

            Assert.AreEqual("X", changes["a@contoso.com"].OldValue);
            Assert.AreEqual("X2", changes["a@contoso.com"].NewValue);
            Assert.AreEqual(UserOrgChangeKind.Changed, changes["a@contoso.com"].Kind);
            Assert.AreEqual(UserOrgChangeKind.Cleared, changes["b@contoso.com"].Kind, "Blank in the file.");
            Assert.AreEqual("Y", changes["b@contoso.com"].OldValue);
            Assert.AreEqual(UserOrgChangeKind.Cleared, changes["c@contoso.com"].Kind, "Absent from a Replace.");
            Assert.AreEqual(UserOrgChangeKind.Added, changes["d@contoso.com"].Kind);
            Assert.AreEqual("Καλημέρα κόσμε", changes["d@contoso.com"].NewValue);
        }

        [TestMethod]
        public async Task AJobRetiredWhileItWasApplyingIsNotReportedAsApplied()
        {
            // The resume sweep retires a job whose worker has gone quiet, without the type's lock, and
            // deletes its staged rows. Here a trigger retires the job in the middle of the apply - after
            // its writes, before it records its success - as a sweep on another instance could.
            var a = AddUser("a@contoso.com");
            var typeId = await NewType();
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Merge, 0, Admin);
            Assert.IsTrue(await _jobs.TryClaimJobAsync(draftId));

            _db.Execute(@"
CREATE TRIGGER dbo.tr_test_sweep_retires ON dbo.user_org_import_changes AFTER INSERT AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.user_org_import_jobs SET status = 4 WHERE id IN (SELECT job_id FROM inserted);
END");
            try
            {
                await Assert.ThrowsExceptionAsync<UserOrgJobSupersededException>(() => _jobs.ApplyAsync(draftId));
            }
            finally
            {
                _db.Execute("DROP TRIGGER dbo.tr_test_sweep_retires;");
            }

            Assert.AreEqual(0, (await _assignments.GetForUserAsync(a)).Count, "Nothing it did survives.");
            Assert.AreNotEqual(UserOrgImportStatus.Succeeded, (await _jobs.GetJobAsync(draftId)).Status);
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_import_changes WHERE job_id = {draftId}"));
        }

        [TestMethod]
        public async Task ARefusedApplyCapturesNothing()
        {
            var a = AddUser("a@contoso.com");
            var typeId = await NewType();
            await Assign(typeId, (a, "X"));
            var draftId = await Draft(typeId, Admin, new UserOrgStagedRow(2, "a@contoso.com", null));
            await _jobs.CommitDraftAsync(draftId, typeId, UserOrgImportMode.Merge, 1, Admin);
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET confirmed_clear_count = 0 WHERE id = {draftId}");
            await _jobs.TryClaimJobAsync(draftId);

            try
            {
                await _jobs.ApplyAsync(draftId);
                Assert.Fail("A clear nobody confirmed must be refused.");
            }
            catch (UserOrgValidationException)
            {
            }

            Assert.IsNull((await _jobs.GetJobAsync(draftId)).ChangeLogStatus);
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_import_changes WHERE job_id = {draftId}"));
        }

        [TestMethod]
        public async Task ShippingWritesTheLogAndEmptiesTheOutbox()
        {
            var a = AddUser("a@contoso.com");
            AddUser("b@contoso.com");
            var typeId = await NewType("Cost Centre");
            await Assign(typeId, (a, "X"));
            var jobId = await Applied(
                typeId,
                UserOrgImportMode.Replace,
                new UserOrgStagedRow(2, "b@contoso.com", "Y"));
            var log = new InMemoryUserOrgChangeLog();

            Assert.AreEqual(UserOrgChangeLogShipOutcome.Written, await Shipper(log).ShipAsync(jobId));

            var job = await _jobs.GetJobAsync(jobId);
            Assert.AreEqual(UserOrgChangeLogStatus.Memory, job.ChangeLogStatus);
            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_import_changes WHERE job_id = {jobId}"));

            var logId = UserOrgChangeLogKeys.LogId(job.Id, job.QueuedUtc);
            var summary = await log.GetImportAsync(logId, CancellationToken.None);
            Assert.AreEqual("Cost Centre", summary.OrgTypeName);
            Assert.AreEqual(Admin, summary.StartedBy);
            Assert.AreEqual(UserOrgImportMode.Replace, summary.Mode);
            Assert.AreEqual(1, summary.Added);
            Assert.AreEqual(1, summary.Cleared);
            Assert.AreEqual(0, summary.Changed);

            var page = await log.GetChangesAsync(logId, null, null, 50, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "a@contoso.com", "b@contoso.com" }, page.Items.Select(i => i.Upn).ToArray());

            Assert.AreEqual(UserOrgChangeLogShipOutcome.NothingToWrite, await Shipper(log).ShipAsync(jobId), "Written once.");
        }

        [TestMethod]
        public async Task AnInterruptedWriteIsRepeatedWithoutDuplicatingAChange()
        {
            AddUser("a@contoso.com");
            AddUser("b@contoso.com");
            var typeId = await NewType();
            var jobId = await Applied(
                typeId,
                UserOrgImportMode.Merge,
                new UserOrgStagedRow(2, "a@contoso.com", "X"),
                new UserOrgStagedRow(3, "b@contoso.com", "Y"));
            var log = new InMemoryUserOrgChangeLog();

            try
            {
                await Shipper(new FailingOnCompleteLog(log)).ShipAsync(jobId);
                Assert.Fail("The write was meant to fail.");
            }
            catch (InvalidOperationException)
            {
            }

            Assert.AreEqual(UserOrgChangeLogStatus.Pending, (await _jobs.GetJobAsync(jobId)).ChangeLogStatus, "Still to be written.");
            Assert.AreEqual(2, Count($"SELECT COUNT(*) FROM dbo.user_org_import_changes WHERE job_id = {jobId}"), "Safe in the outbox.");
            CollectionAssert.Contains((await _outbox.ListPendingAsync(10)).ToList(), jobId, "The resume sweep will find it.");

            Assert.AreEqual(UserOrgChangeLogShipOutcome.Written, await Shipper(log).ShipAsync(jobId));

            var job = await _jobs.GetJobAsync(jobId);
            var summary = await log.GetImportAsync(UserOrgChangeLogKeys.LogId(job.Id, job.QueuedUtc), CancellationToken.None);
            Assert.AreEqual(2, summary.StoredChanges);
            Assert.AreEqual(2, log.StoredChanges, "The repeat replaced the first attempt's changes rather than adding to them.");
            Assert.AreEqual(0, (await _outbox.ListPendingAsync(10)).Count);
        }

        [TestMethod]
        public async Task OnlyOneWorkerWritesAnImportsLog()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var jobId = await Applied(typeId, UserOrgImportMode.Merge, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            var log = new InMemoryUserOrgChangeLog();

            using (var lease = await _outbox.TryLeaseAsync(jobId))
            {
                Assert.IsNotNull(lease);
                Assert.IsNull(await _outbox.TryLeaseAsync(jobId), "The lease is exclusive.");
                Assert.AreEqual(UserOrgChangeLogShipOutcome.LeaseHeld, await Shipper(log).ShipAsync(jobId));
            }

            Assert.AreEqual(UserOrgChangeLogShipOutcome.Written, await Shipper(log).ShipAsync(jobId), "Released with the lease.");
        }

        /// <summary>Ends the SQL session holding an import's change-log lease, as a failover would.</summary>
        private static void KillLeaseSession(int jobId)
        {
            var spid = _db.Scalar($@"
SELECT TOP (1) request_session_id FROM sys.dm_tran_locks
WHERE resource_type = 'APPLICATION' AND resource_database_id = DB_ID()
  AND (resource_description LIKE '%user_org_changelog_{jobId}' OR resource_description LIKE '%user_org_changelog_{jobId}[^0-9]%');");
            Assert.IsNotNull(spid, "The lease's lock should be visible.");
            _db.Execute($"KILL {Convert.ToInt32(spid)};");
        }

        [TestMethod]
        public async Task ALeaseKnowsWhenItsConnectionHasGone()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var jobId = await Applied(typeId, UserOrgImportMode.Merge, new UserOrgStagedRow(2, "a@contoso.com", "X"));

            using (var lease = await _outbox.TryLeaseAsync(jobId))
            {
                Assert.IsTrue(await lease.IsHeldAsync());

                KillLeaseSession(jobId);

                Assert.IsFalse(await lease.IsHeldAsync(), "The lock went with the connection.");
                using (var taken = await _outbox.TryLeaseAsync(jobId))
                {
                    Assert.IsNotNull(taken, "Another worker can take the log over.");
                }

                Exception refused = null;
                try
                {
                    await lease.CompleteAsync(UserOrgChangeLogStatus.Memory);
                }
                catch (Exception ex)
                {
                    refused = ex;
                }

                Assert.IsNotNull(refused, "A lost lease cannot empty the outbox.");
            }

            Assert.AreEqual(UserOrgChangeLogStatus.Pending, (await _jobs.GetJobAsync(jobId)).ChangeLogStatus);
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM dbo.user_org_import_changes WHERE job_id = {jobId}"));
        }

        [TestMethod]
        public async Task AWorkerThatLosesItsLeaseMidWriteLeavesTheLogToWhoeverTookItOver()
        {
            // A failover while a long log is being written: another worker may take it over and finish,
            // so this one must neither write a summary counted from what it managed to read, nor empty the
            // outbox the other is reading.
            AddUser("a@contoso.com");
            AddUser("b@contoso.com");
            var typeId = await NewType();
            var jobId = await Applied(
                typeId,
                UserOrgImportMode.Merge,
                new UserOrgStagedRow(2, "a@contoso.com", "X"),
                new UserOrgStagedRow(3, "b@contoso.com", "Y"));
            var log = new InMemoryUserOrgChangeLog();

            var outcome = await Shipper(new FailoverLog(log, () => KillLeaseSession(jobId))).ShipAsync(jobId);

            Assert.AreEqual(UserOrgChangeLogShipOutcome.LeaseLost, outcome);
            var job = await _jobs.GetJobAsync(jobId);
            Assert.IsNull(await log.GetImportAsync(UserOrgChangeLogKeys.LogId(job.Id, job.QueuedUtc), CancellationToken.None), "No summary.");
            Assert.AreEqual(UserOrgChangeLogStatus.Pending, job.ChangeLogStatus);
            Assert.AreEqual(2, Count($"SELECT COUNT(*) FROM dbo.user_org_import_changes WHERE job_id = {jobId}"), "The outbox is intact.");

            Assert.AreEqual(UserOrgChangeLogShipOutcome.Written, await Shipper(log).ShipAsync(jobId), "The next worker writes it.");
            Assert.AreEqual(2, (await log.GetImportAsync(UserOrgChangeLogKeys.LogId(job.Id, job.QueuedUtc), CancellationToken.None)).StoredChanges);
        }

        /// <summary>A change log whose first write coincides with the writer's SQL session being lost.</summary>
        private sealed class FailoverLog : IUserOrgChangeLog
        {
            private readonly IUserOrgChangeLog _inner;
            private Action _failover;

            public FailoverLog(IUserOrgChangeLog inner, Action failover)
            {
                _inner = inner;
                _failover = failover;
            }

            public UserOrgChangeLogStatus Destination => _inner.Destination;

            public Task AppendAsync(UserOrgChangeLogImport import, IReadOnlyList<UserOrgChangeRecord> changes, CancellationToken cancellationToken)
            {
                Interlocked.Exchange(ref _failover, null)?.Invoke();
                return _inner.AppendAsync(import, changes, cancellationToken);
            }

            public Task CompleteAsync(UserOrgChangeLogImport import, CancellationToken cancellationToken)
                => _inner.CompleteAsync(import, cancellationToken);

            public Task<UserOrgChangeLogImport> GetImportAsync(string logId, CancellationToken cancellationToken)
                => _inner.GetImportAsync(logId, cancellationToken);

            public Task<UserOrgChangeLogPage> GetChangesAsync(string logId, string search, string continuation, int pageSize, CancellationToken cancellationToken)
                => _inner.GetChangesAsync(logId, search, continuation, pageSize, cancellationToken);
        }

        [TestMethod]
        public async Task AnImportThatChangedNobodyStillHasALog()
        {
            // Confirmation that the values were already current is worth recording too.
            var a = AddUser("a@contoso.com");
            var typeId = await NewType();
            await Assign(typeId, (a, "X"));
            var jobId = await Applied(typeId, UserOrgImportMode.Merge, new UserOrgStagedRow(2, "a@contoso.com", "X"));
            var log = new InMemoryUserOrgChangeLog();

            Assert.AreEqual(UserOrgChangeLogShipOutcome.Written, await Shipper(log).ShipAsync(jobId));

            var job = await _jobs.GetJobAsync(jobId);
            var summary = await log.GetImportAsync(UserOrgChangeLogKeys.LogId(job.Id, job.QueuedUtc), CancellationToken.None);
            Assert.AreEqual(0, summary.ChangeCount);
        }

        [TestMethod]
        public async Task AStorageAccountThatCannotBeReachedLeavesTheListWaitingNotInMemory()
        {
            // The deployment configured a store, so its lists are meant to survive a restart. Writing one
            // to memory because the account was briefly unreachable would lose it at the next recycle.
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var jobId = await Applied(typeId, UserOrgImportMode.Merge, new UserOrgStagedRow(2, "a@contoso.com", "X"));

            Assert.AreEqual(
                UserOrgChangeLogShipOutcome.StoreUnavailable,
                await new UserOrgChangeLogShipper(_jobs, _outbox, _types, () => null).ShipAsync(jobId));

            Assert.AreEqual(UserOrgChangeLogStatus.Pending, (await _jobs.GetJobAsync(jobId)).ChangeLogStatus);
            Assert.AreEqual(1, Count($"SELECT COUNT(*) FROM dbo.user_org_import_changes WHERE job_id = {jobId}"), "Still safe in the outbox.");
            using (var lease = await _outbox.TryLeaseAsync(jobId))
            {
                Assert.IsNotNull(lease, "The lease was released, so the next attempt can write it.");
            }

            var log = new InMemoryUserOrgChangeLog();
            Assert.AreEqual(UserOrgChangeLogShipOutcome.Written, await Shipper(log).ShipAsync(jobId), "Written once the store is back.");
            Assert.AreEqual(1, log.StoredChanges);
        }

        [TestMethod]
        public async Task DeletingTheOrgTypeTakesAnUnwrittenListWithIt()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var jobId = await Applied(typeId, UserOrgImportMode.Merge, new UserOrgStagedRow(2, "a@contoso.com", "X"));

            await _types.DeleteAsync(typeId);

            Assert.AreEqual(0, Count($"SELECT COUNT(*) FROM dbo.user_org_import_changes WHERE job_id = {jobId}"));
            Assert.AreEqual(0, (await _outbox.ListPendingAsync(10)).Count);
        }

        /// <summary>Appends normally, then fails to write the summary - a worker dying half way.</summary>
        private sealed class FailingOnCompleteLog : IUserOrgChangeLog
        {
            private readonly IUserOrgChangeLog _inner;

            public FailingOnCompleteLog(IUserOrgChangeLog inner)
            {
                _inner = inner;
            }

            public UserOrgChangeLogStatus Destination => _inner.Destination;

            public Task AppendAsync(UserOrgChangeLogImport import, IReadOnlyList<UserOrgChangeRecord> changes, CancellationToken cancellationToken)
                => _inner.AppendAsync(import, changes, cancellationToken);

            public Task CompleteAsync(UserOrgChangeLogImport import, CancellationToken cancellationToken)
                => throw new InvalidOperationException("the storage account went away");

            public Task<UserOrgChangeLogImport> GetImportAsync(string logId, CancellationToken cancellationToken)
                => _inner.GetImportAsync(logId, cancellationToken);

            public Task<UserOrgChangeLogPage> GetChangesAsync(string logId, string search, string continuation, int pageSize, CancellationToken cancellationToken)
                => _inner.GetChangesAsync(logId, search, continuation, pageSize, cancellationToken);
        }

        #endregion

        #region History

        [TestMethod]
        public async Task TheHistoryListsImportsNewestFirstAndLeavesOutDrafts()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var older = await Draft(typeId, "one@contoso.com", new UserOrgStagedRow(2, "a@contoso.com", "X"));
            var newer = await Draft(typeId, "two@contoso.com", new UserOrgStagedRow(2, "a@contoso.com", "Y"));
            await Draft(typeId, "three@contoso.com", new UserOrgStagedRow(2, "a@contoso.com", "Z"));

            // Committed in the opposite order to the previews: the history follows the imports.
            await _jobs.CommitDraftAsync(newer, typeId, UserOrgImportMode.Merge, 0, Admin);
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET status = 3, queued_utc = DATEADD(MINUTE, -10, SYSUTCDATETIME()) WHERE id = {newer}");
            await _jobs.CommitDraftAsync(older, typeId, UserOrgImportMode.Merge, 0, Admin);

            var history = await _jobs.ListJobsAsync(typeId, 10);

            CollectionAssert.AreEqual(new[] { older, newer }, history.Select(j => j.Id).ToArray());
            Assert.AreEqual(older, (await _types.GetSummariesAsync()).Single().LastImport.Id, "The last import is the latest one queued.");
        }

        #endregion
    }

    /// <summary>What the admin page is told about a job's status.</summary>
    [TestClass]
    public class UserOrgImportStatusDisplayTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        private static UserOrgImportJob Stranded(int attempts, TimeSpan queuedAgo)
        {
            return new UserOrgImportJob
            {
                Status = UserOrgImportStatus.Running,
                QueuedUtc = Now - queuedAgo,
                StartedUtc = Now - queuedAgo,
                HeartbeatUtc = Now - UserOrgImportRunner.StaleHeartbeatThreshold - TimeSpan.FromSeconds(10),
                Attempts = attempts,
            };
        }

        [TestMethod]
        public void AStrandedJobThatWillBeResumedShowsAsWaiting()
        {
            // So the page keeps polling it rather than stopping on "interrupted" moments before a new
            // worker picks it up.
            Assert.AreEqual("pending", UserOrgAdminService.DescribeStatus(Stranded(1, TimeSpan.FromMinutes(5)), Now));
        }

        [TestMethod]
        public void AStrandedJobThatWillNotBeResumedShowsAsInterrupted()
        {
            Assert.AreEqual(
                "interrupted",
                UserOrgAdminService.DescribeStatus(Stranded(UserOrgImportJobLimits.MaxAttempts, TimeSpan.FromMinutes(5)), Now),
                "Interrupted too often.");
            Assert.AreEqual(
                "interrupted",
                UserOrgAdminService.DescribeStatus(Stranded(1, UserOrgImportJobLimits.ResumeWindow + TimeSpan.FromMinutes(1)), Now),
                "Queued too long ago.");
        }

        [TestMethod]
        public void AHealthyJobShowsWhatItIsDoing()
        {
            Assert.AreEqual("running", UserOrgAdminService.DescribeStatus(new UserOrgImportJob
            {
                Status = UserOrgImportStatus.Running,
                QueuedUtc = Now.AddMinutes(-20),
                HeartbeatUtc = Now.AddSeconds(-5),
                Attempts = 1,
            }, Now));
            Assert.AreEqual("succeeded", UserOrgAdminService.DescribeStatus(new UserOrgImportJob { Status = UserOrgImportStatus.Succeeded, QueuedUtc = Now.AddDays(-1) }, Now));
        }
    }

    /// <summary>
    /// The admin service's CSV preview, commit, polling and resume, end to end against real SQL Server.
    /// </summary>
    [TestClass]
    public class UserOrgAdminServiceCsvTests
    {
        private const string Admin = "admin@contoso.com";

        private static ScratchDatabase _db;

        private List<int> _dispatched;
        private List<int> _dispatchedChangeLogs;
        private RecordingTelemetry _telemetry;
        private UserOrgAdminService _service;
        private IUserOrgImportJobStore _jobs;
        private IUserOrgTypeStore _types;
        private IUserOrgAssignmentStore _assignments;
        private IUserOrgChangeOutbox _outbox;
        private InMemoryUserOrgChangeLog _memoryLog;

        [ClassInitialize]
        public static void ClassInit(TestContext context)
        {
            _db = UserOrgImportTestSchema.Create("userorgservice");
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _db?.Dispose();
        }

        [TestInitialize]
        public void Reset()
        {
            UserOrgImportTestSchema.Reset(_db);
            _dispatched = new List<int>();
            _dispatchedChangeLogs = new List<int>();
            _telemetry = new RecordingTelemetry();
            _types = UserOrgStores.CreateTypeStore(_db.ConnectionString);
            _assignments = UserOrgStores.CreateAssignmentStore(_db.ConnectionString);
            _jobs = UserOrgStores.CreateImportJobStore(_db.ConnectionString);
            _outbox = UserOrgStores.CreateChangeOutbox(_db.ConnectionString);
            _memoryLog = new InMemoryUserOrgChangeLog();
            _service = ServiceReadingFrom(_memoryLog);
        }

        private UserOrgAdminService ServiceReadingFrom(IUserOrgChangeLog log)
        {
            return new UserOrgAdminService(
                _types,
                _assignments,
                _jobs,
                UserOrgStores.CreateUserLookup(_db.ConnectionString),
                new NoGraph(),
                id => _dispatched.Add(id),
                telemetry: _telemetry,
                resumeGate: new UserOrgResumeGate(),
                changeOutbox: _outbox,
                // A pending list has no store yet; asked about one, this says where it would be written now.
                changeLogFor: status => status == log.Destination || status == UserOrgChangeLogStatus.Pending ? log : null,
                dispatchChangeLog: id => _dispatchedChangeLogs.Add(id));
        }

        private sealed class NoGraph : IUserOrgGraphProbe
        {
            public Task<UserOrgProbeOutcome> ResolveAsync(EntraOrgAttributeSpec spec, string upn, CancellationToken cancellationToken)
                => throw new NotSupportedException();

            public Task<UserOrgAttributeCatalogueModel> DiscoverAsync(CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        private sealed class RecordingTelemetry : IUserOrgImportTelemetry
        {
            public readonly List<UserOrgImportTelemetryEvent> Events = new List<UserOrgImportTelemetryEvent>();

            public void Record(UserOrgImportTelemetryEvent item)
            {
                lock (Events)
                {
                    Events.Add(item);
                }
            }
        }

        private static int AddUser(string upn) => UserOrgImportTestSchema.AddUser(_db, upn);

        private Task<int> NewType(string name = "Team") => _types.CreateAsync(UserOrgImportTestSchema.CsvType(name));

        private Task<UserOrgCsvPreviewModel> Preview(int typeId, string csv, int? userColumn = null, int? valueColumn = null)
        {
            return Preview(typeId, Encoding.UTF8.GetBytes(csv), userColumn, valueColumn);
        }

        private async Task<UserOrgCsvPreviewModel> Preview(int typeId, byte[] file, int? userColumn = null, int? valueColumn = null)
        {
            using (var stream = new MemoryStream(file, 0, file.Length, false, true))
            {
                return await _service.PreviewAsync(stream, "orgs.csv", typeId, Admin, userColumn, valueColumn, CancellationToken.None);
            }
        }

        private static async Task<UserOrgValidationException> Refused(Func<Task> call)
        {
            try
            {
                await call();
            }
            catch (UserOrgValidationException ex)
            {
                return ex;
            }

            Assert.Fail("The call should have been refused.");
            return null;
        }

        [TestMethod]
        public async Task ASaveFromADialogOpenedBeforeAColleaguesSaveIsRefused()
        {
            // The page sends back the revision each dialog opened at. B's rename goes through and says
            // what the next save needs; A's dialog, opened before it, cannot then turn the type off and
            // put B's name back in the same stroke.
            var typeId = await NewType("Team");
            var opened = (await _service.ListAsync(CancellationToken.None)).Single(t => t.Id == typeId);

            var savedByB = await _service.UpdateAsync(
                typeId,
                new UserOrgTypeSaveModel { Name = "Team (B)", Source = "csv", IsEnabled = true, ExpectedRevision = opened.Revision },
                CancellationToken.None);
            Assert.AreEqual(opened.Revision + 1, savedByB.Revision, "The response carries the revision the next save needs.");

            var refusal = await Refused(() => _service.UpdateAsync(
                typeId,
                new UserOrgTypeSaveModel { Name = "Team", Source = "csv", IsEnabled = false, ExpectedRevision = opened.Revision },
                CancellationToken.None));
            Assert.AreEqual(UserOrgMessageCodes.TypeChangedElsewhere, refusal.Code);

            var now = (await _service.ListAsync(CancellationToken.None)).Single(t => t.Id == typeId);
            Assert.AreEqual("Team (B)", now.Name, "B's rename survives.");
            Assert.IsTrue(now.IsEnabled, "Nothing of A's save was applied.");
            Assert.AreEqual(savedByB.Revision, now.Revision);
        }

        [TestMethod]
        public async Task ThePreviewStagesADraftAndReportsWhatImportingItWouldDo()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            AddUser("c@contoso.com");
            var typeId = await NewType("Team");
            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, "X"), new UserOrgAssignmentUpdate(b, typeId, "Y") });

            var preview = await Preview(typeId, "UPN,Team\r\na@contoso.com,Retail\r\nnot a upn,Ops\r\nb@contoso.com,\r\nnobody@contoso.com,HR\r\nc@contoso.com,Finance\r\n");

            Assert.IsNull(preview.Blocking);
            Assert.IsNotNull(preview.DraftId);
            Assert.AreEqual(4, preview.TotalRows);
            Assert.AreEqual(1, preview.UnknownUpnCount);
            Assert.AreEqual(2, preview.MatchedUserCount, "a and c get a value; b is cleared.");
            Assert.AreEqual(2, preview.CurrentlyAssignedCount);
            Assert.AreEqual(1, preview.WouldClearCount, "Replace clears b.");
            Assert.AreEqual(1, preview.MergeWouldClearCount, "So does Merge, because b's value is empty.");
            Assert.AreEqual(1, preview.Problems.Count);
            Assert.AreEqual(UserOrgCsvProblemCodes.NotAValidUpn, preview.Problems[0].Code);

            CollectionAssert.AreEqual(new[] { 3, 5 }, preview.UnusableRows.Select(r => r.LineNumber).ToArray(), "Every row that will not be imported, in file order.");
            CollectionAssert.AreEqual(
                new[] { UserOrgCsvProblemCodes.NotAValidUpn, UserOrgCsvProblemCodes.UnknownUser },
                preview.UnusableRows.Select(r => r.Code).ToArray());
            Assert.AreEqual(2, preview.UnusableRowCount);
            Assert.AreEqual(UserOrgImportStatus.Draft, (await _jobs.GetJobAsync(preview.DraftId.Value)).Status);

            var previewed = _telemetry.Events.Single(e => e.Stage == UserOrgImportStages.Previewed);
            Assert.AreEqual(4, previewed.Rows);
            Assert.AreEqual(1, previewed.RowsInvalid);
            Assert.AreEqual(1, previewed.RowsUnknownUpn);
        }

        [TestMethod]
        public async Task AFileThatCannotBeReadStagesNothing()
        {
            var typeId = await NewType();

            var preview = await Preview(typeId, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00 });

            Assert.AreEqual(UserOrgCsvBlockingCodes.ExcelWorkbook, preview.Blocking.Code);
            Assert.IsNull(preview.DraftId);
            Assert.AreEqual(0, Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM dbo.user_org_import_jobs")));
            Assert.AreEqual(UserOrgCsvBlockingCodes.ExcelWorkbook, _telemetry.Events.Single().Code);
        }

        [TestMethod]
        public async Task TooManyRowsSaysWhatTheLimitIs()
        {
            var typeId = await NewType();
            var builder = new StringBuilder("UPN,Team\r\n");
            for (var i = 0; i <= UserOrgCsvParser.MaxDataLines; i++)
            {
                builder.Append('u').Append(i).Append("@contoso.com,T\r\n");
            }

            var preview = await Preview(typeId, builder.ToString());

            Assert.AreEqual(UserOrgCsvBlockingCodes.TooManyRows, preview.Blocking.Code);
            Assert.AreEqual(UserOrgCsvParser.MaxDataLines, preview.Blocking.Max);
        }

        [TestMethod]
        public async Task AWideFileAsksForItsColumnsAndThenUsesThem()
        {
            AddUser("adele@contoso.com");
            var typeId = await NewType("Cost Centre");
            const string wide = "EmployeeId,DisplayName,UserPrincipalName,Centre\r\n10001,Adele Vance,adele@contoso.com,CC-100\r\n";

            var first = await Preview(typeId, wide);

            Assert.AreEqual(UserOrgCsvBlockingCodes.ChooseColumns, first.Blocking.Code);
            Assert.AreEqual(4, first.ColumnCount);
            Assert.AreEqual(2, first.UserColumnIndex);

            var second = await Preview(typeId, wide, 2, 3);

            Assert.IsNull(second.Blocking);
            Assert.AreEqual("CC-100", second.Rows.Single().OrgValue);
        }

        [TestMethod]
        public async Task HalfAColumnChoiceIsRefused()
        {
            var typeId = await NewType();

            var refusal = await Refused(() => Preview(typeId, "UPN,Team\r\na@contoso.com,X\r\n", 0, null));

            Assert.AreEqual(UserOrgImportRefusalCodes.InvalidColumns, refusal.Code);
        }

        [TestMethod]
        public async Task AFileCannotBePreviewedIntoAnEntraType()
        {
            var typeId = await _types.CreateAsync(new UserOrgType
            {
                Name = "Cost Centre",
                SourceKind = UserOrgSourceKind.EntraAttribute,
                EntraAttributeName = "extensionAttribute1",
                IsEnabled = true,
            });

            var refusal = await Refused(() => Preview(typeId, "UPN,Team\r\na@contoso.com,X\r\n"));

            Assert.AreEqual(UserOrgImportRefusalCodes.TypeNotCsv, refusal.Code);
            Assert.AreEqual("Cost Centre", refusal.Values["name"]);
        }

        [TestMethod]
        public async Task PreviewCommitAndRunImportsExactlyWhatWasPreviewed()
        {
            var a = AddUser("a@contoso.com");
            var typeId = await NewType();
            var preview = await Preview(typeId, "UPN,Team\r\na@contoso.com," + "Καλημέρα κόσμε" + "\r\n");

            var queued = await _service.CommitImportAsync(typeId, preview.DraftId.Value, UserOrgImportMode.Merge, 0, Admin, CancellationToken.None);

            Assert.AreEqual(preview.DraftId.Value, queued.JobId);
            Assert.AreEqual(1, queued.RowsQueued);
            CollectionAssert.AreEqual(new[] { queued.JobId }, _dispatched);

            var job = await new UserOrgImportRunner(_jobs).RunAsync(queued.JobId);

            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status);
            Assert.AreEqual("Καλημέρα κόσμε", (await _assignments.GetForUserAsync(a)).Single().Value);
            Assert.AreEqual("succeeded", (await _service.GetJobAsync(queued.JobId, CancellationToken.None)).Status);
            CollectionAssert.AreEqual(
                new[] { UserOrgImportStages.Previewed, UserOrgImportStages.Committed },
                _telemetry.Events.Select(e => e.Stage).ToArray());
        }

        [TestMethod]
        public async Task ARefusedCommitCarriesItsCodeAndIsReported()
        {
            var a = AddUser("a@contoso.com");
            var b = AddUser("b@contoso.com");
            var typeId = await NewType();
            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, "X"), new UserOrgAssignmentUpdate(b, typeId, "Y") });
            var preview = await Preview(typeId, "UPN,Team\r\na@contoso.com,X\r\n");

            var refusal = await Refused(() => _service.CommitImportAsync(
                typeId, preview.DraftId.Value, UserOrgImportMode.Replace, 0, Admin, CancellationToken.None));

            Assert.AreEqual(UserOrgImportRefusalCodes.ClearExceedsConfirmed, refusal.Code);
            Assert.AreEqual(0, _dispatched.Count);
            var refused = _telemetry.Events.Last();
            Assert.AreEqual(UserOrgImportStages.CommitRefused, refused.Stage);
            Assert.AreEqual(UserOrgImportRefusalCodes.ClearExceedsConfirmed, refused.Code);
        }

        [TestMethod]
        public async Task PollingAStrandedImportHandsItToANewWorker()
        {
            // An App Service recycle between queueing and dispatch used to leave a job nobody would
            // ever claim. Now the page polling it is what gets it going again.
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var preview = await Preview(typeId, "UPN,Team\r\na@contoso.com,X\r\n");
            await _service.CommitImportAsync(typeId, preview.DraftId.Value, UserOrgImportMode.Merge, 0, Admin, CancellationToken.None);
            _dispatched.Clear();
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET queued_utc = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE id = {preview.DraftId.Value}");

            var polled = await _service.GetJobAsync(preview.DraftId.Value, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { preview.DraftId.Value }, _dispatched);
            Assert.AreEqual("pending", polled.Status);
            Assert.AreEqual(1, _telemetry.Events.Single(e => e.Stage == UserOrgImportStages.ResumeSwept).Count);
        }

        [TestMethod]
        public async Task ADraftIsNotAJobYouCanPoll()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var preview = await Preview(typeId, "UPN,Team\r\na@contoso.com,X\r\n");

            Assert.IsNull(await _service.GetJobAsync(preview.DraftId.Value, CancellationToken.None));
            Assert.AreEqual(0, (await _service.ListImportsAsync(typeId, 10, CancellationToken.None)).Count);
            Assert.IsNull(await _service.ListImportsAsync(typeId + 1000, 10, CancellationToken.None), "An unknown type is not found.");
        }

        [TestMethod]
        public async Task TheUnusableRowListIsCappedButTheCountIsNot()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var builder = new StringBuilder("UPN,Team\r\na@contoso.com,X\r\n");
            for (var i = 0; i < UserOrgAdminService.MaxUnusableRows + 5; i++)
            {
                builder.Append("nobody").Append(i).Append("@contoso.com,X\r\n");
            }

            var preview = await Preview(typeId, builder.ToString());

            Assert.AreEqual(UserOrgAdminService.MaxUnusableRows, preview.UnusableRows.Count);
            Assert.AreEqual(UserOrgAdminService.MaxUnusableRows + 5, preview.UnusableRowCount);
            Assert.AreEqual(3, preview.UnusableRows.First().LineNumber);
        }

        [TestMethod]
        public async Task TheFinishedImportCountsUnknownPeopleTheWayThePreviewDid()
        {
            // Rows, not people, in both places. The finished import used to count distinct UPNs, so a
            // file naming one unknown person on three lines previewed as "3 not found" and finished as
            // "1 not found" - two figures an administrator could not reconcile.
            var known = AddUser("known@contoso.com");
            var typeId = await NewType();
            const string csv = "UPN,Team\r\nknown@contoso.com,Retail\r\nghost@contoso.com,A\r\nGHOST@contoso.com,B\r\nghost@contoso.com,C\r\nother-ghost@contoso.com,D\r\n";

            var preview = await Preview(typeId, csv);
            Assert.AreEqual(4, preview.UnknownUpnCount, "The preview counts every line naming nobody.");

            var queued = await _service.CommitImportAsync(typeId, preview.DraftId.Value, UserOrgImportMode.Merge, 0, Admin, CancellationToken.None);
            var job = await new UserOrgImportRunner(_jobs).RunAsync(queued.JobId);

            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status);
            Assert.AreEqual(preview.UnknownUpnCount, job.RowsUnknownUpn, "The finished import reports the figure the preview did.");
            Assert.AreEqual(1, job.RowsApplied);
            Assert.AreEqual("Retail", (await _assignments.GetForUserAsync(known)).Single().Value);
        }

        [TestMethod]
        public async Task TelemetryCarriesNoTenantData()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType("Secret Division Name");
            var preview = await Preview(typeId, "UPN,Team\r\na@contoso.com,Secret Org\r\nnot a upn,X\r\n");
            await _service.CommitImportAsync(typeId, preview.DraftId.Value, UserOrgImportMode.Merge, 0, Admin, CancellationToken.None);

            foreach (var item in _telemetry.Events)
            {
                foreach (var text in new[] { item.Stage, item.Code, item.ExceptionType })
                {
                    Assert.IsFalse((text ?? string.Empty).Contains("@"), item.Stage);
                    Assert.IsFalse((text ?? string.Empty).Contains("Secret"), item.Stage);
                }
            }
        }

        [TestMethod]
        public async Task ARefusedChangeToTheTypesLeavesTheUserFilterDirectoryAlone()
        {
            // A refusal changed nothing, so throwing the snapshot away would only make every report
            // reader pay for a full directory reload - 200,000 users' worth - per rejected click.
            var invalidations = 0;
            var controller = new AnalyticsWeb::Web.AnalyticsWeb.Controllers.UserOrgAPIController(
                () => _service, () => throw new NotSupportedException(), () => invalidations++)
            {
                Request = new System.Net.Http.HttpRequestMessage(),
                Configuration = new System.Web.Http.HttpConfiguration(),
            };
            controller.Request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            Func<Task<System.Web.Http.IHttpActionResult>, Task<int>> status = async call =>
                (int)(await (await call).ExecuteAsync(CancellationToken.None)).StatusCode;

            var blankName = new UserOrgTypeSaveModel { Name = " ", Source = "csv", IsEnabled = true };
            Assert.AreEqual(400, await status(controller.CreateType(blankName, CancellationToken.None)));
            Assert.AreEqual(0, invalidations, "A refused create.");

            var team = new UserOrgTypeSaveModel { Name = "Team", Source = "csv", IsEnabled = true };
            Assert.AreEqual(200, await status(controller.CreateType(team, CancellationToken.None)));
            Assert.AreEqual(1, invalidations, "A created type is offered to the filter at once.");

            Assert.AreNotEqual(200, await status(controller.CreateType(team, CancellationToken.None)), "The name is taken.");
            Assert.AreNotEqual(200, await status(controller.UpdateType(987654, team, CancellationToken.None)), "No such type.");
            Assert.AreEqual(1, invalidations, "Neither refusal touched the snapshot.");

            var id = (await _types.GetAllAsync()).Single().Id;
            Assert.AreEqual(200, await status(controller.DeleteType(id, CancellationToken.None)));
            Assert.AreEqual(2, invalidations);
        }

        [TestMethod]
        public async Task AnEditThatWaitedOnGraphIsRefusedIfTheTypeChangedMeanwhile()
        {
            // Admin A switches a CSV type to Entra; while their save waits on the Graph probe, admin B
            // disables the type. A's clear and bump were decided for the enabled CSV row, so applying
            // them now - clearing B's type's values - would be wrong. A is told instead.
            var user = AddUser("a@contoso.com");
            var typeId = await NewType();
            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(user, typeId, "Kept") });

            var racing = new RacingGraph(async () =>
            {
                var current = await _types.GetAsync(typeId);
                current.IsEnabled = false;
                await _types.UpdateAsync(current, false, true);
            });
            var service = new UserOrgAdminService(
                _types,
                _assignments,
                _jobs,
                UserOrgStores.CreateUserLookup(_db.ConnectionString),
                racing,
                id => _dispatched.Add(id),
                resumeGate: new UserOrgResumeGate());

            var refusal = await Refused(() => service.UpdateAsync(
                typeId,
                new UserOrgTypeSaveModel { Name = "Team", Source = "entra", EntraAttributeName = "extensionAttribute1", IsEnabled = true },
                CancellationToken.None));

            Assert.AreEqual(1, racing.Calls, "The save really did wait on Graph.");
            Assert.AreEqual(UserOrgMessageCodes.TypeChangedElsewhere, refusal.Code);
            var now = await _types.GetAsync(typeId);
            Assert.AreEqual(UserOrgSourceKind.CsvUpload, now.SourceKind, "B's change stands.");
            Assert.IsFalse(now.IsEnabled);
            Assert.AreEqual("Kept", (await _assignments.GetForUserAsync(user)).Single().Value, "A's clear did not run.");
        }

        [TestMethod]
        public async Task AnImportConfirmedAsTheBrowserLeavesIsStillHandedToAWorker()
        {
            // Cancelled after the type was checked: the commit used to take the request's token, so the
            // import could be committed and then never dispatched - nobody left to poll it, and no sweep
            // until someone next opened the page.
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var preview = await Preview(typeId, "UPN,Team\r\na@contoso.com,X\r\n");
            _dispatched.Clear();

            using (var leaving = new CancellationTokenSource())
            {
                var service = new UserOrgAdminService(
                    new CancelsAfterRead(_types, leaving),
                    _assignments,
                    _jobs,
                    UserOrgStores.CreateUserLookup(_db.ConnectionString),
                    new NoGraph(),
                    id => _dispatched.Add(id),
                    resumeGate: new UserOrgResumeGate());

                await service.CommitImportAsync(typeId, preview.DraftId.Value, UserOrgImportMode.Merge, 0, Admin, leaving.Token);

                Assert.IsTrue(leaving.IsCancellationRequested, "The request really had gone.");
            }

            CollectionAssert.AreEqual(new[] { preview.DraftId.Value }, _dispatched, "Handed to a worker all the same.");
            Assert.AreEqual(UserOrgImportStatus.Pending, (await _jobs.GetJobAsync(preview.DraftId.Value)).Status);
        }

        /// <summary>A type store whose read is the last thing the request lives to see.</summary>
        private sealed class CancelsAfterRead : IUserOrgTypeStore
        {
            private readonly IUserOrgTypeStore _inner;
            private readonly CancellationTokenSource _request;

            public CancelsAfterRead(IUserOrgTypeStore inner, CancellationTokenSource request)
            {
                _inner = inner;
                _request = request;
            }

            public async Task<UserOrgType> GetAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
            {
                var type = await _inner.GetAsync(id, cancellationToken);
                _request.Cancel();
                return type;
            }

            public Task<IReadOnlyList<UserOrgType>> GetAllAsync(CancellationToken cancellationToken = default(CancellationToken)) => _inner.GetAllAsync(cancellationToken);

            public Task<IReadOnlyList<UserOrgTypeSummary>> GetSummariesAsync(CancellationToken cancellationToken = default(CancellationToken)) => _inner.GetSummariesAsync(cancellationToken);

            public Task<IReadOnlyList<UserOrgType>> GetEnabledEntraTypesAsync(CancellationToken cancellationToken = default(CancellationToken)) => _inner.GetEnabledEntraTypesAsync(cancellationToken);

            public Task<int> CreateAsync(UserOrgType type, CancellationToken cancellationToken = default(CancellationToken)) => _inner.CreateAsync(type, cancellationToken);

            public Task UpdateAsync(UserOrgType type, bool clearAssignments, bool bumpGeneration, CancellationToken cancellationToken = default(CancellationToken), int? expectedGeneration = null, int? expectedRevision = null)
                => _inner.UpdateAsync(type, clearAssignments, bumpGeneration, cancellationToken, expectedGeneration);

            public Task DeleteAsync(int id, CancellationToken cancellationToken = default(CancellationToken)) => _inner.DeleteAsync(id, cancellationToken);

            public Task<int> RecordEntraRefreshAsync(IReadOnlyDictionary<int, int> expectedGenerations, DateTime refreshedUtc, CancellationToken cancellationToken = default(CancellationToken))
                => _inner.RecordEntraRefreshAsync(expectedGenerations, refreshedUtc, cancellationToken);
        }

        /// <summary>A Graph probe that lets something else happen while it is being waited on.</summary>
        private sealed class RacingGraph : IUserOrgGraphProbe
        {
            private readonly Func<Task> _meanwhile;

            public RacingGraph(Func<Task> meanwhile)
            {
                _meanwhile = meanwhile;
            }

            public int Calls { get; private set; }

            public async Task<UserOrgProbeOutcome> ResolveAsync(EntraOrgAttributeSpec spec, string upn, CancellationToken cancellationToken)
            {
                Calls++;
                await _meanwhile();

                // What Graph says for the synthetic probe user: the property is fine, the person does not exist.
                return new UserOrgProbeOutcome { Succeeded = false, RejectedTheProperty = false };
            }

            public Task<UserOrgAttributeCatalogueModel> DiscoverAsync(CancellationToken cancellationToken)
                => throw new NotSupportedException();
        }

        /// <summary>Previews, imports and applies a file through the service, then returns the job id.</summary>
        private async Task<int> ImportThroughTheService(int typeId, string csv, UserOrgImportMode mode = UserOrgImportMode.Merge)
        {
            var preview = await Preview(typeId, csv);
            var queued = await _service.CommitImportAsync(typeId, preview.DraftId.Value, mode, int.MaxValue, Admin, CancellationToken.None);
            var job = await new UserOrgImportRunner(_jobs).RunAsync(queued.JobId);
            Assert.AreEqual(UserOrgImportStatus.Succeeded, job.Status);
            return queued.JobId;
        }

        [TestMethod]
        public async Task TheChangeListIsReadBackFromWhereItWasWritten()
        {
            var a = AddUser("a@contoso.com");
            AddUser("b@contoso.com");
            var typeId = await NewType("Cost Centre");
            await _assignments.MergeAsync(new[] { new UserOrgAssignmentUpdate(a, typeId, "Old") });
            var jobId = await ImportThroughTheService(typeId, "UPN,Cost Centre\r\na@contoso.com,New\r\nb@contoso.com,Καλημέρα κόσμε\r\n");

            var pending = await _service.GetChangesAsync(jobId, null, null, 50, CancellationToken.None);
            Assert.AreEqual("pending", pending.Status, "Applied, not yet written.");
            Assert.AreEqual("pending", (await _service.GetJobAsync(jobId, CancellationToken.None)).ChangeLog);

            await new UserOrgChangeLogShipper(_jobs, _outbox, _types, () => _memoryLog).ShipAsync(jobId);

            var page = await _service.GetChangesAsync(jobId, null, null, 50, CancellationToken.None);
            Assert.AreEqual("available", page.Status);
            Assert.AreEqual("memory", page.Storage);
            Assert.AreEqual("memory", (await _service.GetJobAsync(jobId, CancellationToken.None)).ChangeLog);
            Assert.AreEqual(1, page.Summary.Added);
            Assert.AreEqual(1, page.Summary.Changed);
            Assert.AreEqual("Cost Centre", page.Summary.OrgTypeName);
            Assert.AreEqual(Admin, page.Summary.StartedBy);
            CollectionAssert.AreEqual(new[] { "a@contoso.com", "b@contoso.com" }, page.Items.Select(i => i.Upn).ToArray());
            Assert.AreEqual("Old", page.Items[0].Before);
            Assert.AreEqual("New", page.Items[0].After);
            Assert.AreEqual("changed", page.Items[0].Kind);
            Assert.AreEqual("added", page.Items[1].Kind);
            Assert.AreEqual("Καλημέρα κόσμε", page.Items[1].After);

            var searched = await _service.GetChangesAsync(jobId, "B", null, 50, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { "b@contoso.com" }, searched.Items.Select(i => i.Upn).ToArray());

            // A web app that has restarted since has an empty memory log: said as such, not as "no changes".
            var restarted = ServiceReadingFrom(new InMemoryUserOrgChangeLog());
            Assert.AreEqual("missing", (await restarted.GetChangesAsync(jobId, null, null, 50, CancellationToken.None)).Status);

            // A store that cannot be reached right now.
            var unreachable = ServiceReadingFrom(new UnreachableLog());
            Assert.AreEqual("unavailable", (await unreachable.GetChangesAsync(jobId, null, null, 50, CancellationToken.None)).Status);
        }

        [TestMethod]
        public async Task AskingForAListStillToBeWrittenTriesTheWriteAgain()
        {
            // A write the store refused is otherwise retried only by the next sweep - which the admin
            // waiting on the dialog would have to reload the page to trigger.
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var jobId = await ImportThroughTheService(typeId, "UPN,Team\r\na@contoso.com,X\r\n");
            _dispatchedChangeLogs.Clear();

            var page = await _service.GetChangesAsync(jobId, null, null, 50, CancellationToken.None);

            Assert.AreEqual("pending", page.Status);
            CollectionAssert.AreEqual(new[] { jobId }, _dispatchedChangeLogs);
        }

        [TestMethod]
        public async Task AListWaitingForTheStorageAccountSaysSoRatherThanStillBeingWritten()
        {
            // "Still being written, try again in a moment" would be untrue for as long as the account is
            // unreachable, which can be hours.
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var jobId = await ImportThroughTheService(typeId, "UPN,Team\r\na@contoso.com,X\r\n");
            _dispatchedChangeLogs.Clear();
            var waiting = new UserOrgAdminService(
                _types,
                _assignments,
                _jobs,
                UserOrgStores.CreateUserLookup(_db.ConnectionString),
                new NoGraph(),
                id => _dispatched.Add(id),
                resumeGate: new UserOrgResumeGate(),
                changeOutbox: _outbox,
                changeLogFor: status => null,
                dispatchChangeLog: id => _dispatchedChangeLogs.Add(id));

            var page = await waiting.GetChangesAsync(jobId, null, null, 50, CancellationToken.None);

            Assert.AreEqual("unavailable", page.Status);
            Assert.AreEqual("tableStorage", page.Storage);
            Assert.AreEqual(0, _dispatchedChangeLogs.Count, "Nothing to try until the account is back.");
        }

        [TestMethod]
        public async Task AnImportThatDidNotApplyHasNoChangeList()
        {
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var preview = await Preview(typeId, "UPN,Team\r\na@contoso.com,X\r\n");

            Assert.IsNull(await _service.GetChangesAsync(preview.DraftId.Value, null, null, 50, CancellationToken.None), "A draft is not an import.");

            await _service.CommitImportAsync(typeId, preview.DraftId.Value, UserOrgImportMode.Merge, 0, Admin, CancellationToken.None);
            _db.Execute($"UPDATE dbo.user_org_import_jobs SET status = 4 WHERE id = {preview.DraftId.Value}");

            var page = await _service.GetChangesAsync(preview.DraftId.Value, null, null, 50, CancellationToken.None);
            Assert.AreEqual("none", page.Status);
            Assert.IsNull((await _service.GetJobAsync(preview.DraftId.Value, CancellationToken.None)).ChangeLog);
        }

        [TestMethod]
        public async Task TheResumeSweepSendsUnwrittenChangeListsToBeWritten()
        {
            // The worker stopped between the apply and the write: the list is in the outbox, and the
            // sweep is what gets it written.
            AddUser("a@contoso.com");
            var typeId = await NewType();
            var jobId = await ImportThroughTheService(typeId, "UPN,Team\r\na@contoso.com,X\r\n");

            await _service.ResumeInterruptedImportsAsync(CancellationToken.None, force: true);

            CollectionAssert.AreEqual(new[] { jobId }, _dispatchedChangeLogs);
        }

        /// <summary>A change log whose store refuses every read.</summary>
        private sealed class UnreachableLog : IUserOrgChangeLog
        {
            public UserOrgChangeLogStatus Destination => UserOrgChangeLogStatus.Memory;

            public Task AppendAsync(UserOrgChangeLogImport import, IReadOnlyList<UserOrgChangeRecord> changes, CancellationToken cancellationToken)
                => throw new InvalidOperationException("unreachable");

            public Task CompleteAsync(UserOrgChangeLogImport import, CancellationToken cancellationToken)
                => throw new InvalidOperationException("unreachable");

            public Task<UserOrgChangeLogImport> GetImportAsync(string logId, CancellationToken cancellationToken)
                => throw new InvalidOperationException("unreachable");

            public Task<UserOrgChangeLogPage> GetChangesAsync(string logId, string search, string continuation, int pageSize, CancellationToken cancellationToken)
                => throw new InvalidOperationException("unreachable");
        }
    }
}
