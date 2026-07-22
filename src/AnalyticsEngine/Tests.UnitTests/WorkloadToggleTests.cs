using Common.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine;
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
        public void Dispatch_PowerPlatformDisabled_DoesNotAffectSharePoint()
        {
            var json = JToken.Parse("{}");
            var logBase = new WorkloadOnlyAuditLogContent { Workload = ActivityImportConstants.WORKLOAD_SP };

            Assert.IsNotNull(
                AuditLogContentDispatcher.Dispatch(json, logBase, Logger, importPowerPlatform: false),
                "SharePoint events must never be affected by the Power Platform toggle.");
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
