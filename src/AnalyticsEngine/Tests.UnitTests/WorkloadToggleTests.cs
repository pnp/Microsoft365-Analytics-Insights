using Common.Entities;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using ActivityImporter.Engine.ActivityAPI.Copilot;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the optional-workload toggles:
    ///   - ImportPowerPlatform (ImportJobSettings): when off, Power Platform events are dropped at dispatch.
    ///   - ResolveCopilotResourceMetadata (AppConfig): when off, Copilot events are still imported with their
    ///     agent metadata but no file/meeting Graph resolution happens (no network calls on the save path).
    /// </summary>
    [TestClass]
    public class WorkloadToggleTests
    {
        private static ILogger Logger => new LoggerFactory().CreateLogger("WorkloadToggleTests");


        [TestMethod]
        public async Task ActivityReportWebLoader_PowerPlatformOnlySettings_ReadsGeneralFeedAndDropsCopilot()
        {
            var settings = new ImportTaskSettings { ImportPowerPlatform = true };
            Assert.IsTrue(settings.UsesActivityApi, "Power Platform alone must run the Activity API import loop.");
            Assert.AreEqual(ImportTaskSettings.CONTENT_TYPE_AUDIT_GENERAL, settings.ToActivityApiContentTypesString(),
                "Power Platform alone must subscribe to the Audit.General feed it is delivered on.");

            var json = @"[
                { ""Workload"": ""PowerApps"", ""Operation"": ""CreateApp"", ""ObjectId"": ""00000000-0000-0000-0000-000000000010"" },
                { ""Workload"": ""Copilot"", ""Operation"": ""CopilotInteraction"", ""ObjectId"": ""00000000-0000-0000-0000-000000000011"" }
            ]";

            using (var httpClient = new AutoThrottleHttpClient(new StaticJsonHandler(json), Logger))
            {
                var loader = new ActivityReportWebLoader(
                    httpClient,
                    Logger,
                    Guid.Empty.ToString(),
                    importPowerPlatform: settings.ImportPowerPlatform,
                    importCopilot: settings.Copilot);

                var logs = await loader.Load(new ActivityReportInfo
                {
                    ContentUri = new Uri("https://contoso.example/activity/content/00000000-0000-0000-0000-000000000012"),
                    ContentId = "00000000-0000-0000-0000-000000000013",
                    ContentType = ImportTaskSettings.CONTENT_TYPE_AUDIT_GENERAL,
                });

                Assert.IsTrue(logs.DownloadComplete);
                Assert.AreEqual(1, logs.Count, "Only the Power Platform event should survive with Copilot disabled.");
                Assert.IsInstanceOfType(logs[0], typeof(PowerAppsAuditLogContent));
            }
        }

        [TestMethod]
        public void Dispatch_CopilotDisabled_DropsCopilotWorkload()
        {
            var json = JToken.Parse(@"{
                ""Workload"": ""Copilot"",
                ""Operation"": ""CopilotInteraction"",
                ""ObjectId"": ""00000000-0000-0000-0000-000000000014""
            }");
            var logBase = json.ToObject<WorkloadOnlyAuditLogContent>();

            Assert.IsNull(
                AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importPowerPlatform: true, importCopilot: false),
                "Copilot audit events must be dropped when the Copilot import toggle is disabled, even if Audit.General is being read for Power Platform.");
        }

        [TestMethod]
        public void UsesActivityApi_IncludesEveryAuditFeedConsumer()
        {
            Assert.IsTrue(new ImportTaskSettings { ActivityLog = true }.UsesActivityApi, "SharePoint audit uses Audit.SharePoint.");
            Assert.IsTrue(new ImportTaskSettings { Copilot = true }.UsesActivityApi, "Copilot uses Audit.General.");
            Assert.IsTrue(new ImportTaskSettings { ImportPowerPlatform = true }.UsesActivityApi, "Power Platform uses Audit.General.");
            Assert.IsFalse(new ImportTaskSettings().UsesActivityApi, "No audit-feed toggle enabled means the Activity API loop should be skipped.");
        }

        [TestMethod]
        public void Dispatch_PowerPlatformDisabled_DropsPowerPlatformWorkloads()
        {
            var json = JToken.Parse("{}");

            foreach (var workload in new[]
            {
                ActivityImportConstants.WORKLOAD_POWER_PLATFORM,
                ActivityImportConstants.WORKLOAD_POWER_APPS,
                ActivityImportConstants.WORKLOAD_POWER_AUTOMATE,
                ActivityImportConstants.WORKLOAD_POWER_BI,
                ActivityImportConstants.WORKLOAD_COPILOT_STUDIO
            })
            {
                var logBase = new WorkloadOnlyAuditLogContent { Workload = workload };
                Assert.IsNull(
                    AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importPowerPlatform: false),
                    $"'{workload}' must be dropped when Power Platform import is disabled.");
            }
        }

        [TestMethod]
        public void Dispatch_PowerPlatformEnabled_KeepsPowerAppsEvent()
        {
            var json = JToken.Parse("{}");
            var logBase = new WorkloadOnlyAuditLogContent { Workload = ActivityImportConstants.WORKLOAD_POWER_APPS };

            Assert.IsNotNull(
                AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importPowerPlatform: true),
                "Power Platform events must be imported when the workload is enabled.");
        }

        [TestMethod]
        public void Dispatch_PowerAutomateLifecycleEvent_MapsDocumentedFields()
        {
            const string environmentId = "00000000-0000-0000-0000-000000000001";
            const string flowId = "00000000-0000-0000-0000-000000000002";
            var json = JToken.Parse($@"{{
                ""Workload"": ""MicrosoftFlow"",
                ""Operation"": ""EditFlow"",
                ""FlowDetailsUrl"": ""https://admin.powerplatform.microsoft.com/environments/{environmentId}/flows/{flowId}/flowDetails"",
                ""FlowConnectorNames"": ""Request, OpenApiConnection""
            }}");
            var logBase = json.ToObject<WorkloadOnlyAuditLogContent>();

            var mapped = AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importPowerPlatform: true)
                as PowerAutomateAuditLogContent;

            Assert.IsNotNull(mapped);
            Assert.AreEqual(flowId, mapped.FlowId);
            Assert.AreEqual(environmentId, mapped.EnvironmentName);
            CollectionAssert.AreEquivalent(
                new[] { "Request", "OpenApiConnection" },
                mapped.ConnectionReferences.Select(r => r.ConnectorName).ToArray());
        }

        [TestMethod]
        public void Dispatch_PowerAutomatePermissionEvent_MapsRecipientAndRole()
        {
            const string flowId = "00000000-0000-0000-0000-000000000003";
            var json = JToken.Parse($@"{{
                ""Workload"": ""MicrosoftFlow"",
                ""Operation"": ""PutPermissions"",
                ""FlowDetailsUrl"": ""https://admin.powerplatform.microsoft.com/environments/00000000-0000-0000-0000-000000000004/flows/{flowId}/flowDetails"",
                ""RecipientUPN"": ""recipient@contoso.example"",
                ""SharingPermission"": ""3""
            }}");
            var logBase = json.ToObject<WorkloadOnlyAuditLogContent>();

            var mapped = AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importPowerPlatform: true)
                as PowerAutomateAuditLogContent;

            Assert.IsNotNull(mapped);
            Assert.AreEqual(flowId, mapped.FlowId);
            Assert.AreEqual(1, mapped.Permissions.Count);
            Assert.AreEqual("recipient@contoso.example", mapped.Permissions[0].PrincipalName);
            Assert.AreEqual("Owner", mapped.Permissions[0].RoleName);
        }

        [TestMethod]
        public void Dispatch_PowerAutomateEventWithoutFlowIdentity_IsDropped()
        {
            var json = JToken.Parse(@"{
                ""Workload"": ""MicrosoftFlow"",
                ""Operation"": ""StartAPaidTrial""
            }");
            var logBase = json.ToObject<WorkloadOnlyAuditLogContent>();

            Assert.IsNull(
                AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importPowerPlatform: true),
                "Non-flow Power Automate audit events must not create NULL flow metadata rows.");
        }

        [TestMethod]
        public void Dispatch_CopilotStudioAuthoringEvent_RoutesByBotIdShape()
        {
            const string botId = "00000000-0000-0000-0000-000000000005";
            var json = JToken.Parse($@"{{
                ""Workload"": ""PowerPlatform"",
                ""RecordType"": 256,
                ""Operation"": ""BotCreate"",
                ""BotId"": ""{botId}"",
                ""BotSchemaName"": ""contoso_support_agent"",
                ""EnvironmentId"": ""00000000-0000-0000-0000-000000000006""
            }}");
            var logBase = json.ToObject<WorkloadOnlyAuditLogContent>();

            var mapped = AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importPowerPlatform: true)
                as CopilotStudioAuditLogContent;

            Assert.IsNotNull(mapped);
            Assert.AreEqual(botId, mapped.BotId);
            Assert.AreEqual("contoso_support_agent", mapped.BotSchemaName);
            Assert.AreEqual("00000000-0000-0000-0000-000000000006", mapped.EnvironmentId);
        }

        [TestMethod]
        public void Dispatch_PowerPlatformDisabled_DoesNotAffectSharePoint()
        {
            var json = JToken.Parse("{}");
            var logBase = new WorkloadOnlyAuditLogContent { Workload = ActivityImportConstants.WORKLOAD_SP };

            Assert.IsNotNull(
                AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importPowerPlatform: false),
                "SharePoint events must never be affected by the Power Platform toggle.");
        }

        private sealed class StaticJsonHandler : HttpMessageHandler
        {
            private readonly string _json;

            public StaticJsonHandler(string json)
            {
                _json = json;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_json, Encoding.UTF8, "application/json")
                });
            }
        }

        [TestMethod]
        public async Task CopilotResolutionDisabled_MakesNoGraphCalls()
        {
            var loader = new RecordingCopilotMetadataLoader();
            // A dummy (non-empty) connection string is fine: staging only builds in-memory rows; no DB is
            // touched until CommitAllChanges (which this test deliberately does not call).
            var manager = new CopilotAuditEventManager(
                "Data Source=(localdb)\\unused;Initial Catalog=unused;Integrated Security=true",
                loader, Logger, resolveResourceMetadata: false);

            var baseEvent = new CommonAuditEvent
            {
                Id = Guid.NewGuid(),
                TimeStamp = DateTime.UtcNow,
                Operation = new EventOperation { Name = "op" },
                User = new User { UserPrincipalName = "user@contoso.onmicrosoft.com" }
            };

            // Meeting-context event: would normally call GetUserIdFromUpn + GetMeetingInfo.
            await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                AgentId = "agent-1",
                AgentName = "Test Agent",
                IsCustomAgent = true,
                CopilotEventData = new CopilotEventData
                {
                    AppHost = "Teams",
                    Contexts = new List<Context>
                    {
                        new Context
                        {
                            Id = "https://microsoft.teams.com/threads/19:meeting_abc@thread.v2",
                            Type = ActivityImportConstants.COPILOT_CONTEXT_TYPE_TEAMS_MEETING
                        }
                    }
                }
            }, baseEvent);

            // File-context event: would normally call GetSpoFileInfo.
            await manager.SaveSingleCopilotEventToSqlStaging(new CopilotAuditLogContent
            {
                AgentId = "agent-1",
                AgentName = "Test Agent",
                IsCustomAgent = true,
                CopilotEventData = new CopilotEventData
                {
                    AppHost = "Word",
                    Contexts = new List<Context>
                    {
                        new Context
                        {
                            Id = "https://contoso.sharepoint.com/sites/x/Shared Documents/doc.docx",
                            Type = "SharePoint"
                        }
                    }
                }
            }, baseEvent);

            Assert.AreEqual(0, loader.MeetingCalls, "No meeting Graph call when Copilot resource resolution is disabled.");
            Assert.AreEqual(0, loader.FileCalls, "No file Graph call when Copilot resource resolution is disabled.");
            Assert.AreEqual(0, loader.UserIdCalls, "No user-id Graph call when Copilot resource resolution is disabled.");
        }
    }
}
