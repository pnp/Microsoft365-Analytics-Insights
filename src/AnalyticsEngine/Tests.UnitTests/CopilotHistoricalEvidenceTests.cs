using Common.Entities;
using DataUtils;
using Common.Entities.Copilot;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using ActivityImporter.Engine.ActivityAPI.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotHistoricalEvidenceTests
    {
        [ClassInitialize]
        public static void Initialise(TestContext context)
        {
            var config = new TestsAppConfig();
            using (var db = new AnalyticsEntitiesContext(config.ConnectionStrings.DatabaseConnectionString, true, true))
                db.Database.Initialize(true);
        }

        [TestMethod]
        public async Task Repair_OnlyClassifiesProvenStoredIdentities_AndResumesPastUnknownRows()
        {
            using (var db = new AnalyticsEntitiesContext())
            using (var transaction = db.Database.BeginTransaction())
            {
                var suffix = Guid.NewGuid().ToString("N");
                var ids = new[] { "Contoso.Unknown." + suffix, "SPO_" + suffix, Guid.NewGuid().ToString(),
                    "CopilotStudio.CustomEngine." + suffix, "Copilot.M365Copilot." + suffix,
                    "CopilotStudio.Declarative." + suffix };
                foreach (var id in ids)
                    await db.Database.ExecuteSqlCommandAsync(
                        "INSERT dbo.copilot_agents (agent_id, name, is_custom_agent) VALUES (@id, N'Καλημέρα κόσμε', NULL)",
                        new SqlParameter("@id", id));
                // More than the production batch limit: persisted flags are the restart checkpoint.
                await db.Database.ExecuteSqlCommandAsync(@"
INSERT dbo.copilot_agents (agent_id, name)
SELECT TOP (1001) N'CopilotStudio.Future.' + CONVERT(nvarchar(36), NEWID()), N'Contoso'
FROM sys.all_objects a CROSS JOIN sys.all_objects b;");
                var first = await Repair(db);
                Assert.AreEqual(1000, first.ClassifiedAgents);
                var second = await Repair(db);
                Assert.IsTrue(second.ClassifiedAgents > 0);
                Assert.AreEqual(0, (await Repair(db)).ClassifiedAgents);
                foreach (var id in ids)
                {
                    var row = await db.CopilotAgents.AsNoTracking().SingleAsync(a => a.AgentID == id);
                    Assert.AreEqual(CopilotAgentClassifier.ClassifyStoredAgentId(id).IsCustomAgentFlag, row.IsCustomAgent);
                    Assert.AreEqual("Καλημέρα κόσμε", row.Name, "The real nvarchar(100) customer-name boundary is unchanged.");
                }
            }
        }

        [TestMethod]
        public async Task Repair_RestoresOnlyUnsupportedEchoMappings_InBoundedResumableBatches()
        {
            using (var db = new AnalyticsEntitiesContext())
            using (var transaction = db.Database.BeginTransaction())
            {
                var audit = new CommonAuditEvent
                {
                    Id = Guid.NewGuid(),
                    TimeStamp = new DateTime(2026, 1, 5),
                    User = new User { UserPrincipalName = "history." + Guid.NewGuid().ToString("N") + "@contoso.com" },
                    Operation = new EventOperation { Name = "Contoso history repair" }
                };
                db.AuditEventsCommon.Add(audit);
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlCommandAsync(@"
DELETE FROM dbo.copilot_chat_duplicates WHERE reason = 2;
CREATE TABLE #history_events (id uniqueidentifier NOT NULL PRIMARY KEY);
INSERT #history_events SELECT TOP (50002) NEWID() FROM sys.all_objects a CROSS JOIN sys.all_objects b;
INSERT dbo.audit_events (id, time_stamp, user_id, operation_id)
SELECT h.id, '20260105', a.user_id, a.operation_id
FROM #history_events h CROSS JOIN dbo.audit_events a WHERE a.id = @audit;
INSERT dbo.copilot_chats (event_id, time_stamp, app_host, copilot_credit_estimate_total)
SELECT id, '20260105', N'Microsoft Teams', 0 FROM #history_events;
DECLARE @counted uniqueidentifier = NEWID(), @pair uniqueidentifier = (SELECT TOP (1) id FROM #history_events ORDER BY id);
INSERT dbo.copilot_chat_duplicates (event_id, time_stamp, counted_event_id, reason)
SELECT id, '20260105', @counted, CASE WHEN id = @pair THEN 1 ELSE 2 END FROM #history_events;",
                    new SqlParameter("@audit", audit.Id));
                Assert.AreEqual(50000, (await Repair(db)).RestoredTurns);
                Assert.AreEqual(1, (await Repair(db)).RestoredTurns);
                Assert.AreEqual(0, (await Repair(db)).RestoredTurns);
                Assert.AreEqual(1, await db.Database.SqlQuery<int>(@"
SELECT COUNT(*) FROM dbo.copilot_chat_duplicates d JOIN dbo.audit_events a ON a.id = d.event_id
WHERE a.user_id = @user", new SqlParameter("@user", audit.UserId)).SingleAsync());
                Assert.AreEqual(50002, await db.Database.SqlQuery<int>(@"
SELECT COUNT(*) FROM dbo.copilot_chats c JOIN dbo.audit_events a ON a.id = c.event_id
WHERE a.user_id = @user AND c.copilot_credit_estimate_total = 0",
                    new SqlParameter("@user", audit.UserId)).SingleAsync(), "Never invent credits or delete audit records.");
            }
        }

        [TestMethod]
        public async Task Templates_EmbedTheExactSql_AndOriginMatchesTheSharedClassifier()
        {
            var root = ReportDirectory();
            foreach (var template in new[] { "Copilot Agent Interactions.pbit", "Copilot Activity Dashboard.pbit" })
                using (var zip = ZipFile.OpenRead(Path.Combine(root, template)))
                {
                    JObject model;
                    using (var reader = new StreamReader(zip.GetEntry("DataModelSchema").Open(), System.Text.Encoding.Unicode))
                        model = JObject.Parse(reader.ReadToEnd());
                    foreach (var table in model["model"]["tables"])
                    {
                        var file = new System.Collections.Generic.Dictionary<string, string>
                        {
                            ["Copilot Agents"] = "agents.sql",
                            ["Copilot Chats"] = "chats.sql",
                            ["Copilot Accessed Resource Lookups"] = "resources.sql",
                            ["CopilotActivity"] = "activity.sql"
                        };
                        if (!file.TryGetValue((string)table["name"], out var source)) continue;
                        var expression = table["partitions"][0]["source"]["expression"].Values<string>()
                            .Single(l => l.Contains("[Query=\""));
                        var sql = expression.Substring(expression.IndexOf("[Query=\"", StringComparison.Ordinal) + 8);
                        sql = sql.Substring(0, sql.Length - 3).Replace("\"\"", "\"").Replace("#(lf)", "\n");
                        Assert.AreEqual(File.ReadAllText(Path.Combine(root, source)).Trim().Replace("\r\n", "\n"), sql);
                    }
                }
        }

        [TestMethod]
        public async Task RetainedEventData_RepairsProvenCredits_NotUnknownKind_AndReplaysSafelyWithHistoricalWriters()
        {
            var from = new DateTime(2001, 1, 5, 0, 0, 0, DateTimeKind.Utc).AddSeconds(
                Math.Abs((long)Guid.NewGuid().GetHashCode()));
            var raw = @"{""AppHost"":""Office"",""ConversationId"":""contoso-history"",
""Messages"":[{""Id"":""response"",""isPrompt"":false}],
""AccessedResources"":[{""Id"":""https://contoso.sharepoint.com/sites/example/Καλημέρα.pdf"",
""Name"":""Καλημέρα κόσμε.pdf"",""Type"":""docx"",""Action"":""Read""}]}";
            var proven = await AddHistorical(from.AddSeconds(10), "CopilotStudio.CustomEngine." + Guid.NewGuid(), raw);
            var unknown = await AddHistorical(from.AddSeconds(20), Guid.NewGuid().ToString(), raw);
            var malformed = await AddHistorical(from.AddSeconds(30), "Contoso.Unknown." + Guid.NewGuid(), "not-json");
            var future = await AddHistorical(from.AddSeconds(40), "CopilotStudio.CustomEngine." + Guid.NewGuid(), raw, "9.0.0.0");
            var first = await CopilotHistoricalRepair.RunBatchAsync(_Connection(), from, from.AddMinutes(1), null, null, 1, NullLogger.Instance);
            Assert.AreEqual(1, first.Scanned);
            Assert.AreEqual(12, await Credit(proven));
            // Another writer inserts behind the committed timestamp while the repair is paginating.
            var late = await AddHistorical(from.AddSeconds(1), "CopilotStudio.CustomEngine." + Guid.NewGuid(), raw);
            var next = first;
            var skipped = 0;
            do
            {
                var batch = await CopilotHistoricalRepair.RunBatchAsync(_Connection(), from, from.AddMinutes(1),
                    next.AfterTime, next.AfterEventId, 1, NullLogger.Instance, next.AfterUserId);
                skipped += batch.Unrecoverable;
                if (batch.Scanned == 0) break;
                next = batch;
            } while (true);
            Assert.AreEqual(1, skipped, "Malformed retained data is explicitly reported, not guessed.");
            Assert.AreEqual(0, await Credit(late), "A watermark cannot promise to catch historical inserts behind it.");
            await CopilotHistoricalRepair.RunBatchAsync(_Connection(), from, from.AddMinutes(1), null, null, 500, NullLogger.Instance);
            await CopilotHistoricalRepair.RunBatchAsync(_Connection(), from, from.AddMinutes(1), null, null, 500, NullLogger.Instance);
            Assert.AreEqual(12, await Credit(proven));
            Assert.AreEqual(12, await Credit(late), "A fresh window pass catches the older concurrent insertion.");
            Assert.AreEqual(0, await Credit(unknown), "A bare GUID without retained kind evidence cannot be priced.");
            Assert.AreEqual(0, await Credit(malformed));
            Assert.AreEqual(0, await Credit(future), "Never overwrite an unrecognised cost model.");
            using (var db = new AnalyticsEntitiesContext())
            {
                Assert.AreEqual(1, await db.Database.SqlQuery<int>(
                    "SELECT COUNT(*) FROM dbo.copilot_event_accessed_resources WHERE copilot_chat_id = @id",
                    new SqlParameter("@id", proven)).SingleAsync(), "Reruns do not duplicate attributed resources.");
                Assert.AreEqual("Καλημέρα κόσμε.pdf", await db.Database.SqlQuery<string>(@"
SELECT n.name FROM dbo.copilot_event_accessed_resources r
JOIN dbo.copilot_event_accessed_resource_names n ON n.id = r.resource_name_id
WHERE r.copilot_chat_id = @id", new SqlParameter("@id", proven)).SingleAsync());
            }
        }

        [TestMethod]
        public async Task Replay_BoundsPayloads_AndSkipsUnknownShapesWithoutInventingCredits()
        {
            var at = new DateTime(2005, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(
                new Random(Guid.NewGuid().GetHashCode()).Next(1, 10000000));
            var ids = new List<Guid>();
            var invalid = new[] { "[]", "{\"AppHost\":{}}", "{\"AppHost\":\"Office\",\"Messages\":\"unknown shape\"}",
                "{\"AppHost\":\"Office\",\"Padding\":\"" + new string('λ', 600000) + "\"}" };
            for (var i = 0; i < invalid.Length; i++)
                ids.Add(await AddHistorical(at.AddSeconds(i), "CopilotStudio.CustomEngine." + Guid.NewGuid(), invalid[i]));
            var large = "{\"AppHost\":\"Contoso unknown host\",\"Padding\":\"" + new string('λ', 500000) + "\"}";
            for (var i = 0; i < 9; i++)
                ids.Add(await AddHistorical(at.AddSeconds(10 + i), "Contoso.Unknown." + Guid.NewGuid(), large));

            var first = await CopilotHistoricalRepair.RunBatchAsync(_Connection(), at, at.AddMinutes(1),
                null, null, 500, NullLogger.Instance);
            Assert.AreEqual(12, first.Scanned, "The ninth large retained payload must wait for the next 8 MB batch.");
            Assert.AreEqual(4, first.Unrecoverable, "Unknown shapes and oversized data must be reported, not coerced.");
            var second = await CopilotHistoricalRepair.RunBatchAsync(_Connection(), at, at.AddMinutes(1),
                first.AfterTime, first.AfterEventId, 500, NullLogger.Instance, first.AfterUserId);
            Assert.AreEqual(1, second.Scanned, "The buffered-text limit must not skip its unread checkpoint row.");
            foreach (var id in ids) Assert.AreEqual(0, await Credit(id));
        }

        [TestMethod]
        public async Task Replay_KeysetRetainsAllUsersAtTheSameTimestampIncludingNullUser()
        {
            var at = new DateTime(2004, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(
                new Random(Guid.NewGuid().GetHashCode()).Next(1, 10000000));
            var ids = new HashSet<Guid>();
            for (var i = 0; i < 6; i++)
                ids.Add(await AddHistorical(at, "Contoso.Unknown." + Guid.NewGuid().ToString("N"),
                    "{\"AppHost\":\"Contoso unknown host\"}"));
            using (var db = new AnalyticsEntitiesContext())
                await db.Database.ExecuteSqlCommandAsync("UPDATE dbo.copilot_chats SET user_id = NULL WHERE event_id = @id",
                    new SqlParameter("@id", ids.First()));

            DateTime? afterTime = null;
            Guid? afterId = null;
            int? afterUserId = null;
            var visited = new HashSet<Guid>();
            for (var i = 0; i < 7; i++)
            {
                var page = await CopilotHistoricalRepair.RunBatchAsync(_Connection(), at, at.AddSeconds(1),
                    afterTime, afterId, 1, NullLogger.Instance, afterUserId);
                if (page.Scanned == 0) break;
                Assert.IsTrue(visited.Add(page.AfterEventId.Value), "A checkpoint must not revisit an earlier user.");
                afterTime = page.AfterTime;
                afterId = page.AfterEventId;
                afterUserId = page.AfterUserId;
                if (i == 0)
                    using (var db = new AnalyticsEntitiesContext())
                        await db.Database.ExecuteSqlCommandAsync(
                            "DELETE dbo.copilot_chats WHERE event_id = @id", new SqlParameter("@id", afterId));
            }
            CollectionAssert.AreEquivalent(ids.ToList(), visited.ToList());
        }

        private static string _Connection() => new TestsAppConfig().ConnectionStrings.DatabaseConnectionString;
        private static async Task<int?> Credit(Guid id)
        {
            using (var db = new AnalyticsEntitiesContext())
                return await db.CopilotChats.Where(c => c.EventID == id).Select(c => c.CopilotCreditEstimateTotal).SingleAsync();
        }

        private static async Task<Guid> AddHistorical(DateTime at, string identity, string data, string version = "1.1.0.0")
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var audit = new CommonAuditEvent
                {
                    Id = Guid.NewGuid(),
                    TimeStamp = at,
                    EventData = data,
                    Operation = new EventOperation { Name = "Contoso history " + Guid.NewGuid().ToString("N") },
                    User = new User { UserPrincipalName = "history." + Guid.NewGuid().ToString("N") + "@contoso.com" }
                };
                db.AuditEventsCommon.Add(audit);
                await db.SaveChangesAsync();
                db.CopilotChats.Add(new Common.Entities.Entities.AuditLog.CopilotChat
                {
                    AuditEvent = audit,
                    TimeStampUtc = at,
                    UserId = audit.UserId,
                    AppHost = "Office",
                    Agent = new Common.Entities.Entities.AuditLog.CopilotAgent { AgentID = identity, Name = "Καλημέρα κόσμε" },
                    CopilotCreditEstimateTotal = 0,
                    CopilotCreditEstimateJson = "{\"CostModelVersion\":\"" + version + "\",\"TotalCredits\":0}"
                });
                await db.SaveChangesAsync();
                return audit.Id;
            }
        }

        [TestMethod]
        public async Task TemplateOrigins_MatchTheSharedClassifier()
        {
            var root = ReportDirectory();
            using (var db = new AnalyticsEntitiesContext())
            using (var transaction = db.Database.BeginTransaction())
            {
                var ids = new[] { "CopilotStudio.Declarative.Contoso", "Copilot.M365Copilot.Contoso",
                    "SPO_Contoso", "BuiltIn_Contoso", Guid.NewGuid().ToString(), "Contoso.Unknown" };
                foreach (var id in ids)
                    foreach (var flag in new bool?[] { null, false, true })
                    {
                        var key = await db.Database.SqlQuery<int>(@"
INSERT dbo.copilot_agents (agent_id, name, is_custom_agent)
OUTPUT INSERTED.id VALUES (@id, N'Καλημέρα κόσμε', @flag)",
                            new SqlParameter("@id", id), new SqlParameter("@flag", (object)flag ?? DBNull.Value)).SingleAsync();
                        var sql = File.ReadAllText(Path.Combine(root, "agents.sql")) + " WHERE a.id = @id";
                        var row = await db.Database.SqlQuery<OriginRow>(sql.Replace("[Agent origin]", "Origin"),
                            new SqlParameter("@id", key)).SingleAsync();
                        var origin = CopilotAgentClassifier.ResolveStoredOrigin(id, flag);
                        Assert.AreEqual(origin == CopilotAgentOrigin.CustomerBuilt ? "Customer-built" : origin.ToString(), row.Origin);
                    }
            }
        }

        [DataTestMethod]
        [DataRow(24, "{\"CostModelVersion\":\"1.1.0.0\",\"TotalCredits\":24}", 24, "Legacy audit estimate (not billed)")]
        [DataRow(0, "{\"CostModelVersion\":\"1.1.0.0\",\"TotalCredits\":0}", null, "Historical evidence unavailable")]
        [DataRow(24, "{\"AgentCreditBasis\":\"NotAssessed\"}", null, "Not assessed")]
        [DataRow(24, "{\"AgentCreditBasis\":\"CustomEngine\"}", 24, "Audit estimate (not billed)")]
        [DataRow(0, "{\"AgentCreditBasis\":\"NoMessages\"}", 0, "No response evidence")]
        [DataRow(null, null, null, "Historical evidence unavailable")]
        public async Task LegacyTemplate_PreservesPositiveEstimates_AndExposesUnassessedGaps(
            int? total, string json, int? expected, string status)
        {
            var id = await AddHistorical(new DateTime(2006, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                "Contoso.Unknown." + Guid.NewGuid(), "{}");
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync(@"
UPDATE dbo.copilot_chats SET copilot_credit_estimate_total = @total, copilot_credit_estimate_json = @json
WHERE event_id = @id", new SqlParameter("@total", (object)total ?? DBNull.Value),
                    new SqlParameter("@json", (object)json ?? DBNull.Value), new SqlParameter("@id", id));
                var sql = File.ReadAllText(Path.Combine(ReportDirectory(), "chats.sql"))
                    .Replace("[Audit credit estimate]", "Estimate").Replace("[Estimate status]", "Status");
                var row = await db.Database.SqlQuery<EstimateRow>(sql + " AND c.event_id = @id",
                    new SqlParameter("@id", id)).SingleAsync();
                Assert.AreEqual(expected, row.Estimate);
                Assert.AreEqual(status, row.Status);
            }
        }

        internal static string ReportDirectory()
        {
            for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var path = Path.Combine(directory.FullName, "reports", "Copilot");
                if (Directory.Exists(path)) return path;
            }
            throw new DirectoryNotFoundException("Repository Copilot report sources not found.");
        }

        private static async Task<RepairResult> Repair(AnalyticsEntitiesContext db)
        {
            var resource = new ProjectResourceReader(typeof(CopilotAuditEventManager).Assembly).ReadResourceString(
                "WebJob.Office365ActivityImporter.Engine.ActivityAPI.Copilot.SQL.repair_historical_copilot_evidence.sql");
            return await db.Database.SqlQuery<RepairResult>(resource).SingleAsync();
        }

        public class RepairResult
        {
            public int ClassifiedAgents { get; set; }
            public int RestoredTurns { get; set; }
        }
        public class OriginRow { public string Origin { get; set; } }
        public class EstimateRow
        {
            public int? Estimate { get; set; }
            public string Status { get; set; }
        }
    }
}
