using Common.Entities;
using Common.Entities.UserScope;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Persistence;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// The audit save path's SQL against the test database: the core merge ("Insert Activity from Staging
    /// Table.sql", through the real staging pass), the DLP merges and the metadata commit. Each test uses its own
    /// synthetic events and users and deletes them afterwards.
    /// </summary>
    [TestClass]
    public class AuditMergeSqlTests
    {
        private static readonly ILogger Logger = new LoggerFactory().CreateLogger("AuditMergeSqlTests");
        private static readonly string ConnectionString = new TestsAppConfig().ConnectionStrings.DatabaseConnectionString;
        private const string Site = "https://contoso.sharepoint.com/sites/merge-tests";

        #region Lookup values that used to fail the whole batch (A1)

        /// <summary>
        /// A file name longer than event_file_names.file_name (nvarchar(250)), and two extensions the database's
        /// varchar code page can't hold, failed the merge for the whole batch - and again on every retry, until
        /// the content blob aged out.
        /// </summary>
        [TestMethod]
        public async Task CoreMerge_OverlongFileNameAndExtensionsOutsideTheCodePage_SaveAndRetryCleanly()
        {
            var upn = NewUpn();
            var first = SpEvent(upn, LongGreekFileName(), "κείμενο");
            var second = SpEvent(upn, LongGreekFileName(), "έγγραφο");
            try
            {
                await Merge(first, second);
                await Merge(first, second);     // the retry of a batch whose merge already ran

                foreach (var e in new[] { first, second })
                {
                    var stored = StoredSharePointMetadata(e.Id);
                    Assert.AreEqual(e.SourceFileName.Substring(0, 250), stored.FileName, "The file name is trimmed to the column, not dropped.");
                    Assert.IsNotNull(stored.ExtensionId, "The extension must be linked even when the database can't hold its characters.");
                    Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.event_file_names WHERE file_name = @v", ("@v", stored.FileName)));
                    Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.event_file_ext WHERE extension_name = CONVERT(varchar(250), @v COLLATE DATABASE_DEFAULT)",
                        ("@v", e.SourceFileExtension)));
                }
            }
            finally
            {
                DeleteEvents(upn, first.Id, second.Id);
            }
        }

        /// <summary>
        /// A row an earlier, failed run of the merge saved without its lookup ids must be completed when the event
        /// is merged again: the insert skips an event_meta_sharepoint row that already exists.
        /// </summary>
        [TestMethod]
        public async Task CoreMerge_SharePointRowLeftWithoutLookupIds_IsRepairedByTheRetry()
        {
            var upn = NewUpn();
            var spEvent = SpEvent(upn, "Καλημέρα κόσμε " + Guid.NewGuid().ToString("N") + ".docx", "docx");
            try
            {
                await Merge(spEvent);
                Execute("UPDATE dbo.event_meta_sharepoint SET file_name_id = NULL, file_extension_id = NULL, item_type_id = NULL WHERE event_id = @id",
                    ("@id", spEvent.Id));

                await Merge(spEvent);

                var stored = StoredSharePointMetadata(spEvent.Id);
                Assert.AreEqual(spEvent.SourceFileName, stored.FileName);
                Assert.IsNotNull(stored.ExtensionId);
                Assert.IsNotNull(stored.ItemTypeId);
            }
            finally
            {
                DeleteEvents(upn, spEvent.Id);
            }
        }

        [TestMethod]
        public void TrimToLength_NeverSplitsASurrogatePair()
        {
            var name = new string('a', 249) + "\U0001F600" + "b";

            Assert.AreEqual(new string('a', 249), AuditStagingText.TrimToLength(name, 250));
            Assert.AreEqual(new string('a', 249) + "\U0001F600", AuditStagingText.TrimToLength(name, 251));
            Assert.AreEqual(name, AuditStagingText.TrimToLength(name, 252));
            Assert.AreEqual("Καλημέρα", AuditStagingText.TrimToLength("Καλημέρα", 250));
            Assert.IsNull(AuditStagingText.TrimToLength(null, 250));
        }

        #endregion

        #region DLP.All records (A3, A4)

        /// <summary>
        /// A DLP match detected in SharePoint used to be saved as a SharePoint file event (event_meta_sharepoint),
        /// counting as file activity; one detected in Exchange as mail activity. The DLP rows themselves must still
        /// be saved.
        /// </summary>
        [TestMethod]
        public async Task CoreMerge_DlpRuleMatches_AreNotSavedAsFileOrMailActivity()
        {
            var upn = NewUpn();
            var sharePointMatch = DlpEvent(upn, "SharePoint", $"{Site}/Shared Documents/{Guid.NewGuid():N} Καλημέρα κόσμε.docx");
            var exchangeMatch = DlpEvent(upn, "Exchange", "<00000000-0000-0000-0000-000000000000@contoso.onmicrosoft.com>");
            try
            {
                await Merge(sharePointMatch, exchangeMatch);
                var dlp = new DlpAuditEventManager(ConnectionString, Logger);
                foreach (var match in new[] { sharePointMatch, exchangeMatch })
                {
                    await dlp.SaveSingleDlpEventToSqlStaging(match, new CommonAuditEvent { Id = match.Id });
                }
                await dlp.CommitAllChanges();

                foreach (var match in new[] { sharePointMatch, exchangeMatch })
                {
                    Assert.AreEqual(0, Count("SELECT COUNT(*) FROM dbo.event_meta_sharepoint WHERE event_id = @id", ("@id", match.Id)));
                    Assert.AreEqual(0, Count("SELECT COUNT(*) FROM dbo.event_meta_exchange WHERE event_id = @id", ("@id", match.Id)));
                    Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.event_meta_general WHERE event_id = @id AND workload = 'DLP'", ("@id", match.Id)));
                    Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.dlp_rule_matches WHERE event_id = @id", ("@id", match.Id)),
                        "The rule match itself must still be saved.");
                }
                Assert.AreEqual(0, Count("SELECT COUNT(*) FROM dbo.urls WHERE full_url = @url", ("@url", sharePointMatch.ObjectId)),
                    "A DLP match must not add the file's URL to the SharePoint URL lookup.");
            }
            finally
            {
                DeleteEvents(upn, sharePointMatch.Id, exchangeMatch.Id);
            }
        }

        /// <summary>
        /// Against a database that hasn't had the DLP migration, the merges used to skip their rows and report
        /// success - so the batch's content blob was checkpointed and the matches were lost for good.
        /// </summary>
        [TestMethod]
        public async Task DlpMerges_DatabaseWithoutTheDlpTables_FailTheBatchInsteadOfDroppingTheRows()
        {
            using (var scratch = ScratchDatabase.Create("dlpschema"))
            {
                var ruleMatches = new SqlDlpStagingWriter(scratch.ConnectionString, Logger);
                ruleMatches.StageDlpRuleMatch(new DlpRuleMatchLogTempEntity { EventId = Guid.NewGuid(), PolicyId = "policy-1", PolicyName = "Contoso Finance", IsBlocked = true });
                var ex = await Assert.ThrowsExceptionAsync<BatchSaveException>(() => ruleMatches.CommitAllChanges());
                StringAssert.Contains(ex.Message, "Database upgrade required");

                var copilotMatches = new SqlDlpStagingWriter(scratch.ConnectionString, Logger);
                copilotMatches.StageCopilotDlp(new CopilotDlpLogTempEntity { EventId = Guid.NewGuid(), PolicyId = "policy-1", IsBlocked = true });
                ex = await Assert.ThrowsExceptionAsync<BatchSaveException>(() => copilotMatches.CommitAllChanges());
                StringAssert.Contains(ex.Message, "Database upgrade required");
            }
        }

        #endregion

        #region One workload's failure doesn't block the others (B1)

        [TestMethod]
        public async Task SaveSession_CopilotCommitFailure_StillSavesTheBatchsDlpMatches()
        {
            var upn = NewUpn();
            var match = DlpEvent(upn, "Exchange", "<00000000-0000-0000-0000-000000000000@contoso.onmicrosoft.com>");
            try
            {
                await Merge(match);

                var config = new TestsAppConfig();
                using (var session = new SaveSession(Logger, new AnalyticsEntitiesContext(), config, new FakeCopilotMetadataLoader()))
                {
                    await session.Init();

                    // An interaction whose audit event doesn't exist fails the Copilot merge on its foreign key.
                    await session.CopilotEventResolver.SaveSingleCopilotEventToSqlStaging(
                        new CopilotAuditLogContent { CopilotEventData = new CopilotEventData { AppHost = "Teams" } },
                        new CommonAuditEvent { Id = Guid.NewGuid(), User = new User { UserPrincipalName = upn } });
                    await session.DlpEventResolver.SaveSingleDlpEventToSqlStaging(match, new CommonAuditEvent { Id = match.Id });

                    await Assert.ThrowsExceptionAsync<BatchSaveException>(() => session.CommitAllChanges(),
                        "The batch must still fail, so it is retried.");
                }

                Assert.AreEqual(1, Count("SELECT COUNT(*) FROM dbo.dlp_rule_matches WHERE event_id = @id", ("@id", match.Id)),
                    "A Copilot merge failure must not stop the batch's DLP matches being saved.");
            }
            finally
            {
                DeleteEvents(upn, match.Id);
            }
        }

        #endregion

        #region Helpers

        private static string NewUpn() => $"audit-merge-{Guid.NewGuid():N}@contoso.onmicrosoft.com";

        /// <summary>A unique 260-character Greek file name - longer than event_file_names.file_name.</summary>
        private static string LongGreekFileName()
        {
            var name = Guid.NewGuid().ToString("N") + " " + string.Concat(Enumerable.Repeat("Καλημέρα κόσμε ", 20));
            return name.Substring(0, 255) + ".docx";
        }

        private static SharePointAuditLogContent SpEvent(string upn, string fileName, string extension) => new SharePointAuditLogContent
        {
            Id = Guid.NewGuid(),
            UserId = upn,
            CreationTime = DateTime.UtcNow,
            Workload = ActivityImportConstants.WORKLOAD_SP,
            Operation = "FileAccessed",
            ObjectId = $"{Site}/Shared Documents/{Guid.NewGuid():N}.docx",
            SiteUrl = Site,
            SourceFileName = fileName,
            SourceFileExtension = extension,
            ItemType = "File",
            EventData = "<event/>"
        };

        private static DlpAuditLogContent DlpEvent(string upn, string detectedIn, string objectId) => new DlpAuditLogContent
        {
            Id = Guid.NewGuid(),
            UserId = upn,
            CreationTime = DateTime.UtcNow,
            Workload = detectedIn,
            RecordType = ActivityImportConstants.DlpRecordTypes.ComplianceDLPSharePoint,
            Operation = "DlpRuleMatch",
            ObjectId = objectId,
            PolicyDetails = new List<AccessedResourcePolicyDetail>
            {
                new AccessedResourcePolicyDetail
                {
                    PolicyId = "policy-" + Guid.NewGuid().ToString("N"),
                    PolicyName = "Contoso Finance",
                    Rules = new List<AccessedResourcePolicyRule>
                    {
                        new AccessedResourcePolicyRule { RuleId = "rule-1", RuleName = "Card numbers", RuleMode = "Enforce", Actions = new List<string> { "BlockAccess" } }
                    }
                }
            }
        };

        /// <summary>The production staging pass and merge, with nothing remembered from earlier attempts.</summary>
        private static async Task Merge(params AbstractAuditLogContent[] events)
        {
            var set = new WebActivityReportSet();
            set.AddRange(events);
            var pass = new ActivityStagingPass(new AllowAllFilterConfig(), UserImportScope.Unfiltered, Logger);
            using (var db = new AnalyticsEntitiesContext())
            {
                var batch = new SqlActivityStagingWriter(Logger).CreateBatch(db);
                var result = await pass.RunAsync(set, ActivityImportCache.GetEmptyCache(), batch, null, null);
                Assert.AreEqual(events.Length, result.Stats.Imported);
            }
        }

        private sealed class StoredMetadata
        {
            public string FileName;
            public int? ExtensionId;
            public int? ItemTypeId;
        }

        private static StoredMetadata StoredSharePointMetadata(Guid eventId)
        {
            using (var con = new SqlConnection(ConnectionString))
            using (var cmd = new SqlCommand(@"
SELECT n.file_name, m.file_extension_id, m.item_type_id
FROM dbo.event_meta_sharepoint m
LEFT JOIN dbo.event_file_names n ON n.id = m.file_name_id
WHERE m.event_id = @id", con))
            {
                cmd.Parameters.AddWithValue("@id", eventId);
                con.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    Assert.IsTrue(reader.Read(), "The event's SharePoint metadata row must exist.");
                    return new StoredMetadata
                    {
                        FileName = reader.IsDBNull(0) ? null : reader.GetString(0),
                        ExtensionId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),
                        ItemTypeId = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                    };
                }
            }
        }

        private static int Count(string sql, params (string Name, object Value)[] parameters)
        {
            using (var con = new SqlConnection(ConnectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value);
                con.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        private static void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using (var con = new SqlConnection(ConnectionString))
            using (var cmd = new SqlCommand(sql, con))
            {
                foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value);
                con.Open();
                cmd.ExecuteNonQuery();
            }
        }

        private static void DeleteEvents(string upn, params Guid[] eventIds)
        {
            foreach (var id in eventIds)
            {
                Execute(@"
DELETE FROM dbo.dlp_rule_matches WHERE event_id = @id;
DELETE FROM dbo.event_meta_sharepoint WHERE event_id = @id;
DELETE FROM dbo.event_meta_exchange WHERE event_id = @id;
DELETE FROM dbo.event_meta_general WHERE event_id = @id;
DELETE FROM dbo.audit_events WHERE id = @id;", ("@id", id));
            }
            Execute("DELETE FROM dbo.users WHERE user_name = @upn AND NOT EXISTS (SELECT 1 FROM dbo.audit_events e INNER JOIN dbo.users u ON u.id = e.user_id WHERE u.user_name = @upn);",
                ("@upn", upn));
        }

        #endregion
    }
}
