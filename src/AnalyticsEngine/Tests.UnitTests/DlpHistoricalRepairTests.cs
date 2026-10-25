using Common.Entities;
using Common.Entities.State;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp;

namespace Tests.UnitTests
{
    [TestClass]
    public class DlpHistoricalRepairTests
    {
        private static readonly DateTime From = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime To = From.AddDays(1);

        [TestMethod]
        public async Task Repair_OnlyProvenDlpMetadataIsRemoved_CoreAndPolicyEvidenceSurviveReplayAndPaging()
        {
            using (var db = CreateFixture())
            {
                var archive = new RecordingArchive();
                var first = await Run(db, null, 2, archive);
                var second = await Run(db, first, 2, archive);
                var end = await Run(db, second, 2, archive);
                Assert.AreEqual(2, first.Scanned);
                Assert.AreEqual(2, second.Scanned);
                Assert.AreEqual(0, end.Scanned);
                Assert.AreEqual(4, first.SharePointRowsRemoved + second.SharePointRowsRemoved);
                Assert.AreEqual(4, first.ExchangeRowsRemoved + second.ExchangeRowsRemoved);
                Assert.AreEqual(4, first.GeneralRowsReclassified + second.GeneralRowsReclassified);
                Assert.AreEqual(6, Count(db, "audit_events"));
                Assert.AreEqual(1, Count(db, "dlp_rule_matches"));
                Assert.AreEqual(2, Count(db, "event_meta_sharepoint"), "Genuine file activity and a Dlp-prefixed non-DLP operation are untouched.");
                Assert.AreEqual(2, Count(db, "event_meta_exchange"));
                Assert.AreEqual(6, Scalar(db, "SELECT COUNT(*) FROM dbo.event_meta_general WHERE json = N'{\"synthetic\":\"Contoso\"}'"));
                Assert.AreEqual(4, archive.Values.Count);
                foreach (var json in archive.Values.Values)
                {
                    var payload = Newtonsoft.Json.Linq.JObject.Parse(json);
                    Assert.AreEqual("Καλημέρα κόσμε.docx", (string)payload["sharePoint"]["fileName"]["file_name"]);
                    Assert.AreEqual("<synthetic-message@contoso.com>", (string)payload["exchange"]["object_id"]);
                }
                var replay = await Run(db, null, 500, archive);
                Assert.AreEqual(4, replay.Scanned);
                Assert.AreEqual(0, replay.SharePointRowsRemoved + replay.ExchangeRowsRemoved + replay.GeneralRowsReclassified);
                Assert.AreEqual(4, archive.Values.Count, "Replay must not replace original context with an empty archive.");
            }
        }

