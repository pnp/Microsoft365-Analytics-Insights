using Common.Entities;
using Common.Entities.UserScope;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Data.Entity.Infrastructure;
using System.Data.Entity.Migrations;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Rules;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;
using Configuration = Common.Entities.Migrations.Configuration;

namespace Tests.UnitTests
{
    /// <summary>
    /// Which metadata table the core merge ("Insert Activity from Staging Table.sql") files an event under, run through the real
    /// staging pass against a throwaway database with every migration applied, on a sharded staging table of its own.
    /// </summary>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class AuditMergeGeneralMetadataSqlTests
    {
        private static readonly ILogger Logger = NullLogger.Instance;
        private const string OneDriveSite = "https://contoso-my.sharepoint.com/personal/jane_contoso_com";
        private static ScratchDatabase _database;

        [ClassInitialize]
        public static void CreateMigratedDatabase(TestContext context)
        {
            _database = ScratchDatabase.Create("AuditMergeGeneral");
            new DbMigrator(new Configuration
            {
                TargetDatabase = new DbConnectionInfo(_database.ConnectionString, "Microsoft.Data.SqlClient"),
            }).Update();
        }

        [ClassCleanup]
        public static void DropDatabase()
        {
            SqlConnection.ClearAllPools();
            _database?.Dispose();
        }

        /// <summary>
        /// R1-4: a OneDrive file event is filed under event_meta_sharepoint, and used to get an event_meta_general row as
        /// well, because the general insert excluded 'SharePoint' but not 'OneDrive'.
        /// </summary>
        [TestMethod]
        public async Task CoreMerge_OneDriveFileEvent_IsFiledUnderSharePointOnly_AndARetryAddsNothing()
        {
            var upn = NewUpn();
            var oneDrive = FileEvent(upn, "OneDrive", OneDriveSite + $"/Documents/Καλημέρα κόσμε {Guid.NewGuid():N}.docx");
            var sharePoint = FileEvent(upn, "SharePoint", $"https://contoso.sharepoint.com/sites/example/Shared Documents/{Guid.NewGuid():N}.docx");

            await MergeAsync(oneDrive, sharePoint);
            await MergeAsync(oneDrive, sharePoint);     // the retry of a batch whose merge already ran

            foreach (var e in new[] { oneDrive, sharePoint })
            {
                Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.event_meta_sharepoint WHERE event_id = @id", e.Id), e.Workload);
                Assert.AreEqual(0, Count("SELECT COUNT(*) FROM dbo.event_meta_general WHERE event_id = @id", e.Id), e.Workload + ": no second, general, row.");
            }
        }

        /// <summary>
        /// A OneDrive event with no file URL gets no SharePoint row, so it keeps its general row rather than being left with no
        /// metadata row at all. A workload without a table of its own is filed as general, as before.
        /// </summary>
        [TestMethod]
        public async Task CoreMerge_EventsWithNoSharePointRow_StillGetAGeneralRow()
        {
            var upn = NewUpn();
            var oneDriveWithoutUrl = FileEvent(upn, "OneDrive", null);
            var general = new GeneralAuditLogContent
            {
                Id = Guid.NewGuid(),
                UserId = upn,
                CreationTime = DateTime.UtcNow,
                Workload = "MicrosoftForms",
                Operation = "ViewForm",
            };

            await MergeAsync(oneDriveWithoutUrl, general);

            Assert.AreEqual(0, Count("SELECT COUNT(*) FROM dbo.event_meta_sharepoint WHERE event_id = @id", oneDriveWithoutUrl.Id));
            Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.event_meta_general WHERE event_id = @id AND workload = 'OneDrive'", oneDriveWithoutUrl.Id));
            Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.event_meta_general WHERE event_id = @id AND workload = 'MicrosoftForms'", general.Id));
        }

        private static string NewUpn() => $"audit-merge-{Guid.NewGuid():N}@contoso.com";

        private static SharePointAuditLogContent FileEvent(string upn, string workload, string objectId) => new SharePointAuditLogContent
        {
            Id = Guid.NewGuid(),
            UserId = upn,
            CreationTime = DateTime.UtcNow,
            Workload = workload,
            Operation = "FileAccessed",
            ObjectId = objectId,
            SiteUrl = workload == "OneDrive" ? OneDriveSite : "https://contoso.sharepoint.com/sites/example",
            SourceFileName = "Καλημέρα κόσμε.docx",
            SourceFileExtension = "docx",
            ItemType = "File",
            EventData = "<event/>",
        };

        /// <summary>The production staging pass and merge, with nothing remembered from earlier attempts.</summary>
        private static async Task MergeAsync(params AbstractAuditLogContent[] events)
        {
            var set = new WebActivityReportSet();
            set.AddRange(events);
            var pass = new ActivityStagingPass(new AllowAllFilterConfig(), UserImportScope.Unfiltered, Logger);
            using (var db = new AnalyticsEntitiesContext(_database.ConnectionString, true, false))
            {
                var batch = new SqlActivityStagingWriter(Logger).CreateBatch(db);
                var result = await pass.RunAsync(set, ActivityImportCache.GetEmptyCache(), batch,
                    ActivitySaveConcurrencyPolicy.NewShardedStagingTableName(), null);
                Assert.AreEqual(events.Length, result.Stats.Imported);
            }
        }

        private static int Count(string sql, Guid id)
        {
            using (var con = new SqlConnection(_database.ConnectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@id", id);
                con.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }
    }
}
