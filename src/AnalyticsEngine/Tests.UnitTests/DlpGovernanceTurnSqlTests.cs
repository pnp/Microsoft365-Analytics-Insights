extern alias AnalyticsWeb;

using Common.Entities;
using Common.Entities.Copilot;
using Common.Entities.Migrations;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;
using DlpAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.DlpAPIController;
using DlpGovernanceSummary = AnalyticsWeb::Web.AnalyticsWeb.Models.Dlp.DlpGovernanceSummary;

namespace Tests.UnitTests
{
    /// <summary>Runs the real governance queries on synthetic turns, with production column types.</summary>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class DlpGovernanceTurnSqlTests
    {
        [TestMethod]
        public void GovernanceQueries_UseTheSharedTimeStampAndEventIdTurnPredicate()
        {
            foreach (var sql in new[]
            {
                DlpAPIController.GovernanceMessagesSql, DlpAPIController.GovernanceResourcesSql,
                DlpAPIController.GovernanceModelsSql, DlpAPIController.GovernancePluginsSql
            })
            {
                StringAssert.Contains(sql, CopilotTurnSql.CountedTurn("c"),
                    "Self-contained benchmarkable SQL must stay in step with the common canonical-turn model.");
            }
        }

        [TestMethod]
        public async Task Pair_KeepsRuntimeOnlySafetyAndClientOnlyModel_AndCountsTheSharedResourceOnce()
        {
            using (var db = NewDatabase())
            {
                var runtime = Chat(db, 1, 1, DateTime.UtcNow.Date.AddDays(-2));
                var client = Chat(db, 2, 1, DateTime.UtcNow.Date.AddDays(-2).AddSeconds(5));
                Duplicate(db, runtime, client, 1);
                Message(db, runtime, true);
                Message(db, runtime, null);
                Message(db, client, null);
                Resource(db, runtime, true, label: 1);
                Resource(db, client, null, label: 1);
                db.Execute($@"INSERT dbo.copilot_event_ai_models (copilot_chat_id, model_id) VALUES ('{client}', 1);
INSERT dbo.copilot_event_ai_system_plugins (copilot_chat_id, ai_system_plugin_id) VALUES ('{runtime}', 1), ('{client}', 1);");

                var result = await Governance(db);

                Assert.AreEqual(1L, result.Interactions, "The runtime/client pair is one turn, not two audit records.");
                Assert.AreEqual(1L, result.Jailbreak.ReportedInteractions);
                Assert.AreEqual(1L, result.Jailbreak.FlaggedInteractions, "Runtime-only evidence must not be discarded.");
                Assert.AreEqual(10000d, result.Jailbreak.RatePer10000.Value);
                Assert.AreEqual(1L, result.Xpia.ReportedInteractions);
                Assert.AreEqual(1L, result.Xpia.FlaggedInteractions, "The retained resource's NULL must not hide its twin's true flag.");
                Assert.AreEqual(1L, result.SensitivityLabels.Resources);
                Assert.AreEqual(1L, result.SensitivityLabels.LabelledResources);
                Assert.AreEqual(1L, result.SensitivityLabels.InteractionsWithResources);
                Assert.AreEqual(1d, result.SensitivityLabels.Share.Value);
                Assert.AreEqual(1L, result.InteractionsWithModel);
                Assert.AreEqual(1L, result.Models.Single().Interactions);
                Assert.AreEqual(1d, result.Models.Single().Share.Value, "A client-only model served the whole turn, not half of it.");
                Assert.AreEqual(1L, result.InteractionsWithPlugin);
                Assert.AreEqual(1L, result.Plugins.Single().Interactions);
                Assert.AreEqual(1d, result.Plugins.Single().Share.Value);
            }
        }

        [TestMethod]
        public async Task EchoBurst_KeepsAllSignalEvidence_AndEveryDistinctNullableResourceTuple()
        {
            using (var db = NewDatabase())
            {
                var turn = Chat(db, 1, 1, DateTime.UtcNow.Date.AddDays(-2));
                var echo = Chat(db, 2, 1, DateTime.UtcNow.Date.AddDays(-2).AddSeconds(3));
                var lastEcho = Chat(db, 3, 1, DateTime.UtcNow.Date.AddDays(-2).AddSeconds(6));
                Duplicate(db, echo, turn, 2);
                Duplicate(db, lastEcho, turn, 2);
                Message(db, turn, false);
                Message(db, echo, null);
                Message(db, lastEcho, true);
                Resource(db, turn, false);
                Resource(db, echo, null);
                Resource(db, lastEcho, true);

                // The same seven-column identity used by the importer and CopilotTurnSql.ResourceCountedOnce.
                var tupleColumns = new[]
                {
                    "resource_id_id", "resource_name_id", "resource_site_url_id", "resource_type_id",
                    "sensitivity_label_id", "action_id", "list_item_unique_id_id"
                };
                foreach (var column in tupleColumns)
                {
                    db.Execute($@"INSERT dbo.copilot_event_accessed_resources (copilot_chat_id, {column}, xpia_detected)
VALUES ('{lastEcho}', 1, NULL);");
                }
                db.Execute($@"INSERT dbo.copilot_event_ai_models (copilot_chat_id, model_id)
VALUES ('{turn}', 1), ('{echo}', 2), ('{lastEcho}', 3);
INSERT dbo.copilot_event_ai_system_plugins (copilot_chat_id, ai_system_plugin_id)
VALUES ('{turn}', 1), ('{echo}', 1), ('{lastEcho}', 2);");

                var result = await Governance(db);

                Assert.AreEqual(1L, result.Interactions);
                Assert.AreEqual(1L, result.Jailbreak.ReportedInteractions);
                Assert.AreEqual(1L, result.Jailbreak.FlaggedInteractions);
                Assert.AreEqual(1L, result.Xpia.ReportedInteractions);
                Assert.AreEqual(1L, result.Xpia.FlaggedInteractions);
                Assert.AreEqual(8L, result.SensitivityLabels.Resources, "One shared NULL tuple, plus seven genuinely different accesses.");
                Assert.AreEqual(1L, result.SensitivityLabels.LabelledResources);
                Assert.AreEqual(1L, result.SensitivityLabels.InteractionsWithResources);
                Assert.AreEqual(0.125d, result.SensitivityLabels.Share.Value);
                Assert.AreEqual(1L, result.InteractionsWithModel);
                Assert.AreEqual(2, result.Models.Count, "Different versions of the same model name are still one model per turn.");
                Assert.IsTrue(result.Models.All(m => m.Interactions == 1 && m.Share == 1d));
                Assert.IsTrue(result.Models.Any(m => m.Name == "Καλημέρα κόσμε"));
                Assert.AreEqual(1L, result.InteractionsWithPlugin);
                Assert.AreEqual(2, result.Plugins.Count);
                Assert.IsTrue(result.Plugins.All(p => p.Interactions == 1 && p.Share == 1d));
                Assert.IsTrue(result.Plugins.Any(p => p.Name == "Καλημέρα κόσμε"));
            }
        }

        [TestMethod]
        public async Task ScopedSingletonsAndPairs_DistinguishUnknownFromKnownClean_AndExcludeOtherPeople()
        {
            using (var db = NewDatabase())
            {
                var unknown = Chat(db, 1, 1, DateTime.UtcNow.Date.AddDays(-2));
                var unknownTwin = Chat(db, 2, 1, DateTime.UtcNow.Date.AddDays(-2).AddSeconds(5));
                Duplicate(db, unknown, unknownTwin, 1);
                Message(db, unknown, null);
                Message(db, unknownTwin, null);
                Resource(db, unknown, null);
                Resource(db, unknownTwin, null);

                var clean = Chat(db, 3, 2, DateTime.UtcNow.Date.AddDays(-2));
                Message(db, clean, false);
                Resource(db, clean, false);
                Chat(db, 4, 3, DateTime.UtcNow.Date.AddDays(-2));
                var otherPerson = Chat(db, 5, 4, DateTime.UtcNow.Date.AddDays(-2));
                Message(db, otherPerson, true);
                Resource(db, otherPerson, true, label: 1);
                db.Execute($@"INSERT dbo.copilot_event_ai_models (copilot_chat_id, model_id) VALUES ('{otherPerson}', 1);
INSERT dbo.copilot_event_ai_system_plugins (copilot_chat_id, ai_system_plugin_id) VALUES ('{otherPerson}', 1);");

                var onlyUnknown = await Governance(db, ReportUserScope.ForUsers(new[] { 1 }));
                Assert.AreEqual(1L, onlyUnknown.Interactions);
                Assert.AreEqual(0L, onlyUnknown.Jailbreak.ReportedInteractions);
                Assert.IsNull(onlyUnknown.Jailbreak.RatePer10000);
                Assert.AreEqual(0L, onlyUnknown.Xpia.ReportedInteractions);
                Assert.IsNull(onlyUnknown.Xpia.RatePer10000);
                Assert.AreEqual(1L, onlyUnknown.SensitivityLabels.Resources);
                Assert.AreEqual(0d, onlyUnknown.SensitivityLabels.Share.Value);

                var onlyClean = await Governance(db, ReportUserScope.ForUsers(new[] { 2 }));
                Assert.AreEqual(1L, onlyClean.Interactions);
                Assert.AreEqual(1L, onlyClean.Jailbreak.ReportedInteractions);
                Assert.AreEqual(0d, onlyClean.Jailbreak.RatePer10000.Value);
                Assert.AreEqual(1L, onlyClean.Xpia.ReportedInteractions);
                Assert.AreEqual(0d, onlyClean.Xpia.RatePer10000.Value);

                var scoped = await Governance(db, ReportUserScope.ForUsers(new[] { 1, 2, 3 }));
                Assert.AreEqual(3L, scoped.Interactions);
                Assert.AreEqual(1L, scoped.Jailbreak.ReportedInteractions);
                Assert.AreEqual(0L, scoped.Jailbreak.FlaggedInteractions);
                Assert.AreEqual(1L, scoped.Xpia.ReportedInteractions);
                Assert.AreEqual(0L, scoped.Xpia.FlaggedInteractions);
                Assert.AreEqual(2L, scoped.SensitivityLabels.Resources);
                Assert.AreEqual(0L, scoped.InteractionsWithModel + scoped.InteractionsWithPlugin);
                Assert.AreEqual(0, scoped.Models.Count + scoped.Plugins.Count);

                var unfiltered = await Governance(db);
                var everyone = await Governance(db, ReportUserScope.ForUsers(new[] { 1, 2, 3, 4 }));
                Assert.AreEqual(4L, unfiltered.Interactions);
                Assert.AreEqual(unfiltered.Interactions, everyone.Interactions);
                Assert.AreEqual(2L, everyone.Jailbreak.ReportedInteractions);
                Assert.AreEqual(1L, everyone.Jailbreak.FlaggedInteractions);
                Assert.AreEqual(2L, everyone.Xpia.ReportedInteractions);
                Assert.AreEqual(1L, everyone.Xpia.FlaggedInteractions);
                Assert.AreEqual(0.25d, everyone.Models.Single().Share.Value);
                Assert.AreEqual(0.25d, everyone.Plugins.Single().Share.Value);

                var nobody = await Governance(db, ReportUserScope.ForUsers(new int[0]));
                Assert.AreEqual(0L, nobody.Interactions);
                Assert.IsNull(nobody.Jailbreak.RatePer10000);
                Assert.IsNull(nobody.Xpia.RatePer10000);
                Assert.IsNull(nobody.SensitivityLabels.Share);
                Assert.AreEqual(0L, nobody.InteractionsWithModel + nobody.InteractionsWithPlugin);
            }
        }

        [TestMethod]
        public async Task Window_IsAnchoredOnTheCountedRecord_AndRetainsItsConstituentsOutsideTheBoundary()
        {
            using (var db = NewDatabase())
            {
                var from = DateTime.UtcNow.Date.AddDays(-28);
                var runtime = Chat(db, 1, 1, from.AddSeconds(-2));
                var client = Chat(db, 2, 1, from.AddSeconds(1));
                Duplicate(db, runtime, client, 1);
                Message(db, runtime, true);
                Resource(db, runtime, true, label: 1);
                db.Execute($@"INSERT dbo.copilot_event_ai_models (copilot_chat_id, model_id) VALUES ('{runtime}', 1);
INSERT dbo.copilot_event_ai_system_plugins (copilot_chat_id, ai_system_plugin_id) VALUES ('{runtime}', 1);");

                var outsideTurn = Chat(db, 3, 1, from.AddSeconds(-1));
                var insideEcho = Chat(db, 4, 1, from.AddSeconds(2));
                Duplicate(db, insideEcho, outsideTurn, 2);
                Resource(db, insideEcho, false);
                db.Execute($@"INSERT dbo.copilot_event_ai_models (copilot_chat_id, model_id) VALUES ('{insideEcho}', 3);");

                var result = await Governance(db, ReportUserScope.ForUsers(new[] { 1 }));

                Assert.AreEqual(1L, result.Interactions, "An echo in the window does not move its earlier turn into it.");
                Assert.AreEqual(1L, result.Jailbreak.FlaggedInteractions, "All evidence of the selected turn belongs to it.");
                Assert.AreEqual(1L, result.Xpia.FlaggedInteractions);
                Assert.AreEqual(1L, result.Xpia.ReportedInteractions);
                Assert.AreEqual(1L, result.SensitivityLabels.Resources);
                Assert.AreEqual(1L, result.InteractionsWithModel);
                Assert.AreEqual("Contoso Model", result.Models.Single().Name);
                Assert.AreEqual(1d, result.Models.Single().Share.Value);
                Assert.AreEqual(1L, result.InteractionsWithPlugin);
            }
        }

        private sealed class ScratchContextFactory : IAnalyticsDbContextFactory
        {
            private readonly string _connectionString;
            public ScratchContextFactory(string connectionString) => _connectionString = connectionString;
            public AnalyticsEntitiesContext Create() => new AnalyticsEntitiesContext(_connectionString, true, false);
        }

        private static async Task<DlpGovernanceSummary> Governance(ScratchDatabase db, ReportUserScope scope = null)
        {
            using (var controller = new DlpAPIController(new ScratchContextFactory(db.ConnectionString)))
            {
                return await controller.BuildGovernanceAsync(28, scope);
            }
        }

        private static ScratchDatabase NewDatabase()
        {
            var db = ScratchDatabase.Create("DlpGovernanceTurns");
            db.Execute(@"
CREATE TABLE dbo.copilot_chats (event_id uniqueidentifier NOT NULL PRIMARY KEY, user_id int NULL, time_stamp datetime NULL);
CREATE INDEX IX_copilot_chats_time_stamp_user_id ON dbo.copilot_chats (time_stamp, user_id);
CREATE TABLE dbo.copilot_event_messages (
    id int IDENTITY NOT NULL PRIMARY KEY, copilot_chat_id uniqueidentifier NOT NULL,
    message_id nvarchar(500) NULL, size bigint NULL, is_prompt bit NULL, jailbreak_detected bit NULL);
CREATE INDEX IX_copilot_event_messages_chat ON dbo.copilot_event_messages (copilot_chat_id);
CREATE TABLE dbo.copilot_event_accessed_resources (
    id int IDENTITY NOT NULL PRIMARY KEY, copilot_chat_id uniqueidentifier NOT NULL,
    resource_id_id int NULL, resource_name_id int NULL, resource_site_url_id int NULL,
    resource_type_id int NULL, sensitivity_label_id int NULL, action_id int NULL,
    list_item_unique_id_id int NULL, xpia_detected bit NULL);
CREATE INDEX IX_copilot_event_accessed_resources_dedup ON dbo.copilot_event_accessed_resources
    (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id,
     resource_type_id, sensitivity_label_id, action_id, list_item_unique_id_id);
CREATE TABLE dbo.copilot_ai_models (
    id int NOT NULL PRIMARY KEY, name nvarchar(100) NULL, provider_name nvarchar(100) NULL, version nvarchar(100) NULL);
CREATE TABLE dbo.copilot_event_ai_models (
    id int IDENTITY NOT NULL PRIMARY KEY, copilot_chat_id uniqueidentifier NOT NULL, model_id int NOT NULL);
CREATE TABLE dbo.copilot_ai_system_plugins (
    id int NOT NULL PRIMARY KEY, plugin_id nvarchar(255) NULL, name nvarchar(255) NULL, version nvarchar(50) NULL);
CREATE TABLE dbo.copilot_event_ai_system_plugins (
    id int IDENTITY NOT NULL PRIMARY KEY, copilot_chat_id uniqueidentifier NOT NULL, ai_system_plugin_id int NOT NULL);
INSERT dbo.copilot_ai_models (id, name, provider_name, version)
VALUES (1, N'Contoso Model', N'Contoso AI', N'1'), (2, N'Contoso Model', N'Contoso AI', N'2'), (3, N'Καλημέρα κόσμε', NULL, NULL);
INSERT dbo.copilot_ai_system_plugins (id, plugin_id, name)
VALUES (1, N'Contoso.Search', N'Contoso search'), (2, NULL, N'Καλημέρα κόσμε');");
            db.Execute(CopilotTurnPairing.Up_Sql);
            return db;
        }

        private static Guid Chat(ScratchDatabase db, int id, int userId, DateTime when)
        {
            var eventId = new Guid($"00000000-0000-0000-0000-{id:000000000000}");
            db.Execute($@"INSERT dbo.copilot_chats (event_id, user_id, time_stamp)
VALUES ('{eventId}', {userId}, '{when:yyyy-MM-ddTHH:mm:ss}');");
            return eventId;
        }

        private static void Duplicate(ScratchDatabase db, Guid extra, Guid counted, int reason)
            => db.Execute($@"INSERT dbo.copilot_chat_duplicates (event_id, time_stamp, counted_event_id, reason)
SELECT event_id, time_stamp, '{counted}', {reason} FROM dbo.copilot_chats WHERE event_id = '{extra}';");

        private static void Message(ScratchDatabase db, Guid chat, bool? flag)
            => db.Execute($@"INSERT dbo.copilot_event_messages (copilot_chat_id, jailbreak_detected)
VALUES ('{chat}', {Flag(flag)});");

        private static void Resource(ScratchDatabase db, Guid chat, bool? flag, int? label = null)
            => db.Execute($@"INSERT dbo.copilot_event_accessed_resources (copilot_chat_id, xpia_detected, sensitivity_label_id)
VALUES ('{chat}', {Flag(flag)}, {(label.HasValue ? label.Value.ToString() : "NULL")});");

        private static string Flag(bool? flag) => flag.HasValue ? (flag.Value ? "1" : "0") : "NULL";
    }
}