        [DataTestMethod]
        [DataRow("column")]
        [DataRow("foreign-key")]
        [DataRow("trigger")]
        public async Task Repair_CustomMetadataPayloadOrSideEffects_FailsWithoutDeletingAnything(string customisation)
        {
            using (var db = CreateFixture())
            {
                if (customisation == "column")
                {
                    db.Execute("ALTER TABLE dbo.event_meta_sharepoint ADD contoso_custom_text nvarchar(100) NULL;");
                    db.Execute("UPDATE dbo.event_meta_sharepoint SET contoso_custom_text = N'Καλημέρα κόσμε';");
                }
                else if (customisation == "foreign-key")
                {
                    db.Execute(@"ALTER TABLE dbo.event_meta_sharepoint ADD PRIMARY KEY (event_id);
CREATE TABLE dbo.contoso_custom_payload (event_id uniqueidentifier REFERENCES dbo.event_meta_sharepoint(event_id) ON DELETE CASCADE,
    note nvarchar(100));
INSERT dbo.contoso_custom_payload SELECT event_id, N'Contoso synthetic payload' FROM dbo.event_meta_sharepoint;");
                }
                else
                {
                    db.Execute("CREATE TRIGGER dbo.contoso_custom_delete ON dbo.event_meta_sharepoint AFTER DELETE AS BEGIN SET NOCOUNT ON; END;");
                }
                var error = await Assert.ThrowsExceptionAsync<SqlException>(() => Run(db, null, 500));
                Assert.AreEqual(50003, error.Number);
                Assert.AreEqual(6, Count(db, "event_meta_sharepoint"));
                Assert.AreEqual(6, Count(db, "event_meta_exchange"));
                Assert.AreEqual(0, Scalar(db, "SELECT COUNT(*) FROM dbo.event_meta_general WHERE workload = 'DLP'"));
                if (customisation == "foreign-key") Assert.AreEqual(6, Count(db, "contoso_custom_payload"));
            }
        }

        [TestMethod]
        public async Task Repair_ArchiveUnavailable_DoesNotRemoveMetadataOrReclassifyGeneralEvidence()
        {
            using (var db = CreateFixture())
            {
                var archive = new RecordingArchive { BeforeWrite = () => throw new InvalidOperationException("Synthetic Table outage") };
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Run(db, null, 500, archive));
                Assert.AreEqual(6, Count(db, "event_meta_sharepoint"));
                Assert.AreEqual(6, Count(db, "event_meta_exchange"));
                Assert.AreEqual(0, Scalar(db, "SELECT COUNT(*) FROM dbo.event_meta_general WHERE workload = 'DLP'"));
            }
        }

        [DataTestMethod]
        [DataRow(32768, true)]
        [DataRow(32769, false)]
        public async Task Repair_ExchangeObjectIdArchiveBound_PreservesTheExactUnicodeBoundary(int characters, bool fits)
        {
            using (var db = CreateFixture())
            {
                db.Execute("UPDATE dbo.event_meta_exchange SET object_id = REPLICATE(CAST(N'Κ' AS nvarchar(max)), " + characters + ");");
                var archive = new RecordingArchive();
                if (fits)
                {
                    await Run(db, null, 500, archive);
                    foreach (var json in archive.Values.Values)
                        Assert.AreEqual(new string('Κ', characters),
                            (string)Newtonsoft.Json.Linq.JObject.Parse(json)["exchange"]["object_id"]);
                    Assert.AreEqual(2, Count(db, "event_meta_exchange"));
                }
                else
                {
                    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => Run(db, null, 500, archive));
                    Assert.AreEqual(6, Count(db, "event_meta_sharepoint"));
                    Assert.AreEqual(6, Count(db, "event_meta_exchange"));
                    Assert.AreEqual(0, Scalar(db, "SELECT COUNT(*) FROM dbo.event_meta_general WHERE workload = 'DLP'"));
                    Assert.AreEqual(0, archive.Values.Count);
                }
            }
        }

        [TestMethod]
        public async Task Repair_MetadataChangesAfterArchive_AbortsAndRetainsBothVersionsOnRetry()
        {
            using (var db = CreateFixture())
            {
                var changed = 0;
                var archive = new RecordingArchive
                {
                    BeforeWrite = () =>
                    {
                        if (Interlocked.Exchange(ref changed, 1) == 0)
                            db.Execute("UPDATE dbo.event_meta_exchange SET object_id = '<changed-synthetic@contoso.com>' WHERE event_id = '00000000-0000-0000-0000-000000000001';");
                    }
                };
                var failure = await Assert.ThrowsExceptionAsync<SqlException>(() => Run(db, null, 500, archive));
                Assert.AreEqual(50004, failure.Number);
                Assert.AreEqual(6, Count(db, "event_meta_sharepoint"));
                Assert.AreEqual(6, Count(db, "event_meta_exchange"));
                await Run(db, null, 500, archive);
                Assert.AreEqual(5, archive.Values.Count, "Changed context receives a new immutable content-addressed version.");
                Assert.IsTrue(archive.Values.Values.Any(json => json.Contains("<changed-synthetic@contoso.com>")));
                Assert.IsTrue(archive.Values.Values.Any(json => json.Contains("<synthetic-message@contoso.com>")));
            }
        }

        [TestMethod]
        [TestCategory("Azurite")]
        public async Task Repair_AzureTableArchive_RetainsTheRemovedUnicodeContext()
        {
            using (var socket = new System.Net.Sockets.TcpClient())
            {
                try { await socket.ConnectAsync("127.0.0.1", 10002); }
                catch (System.Net.Sockets.SocketException)
                {
                    if (Environment.GetEnvironmentVariable("REQUIRE_AZURITE") == "true") Assert.Fail("Azurite is required.");
                    Assert.Inconclusive("Azurite is not listening on port 10002.");
                }
            }
            var table = new Azure.Data.Tables.TableClient("UseDevelopmentStorage=true", "DlpArchiveTest" + Guid.NewGuid().ToString("N"));
            await table.CreateIfNotExistsAsync();
            try
            {
                using (var db = CreateFixture())
                {
                    var store = new AzureTableKeyValueStore(table, "DlpArchive");
                    await Run(db, null, 500, store);
                    var rows = table.Query<Azure.Data.Tables.TableEntity>().ToList();
                    Assert.AreEqual(4, rows.Count);
                    foreach (var row in rows)
                    {
                        var payload = Newtonsoft.Json.Linq.JObject.Parse(await store.GetStringAsync(row.GetString(AzureTableKeyValueStore.KeyProperty)));
                        Assert.AreEqual("Καλημέρα κόσμε.docx", (string)payload["sharePoint"]["fileName"]["file_name"]);
                    }
                    Assert.AreEqual(2, Count(db, "event_meta_sharepoint"));
                }
            }
            finally { await table.DeleteAsync(); }
        }

        /// <summary>
        /// D-5: pages follow <c>(time_stamp, id)</c>, the order <c>IX_audit_events_time_stamp</c> provides, whatever the
        /// user ids. Paging on <c>(time_stamp, user_id, id)</c> needed a sort the index cannot supply.
        /// </summary>
        [TestMethod]
        public async Task Repair_PagesInTimeThenEventIdOrder_RegardlessOfUser()
        {
            using (var db = CreateFixture())
            {
                // User ids descend as event ids ascend, so the old (time, user, id) order would visit the events backwards.
                db.Execute("UPDATE dbo.audit_events SET user_id = 10 - CAST(RIGHT(CAST(id AS varchar(36)), 1) AS int);");
                var visited = new System.Collections.Generic.List<Guid>();
                DlpHistoricalRepairBatch page = null;
                while (true)
                {
                    page = await Run(db, page, 1);
                    if (page.Scanned == 0) break;
                    visited.Add(page.AfterEventId.Value);
                }
                CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }.Select(n => new Guid("00000000-0000-0000-0000-00000000000" + n)).ToList(), visited);
            }
        }

        [TestMethod]
        public void RepairCheckpoint_FromTheUserOrderedBuild_IsRefusedWithAClearMessage()
        {
            var from = From;
            var to = To;
            var old = Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                Target = "synthetic-target", FromUtc = from, ToUtc = to,
                Batch = new { Scanned = 500, AfterTime = from, AfterUserId = 7, AfterEventId = Guid.Empty }
            });
            var error = Assert.ThrowsException<ArgumentException>(() => DlpHistoricalRepairCommand.ReadCheckpoint(old, "synthetic-target", from, to));
            StringAssert.Contains(error.Message, "format version 0");
            StringAssert.Contains(error.Message, "new checkpoint file");

            var current = Newtonsoft.Json.JsonConvert.SerializeObject(new DlpHistoricalRepairCommand.Checkpoint
            {
                Version = DlpHistoricalRepairCommand.CheckpointVersion, Target = "synthetic-target", FromUtc = from, ToUtc = to,
                Batch = new DlpHistoricalRepairBatch { AfterTime = from, AfterEventId = Guid.Empty }
            });
            Assert.AreEqual(Guid.Empty, DlpHistoricalRepairCommand.ReadCheckpoint(current, "synthetic-target", from, to).Batch.AfterEventId);
        }

        private static Task<DlpHistoricalRepairBatch> Run(ScratchDatabase db, DlpHistoricalRepairBatch after, int take, IKeyValueStore archive = null) =>
            DlpHistoricalRepair.RunBatchAsync(db.ConnectionString, From, To, after?.AfterTime,
                after?.AfterEventId, take, NullLogger.Instance, archive ?? new RecordingArchive());

        private static ScratchDatabase CreateFixture()
        {
            string catalog;
            using (var context = new AnalyticsEntitiesContext())
            {
                context.Database.Initialize(false);
                catalog = new SqlConnectionStringBuilder(context.Database.Connection.ConnectionString).InitialCatalog;
            }
            var db = ScratchDatabase.Create("dlp_history");
            try
            {
                var quoted = "[" + catalog.Replace("]", "]]") + "]";
                foreach (var table in new[] { "audit_events", "event_operations", "dlp_rule_matches", "event_meta_sharepoint", "event_meta_exchange", "event_meta_general",
                    "event_file_names", "event_file_ext", "urls", "webs", "event_types" })
                    db.Execute($"SELECT TOP (0) * INTO dbo.[{table}] FROM {quoted}.dbo.[{table}];");
                db.Execute(@"
INSERT dbo.event_operations (operation_name) VALUES ('DlpRuleMatch'), ('DlpRuleUndo'), ('DlpInfo'), ('UnrecognisedOperation'), ('DlpRuleMatchPreview'), ('FileAccessed');
INSERT dbo.audit_events (id, time_stamp, operation_id, user_id)
SELECT CAST('00000000-0000-0000-0000-00000000000' + CAST(id AS varchar(1)) AS uniqueidentifier), '20000101', id, NULL
FROM dbo.event_operations;
INSERT dbo.dlp_rule_matches (event_id, is_blocked) VALUES ('00000000-0000-0000-0000-000000000004', 1);
INSERT dbo.event_meta_sharepoint (event_id) SELECT id FROM dbo.audit_events;
INSERT dbo.event_meta_exchange (event_id) SELECT id FROM dbo.audit_events;
INSERT dbo.event_meta_general (event_id, workload, json) SELECT id, 'SharePoint', N'{""synthetic"":""Contoso""}' FROM dbo.audit_events;");
                db.Execute(@"
INSERT dbo.event_file_names (file_name) VALUES (N'Καλημέρα κόσμε.docx');
INSERT dbo.urls (full_url) VALUES (N'https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.docx');
UPDATE dbo.event_meta_sharepoint SET file_name_id = (SELECT TOP (1) id FROM dbo.event_file_names), url_id = (SELECT TOP (1) id FROM dbo.urls);
UPDATE dbo.event_meta_exchange SET object_id = '<synthetic-message@contoso.com>';");
                return db;
            }
            catch { db.Dispose(); throw; }
        }

        private static int Count(ScratchDatabase db, string table) => Scalar(db, $"SELECT COUNT(*) FROM dbo.[{table}]");
        private static int Scalar(ScratchDatabase db, string sql)
        {
            using (var connection = new SqlConnection(db.ConnectionString))
            using (var command = new SqlCommand(sql, connection))
            {
                connection.Open();
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private sealed class RecordingArchive : IKeyValueStore
        {
            public ConcurrentDictionary<string, string> Values { get; } = new ConcurrentDictionary<string, string>();
            public Action BeforeWrite { get; set; }
            public string Description => "Synthetic archive";
            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) =>
                Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
            {
                BeforeWrite?.Invoke();
                Values[key] = value;
                return Task.CompletedTask;
            }
            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Values.ContainsKey(key));
        }
    }
}
