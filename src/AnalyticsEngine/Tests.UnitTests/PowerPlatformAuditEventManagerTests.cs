using Common.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// DB-backed tests for <see cref="PowerPlatformAuditEventManager"/>: covers the per-workload
    /// staging + merge pipeline for Power Apps, Power Automate, Power BI, Copilot Studio and Dataverse.
    /// Follows the same shape as <c>CopilotEventManagerSaveTests</c> (real <see cref="AnalyticsEntitiesContext"/>,
    /// unique IDs per test so test runs don't interfere).
    /// </summary>
    [TestClass]
    public class PowerPlatformAuditEventManagerTests
    {
        private readonly ILogger _logger = new LoggerFactory().CreateLogger("PowerPlatformTests");
        private readonly TestsAppConfig _config = new TestsAppConfig();

        private PowerPlatformAuditEventManager NewManager() =>
            new PowerPlatformAuditEventManager(_config.ConnectionStrings.DatabaseConnectionString, _logger);

        private CommonAuditEvent BuildCommonEvent(string operationName, string upnSuffix)
        {
            var ticks = DateTime.Now.Ticks;
            return new CommonAuditEvent
            {
                Id = Guid.NewGuid(),
                TimeStamp = DateTime.UtcNow,
                Operation = new EventOperation { Name = $"{operationName} {ticks}" },
                User = new User { AzureAdId = "test-" + ticks, UserPrincipalName = $"test-{upnSuffix}-{ticks}@unit.test" }
            };
        }

        private async Task PersistAuditEventAsync(AnalyticsEntitiesContext db, params CommonAuditEvent[] events)
        {
            db.AuditEventsCommon.AddRange(events);
            await db.SaveChangesAsync();
        }

        // -- Power Apps -----------------------------------------------------------------------

        /// <summary>
        /// A single LaunchPowerApp event should persist the app lookup + per-event metadata
        /// and populate first_seen_at and client_type.
        /// </summary>
        [TestMethod]
        public async Task PowerApp_SimpleLaunchEvent_PersistsAppAndMetadata()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("LaunchPowerApp", "app-launch");
                await PersistAuditEventAsync(db, commonEvent);

                var appId = "app-" + Guid.NewGuid().ToString("N");

                var manager = NewManager();
                await manager.SaveSinglePowerAppEventToSqlStaging(new PowerAppsAuditLogContent
                {
                    AppName = appId,
                    AppDisplayName = "My Stress Test App",
                    EnvironmentName = "env-test",
                    AppSessionId = Guid.NewGuid().ToString("N"),
                    AppType = "Canvas",
                    ClientType = "Web",
                    UserAgent = "Mozilla/5.0 unit-test"
                }, commonEvent);

                await manager.CommitAllChanges();

                var appRow = await db.power_apps.SingleOrDefaultAsync(a => a.AppId == appId);
                Assert.IsNotNull(appRow, "power_apps row must be created for a brand-new app_id.");
                Assert.AreEqual("My Stress Test App", appRow.Name);
                Assert.IsTrue(appRow.FirstSeenAt.HasValue, "first_seen_at should be populated on first sighting.");

                var meta = await db.power_app_events.SingleOrDefaultAsync(m => m.EventID == commonEvent.Id);
                Assert.IsNotNull(meta, "event_meta_power_app must exist for the staged event.");
                Assert.AreEqual(appRow.ID, meta.PowerAppId);
                Assert.IsNotNull(meta.ClientTypeId, "client_type lookup should be linked.");
            }
        }

        /// <summary>
        /// A publish event with two connectors must produce one row per (app, connector) in the junction table
        /// and one row per connector in the lookup table.
        /// </summary>
        [TestMethod]
        public async Task PowerApp_WithConnectors_PopulatesConnectorJunction()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("PublishPowerApp", "app-publish");
                await PersistAuditEventAsync(db, commonEvent);

                var appId = "app-" + Guid.NewGuid().ToString("N");
                var connectorA = "connector-a-" + Guid.NewGuid().ToString("N");
                var connectorB = "connector-b-" + Guid.NewGuid().ToString("N");

                var manager = NewManager();
                await manager.SaveSinglePowerAppEventToSqlStaging(new PowerAppsAuditLogContent
                {
                    AppName = appId,
                    AppDisplayName = "App With Connectors",
                    AppType = "Canvas",
                    ClientType = "Web",
                    ConnectionReferences = new List<PowerPlatformConnectionRef>
                    {
                        new PowerPlatformConnectionRef { ConnectorName = connectorA },
                        new PowerPlatformConnectionRef { ConnectorName = connectorB },
                        new PowerPlatformConnectionRef { ConnectorName = connectorA } // duplicate - should collapse
                    }
                }, commonEvent);

                await manager.CommitAllChanges();

                var appRow = await db.power_apps.SingleAsync(a => a.AppId == appId);
                var connectorALookup = await db.power_platform_connectors.SingleOrDefaultAsync(c => c.Name == connectorA);
                var connectorBLookup = await db.power_platform_connectors.SingleOrDefaultAsync(c => c.Name == connectorB);
                Assert.IsNotNull(connectorALookup, "Connector A should be inserted in the lookup.");
                Assert.IsNotNull(connectorBLookup, "Connector B should be inserted in the lookup.");

                var bindings = await db.power_app_connectors
                    .Where(j => j.PowerAppId == appRow.ID)
                    .ToListAsync();
                Assert.AreEqual(2, bindings.Count, "Duplicate connector names within one event should be deduped in the junction.");
                CollectionAssert.AreEquivalent(
                    new[] { connectorALookup.ID, connectorBLookup.ID },
                    bindings.Select(b => b.ConnectorId).ToArray());
            }
        }

        /// <summary>
        /// A share/permission-grant event with two recipients must emit two share rows and
        /// auto-create the recipient users if they don't already exist.
        /// </summary>
        [TestMethod]
        public async Task PowerApp_WithSharePermissions_EmitsOneRowPerRecipient()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("ShareApp", "app-share");
                await PersistAuditEventAsync(db, commonEvent);

                var appId = "app-" + Guid.NewGuid().ToString("N");
                var recipient1 = $"recipient1-{Guid.NewGuid():N}@unit.test";
                var recipient2 = $"recipient2-{Guid.NewGuid():N}@unit.test";

                var manager = NewManager();
                await manager.SaveSinglePowerAppEventToSqlStaging(new PowerAppsAuditLogContent
                {
                    AppName = appId,
                    AppDisplayName = "Shared App",
                    AppType = "Canvas",
                    Permissions = new List<PowerPlatformPermissionEntry>
                    {
                        new PowerPlatformPermissionEntry { PrincipalName = recipient1, RoleName = "CanView" },
                        new PowerPlatformPermissionEntry { PrincipalName = recipient2, RoleName = "CanEdit" }
                    }
                }, commonEvent);

                await manager.CommitAllChanges();

                var shareRows = await db.power_app_share_events
                    .Where(s => s.EventId == commonEvent.Id)
                    .ToListAsync();
                Assert.AreEqual(2, shareRows.Count, "Two recipients → two share rows.");

                var roleNames = shareRows.Select(s => s.RoleName).OrderBy(x => x).ToArray();
                CollectionAssert.AreEqual(new[] { "CanEdit", "CanView" }, roleNames);

                var recipient1Exists = await db.users.AnyAsync(u => u.UserPrincipalName == recipient1);
                var recipient2Exists = await db.users.AnyAsync(u => u.UserPrincipalName == recipient2);
                Assert.IsTrue(recipient1Exists, "Recipient 1 should have been inserted into users.");
                Assert.IsTrue(recipient2Exists, "Recipient 2 should have been inserted into users.");
            }
        }

        /// <summary>
        /// Permissions[] entries with a null/empty PrincipalName must be skipped silently
        /// (no NRE, no rogue share row, no orphan user).
        /// </summary>
        [TestMethod]
        public async Task PowerApp_WithNullPrincipalInPermissions_SkipsThatEntry()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("ShareApp", "app-share-skip");
                await PersistAuditEventAsync(db, commonEvent);

                var appId = "app-" + Guid.NewGuid().ToString("N");
                var validRecipient = $"valid-{Guid.NewGuid():N}@unit.test";

                var manager = NewManager();
                await manager.SaveSinglePowerAppEventToSqlStaging(new PowerAppsAuditLogContent
                {
                    AppName = appId,
                    Permissions = new List<PowerPlatformPermissionEntry>
                    {
                        new PowerPlatformPermissionEntry { PrincipalName = null, RoleName = "CanView" },
                        new PowerPlatformPermissionEntry { PrincipalName = "", RoleName = "CanView" },
                        new PowerPlatformPermissionEntry { PrincipalName = validRecipient, RoleName = "Owner" }
                    }
                }, commonEvent);

                await manager.CommitAllChanges();

                var shareRows = await db.power_app_share_events
                    .Where(s => s.EventId == commonEvent.Id)
                    .ToListAsync();
                Assert.AreEqual(1, shareRows.Count, "Only the entry with a populated PrincipalName should be persisted.");
                Assert.AreEqual("Owner", shareRows[0].RoleName);
            }
        }

        // -- Power Automate -------------------------------------------------------------------

        /// <summary>
        /// A FlowRunCompleted event should create the flow lookup and the per-event metadata
        /// with recurrence_type linked.
        /// </summary>
        [TestMethod]
        public async Task PowerAutomate_FlowEvent_PersistsFlowAndMetadata()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("FlowRunCompleted", "flow-run");
                await PersistAuditEventAsync(db, commonEvent);

                var flowId = "flow-" + Guid.NewGuid().ToString("N");

                var manager = NewManager();
                await manager.SaveSinglePowerAutomateEventToSqlStaging(new PowerAutomateAuditLogContent
                {
                    FlowId = flowId,
                    FlowDisplayName = "Nightly Sync",
                    EnvironmentName = "env-test",
                    RunId = Guid.NewGuid().ToString("N"),
                    RecurrenceType = "Recurrence"
                }, commonEvent);

                await manager.CommitAllChanges();

                var flowRow = await db.power_automate_flows.SingleOrDefaultAsync(f => f.FlowId == flowId);
                Assert.IsNotNull(flowRow, "power_automate_flows row must be created for a brand-new flow_id.");
                Assert.AreEqual("Nightly Sync", flowRow.Name);
                Assert.IsTrue(flowRow.FirstSeenAt.HasValue, "first_seen_at should be populated on first sighting.");

                var meta = await db.power_automate_flow_events.SingleOrDefaultAsync(m => m.EventID == commonEvent.Id);
                Assert.IsNotNull(meta, "event_meta_power_automate_flow must exist for the staged event.");
                Assert.AreEqual(flowRow.ID, meta.FlowId);
                Assert.IsNotNull(meta.RecurrenceTypeId, "recurrence_type lookup should be linked.");
            }
        }

        // -- Power BI -------------------------------------------------------------------------

        /// <summary>
        /// Regression test for the bug fixed in commit e47493b: the workspaces merge used
        /// SELECT DISTINCT (workspace_id, name) and threw "Cannot insert duplicate key row"
        /// when two events in the same batch carried the same workspace_id with different names.
        /// Must now succeed and leave exactly one workspace row.
        /// </summary>
        [TestMethod]
        public async Task PowerBI_DuplicateWorkspaceIdDifferentNames_DoesNotThrow()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var workspaceId = "workspace-" + Guid.NewGuid().ToString("N");
                var event1 = BuildCommonEvent("ViewReport", "bi-1");
                var event2 = BuildCommonEvent("ViewReport", "bi-2");
                await PersistAuditEventAsync(db, event1, event2);

                var manager = NewManager();
                await manager.SaveSinglePowerBIEventToSqlStaging(new PowerBIAuditLogContent
                {
                    WorkspaceId = workspaceId,
                    WorkspaceName = "WS-Original-Name",
                    ReportId = "rep-" + Guid.NewGuid().ToString("N"),
                    ReportName = "Sales report",
                    ReportType = "PowerBIReport"
                }, event1);
                await manager.SaveSinglePowerBIEventToSqlStaging(new PowerBIAuditLogContent
                {
                    WorkspaceId = workspaceId,
                    WorkspaceName = "WS-Renamed",
                    ReportId = "rep-" + Guid.NewGuid().ToString("N"),
                    ReportName = "Marketing report",
                    ReportType = "PowerBIReport"
                }, event2);

                // Previously: BatchSaveException with IX_workspace_id violation.
                await manager.CommitAllChanges();

                var workspaceRows = await db.power_bi_workspaces
                    .Where(w => w.WorkspaceId == workspaceId)
                    .ToListAsync();
                Assert.AreEqual(1, workspaceRows.Count,
                    "Same workspace_id with two different display-names in one batch must collapse to exactly one workspace row.");
            }
        }

        /// <summary>
        /// A ViewReport event should populate workspace + report lookups and link them on event_meta_power_bi.
        /// </summary>
        [TestMethod]
        public async Task PowerBI_ReportEvent_PersistsWorkspaceReportAndMetadata()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("ViewReport", "bi-report");
                await PersistAuditEventAsync(db, commonEvent);

                var workspaceId = "workspace-" + Guid.NewGuid().ToString("N");
                var reportId = "rep-" + Guid.NewGuid().ToString("N");

                var manager = NewManager();
                await manager.SaveSinglePowerBIEventToSqlStaging(new PowerBIAuditLogContent
                {
                    WorkspaceId = workspaceId,
                    WorkspaceName = "Finance Workspace",
                    ReportId = reportId,
                    ReportName = "Q1 Report",
                    ReportType = "PowerBIReport"
                }, commonEvent);

                await manager.CommitAllChanges();

                var workspaceRow = await db.power_bi_workspaces.SingleOrDefaultAsync(w => w.WorkspaceId == workspaceId);
                Assert.IsNotNull(workspaceRow);
                Assert.AreEqual("Finance Workspace", workspaceRow.Name);

                var reportRow = await db.power_bi_reports.SingleOrDefaultAsync(r => r.ReportId == reportId);
                Assert.IsNotNull(reportRow);
                Assert.AreEqual("Q1 Report", reportRow.Name);
                Assert.AreEqual(workspaceRow.ID, reportRow.WorkspaceId);

                var meta = await db.power_bi_events.SingleOrDefaultAsync(m => m.EventID == commonEvent.Id);
                Assert.IsNotNull(meta);
                Assert.AreEqual(workspaceRow.ID, meta.WorkspaceId);
                Assert.AreEqual(reportRow.ID, meta.ReportId);
                Assert.IsNull(meta.DashboardId, "Report-only event should not link a dashboard.");
            }
        }

        /// <summary>
        /// A ViewDashboard event should populate the dashboard lookup and link only the dashboard on the metadata row.
        /// </summary>
        [TestMethod]
        public async Task PowerBI_DashboardEvent_PersistsDashboard()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("ViewDashboard", "bi-dash");
                await PersistAuditEventAsync(db, commonEvent);

                var workspaceId = "workspace-" + Guid.NewGuid().ToString("N");
                var dashboardId = "dash-" + Guid.NewGuid().ToString("N");

                var manager = NewManager();
                await manager.SaveSinglePowerBIEventToSqlStaging(new PowerBIAuditLogContent
                {
                    WorkspaceId = workspaceId,
                    WorkspaceName = "Ops Workspace",
                    DashboardId = dashboardId,
                    DashboardName = "Ops Dashboard"
                }, commonEvent);

                await manager.CommitAllChanges();

                var dashboardRow = await db.power_bi_dashboards.SingleOrDefaultAsync(d => d.DashboardId == dashboardId);
                Assert.IsNotNull(dashboardRow);
                Assert.AreEqual("Ops Dashboard", dashboardRow.Name);

                var meta = await db.power_bi_events.SingleOrDefaultAsync(m => m.EventID == commonEvent.Id);
                Assert.IsNotNull(meta);
                Assert.AreEqual(dashboardRow.ID, meta.DashboardId);
                Assert.IsNull(meta.ReportId, "Dashboard-only event should not link a report.");
            }
        }

        // -- Copilot Studio -------------------------------------------------------------------

        /// <summary>
        /// A Copilot Studio bot event should create the bot lookup and per-event metadata.
        /// </summary>
        [TestMethod]
        public async Task CopilotStudio_BotEvent_PersistsBotAndMetadata()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("BotPublished", "bot-pub");
                await PersistAuditEventAsync(db, commonEvent);

                var botId = "bot-" + Guid.NewGuid().ToString("N");

                var manager = NewManager();
                await manager.SaveSingleCopilotStudioEventToSqlStaging(new CopilotStudioAuditLogContent
                {
                    BotId = botId,
                    BotName = "HR Bot",
                    EnvironmentName = "env-test"
                }, commonEvent);

                await manager.CommitAllChanges();

                var botRow = await db.copilot_studio_bots.SingleOrDefaultAsync(b => b.BotId == botId);
                Assert.IsNotNull(botRow);
                Assert.AreEqual("HR Bot", botRow.Name);
                Assert.IsTrue(botRow.FirstSeenAt.HasValue);

                var meta = await db.copilot_studio_events.SingleOrDefaultAsync(m => m.EventID == commonEvent.Id);
                Assert.IsNotNull(meta);
                Assert.AreEqual(botRow.ID, meta.BotId);
            }
        }

        // -- Dataverse ------------------------------------------------------------------------

        /// <summary>
        /// A Dataverse CreateRecord event should populate the entity lookup and the per-event metadata
        /// including the record_id.
        /// </summary>
        [TestMethod]
        public async Task Dataverse_RecordEvent_PersistsEntityAndMetadata()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("CreateRecord", "dv-create");
                await PersistAuditEventAsync(db, commonEvent);

                var entityName = "custom_widget_" + Guid.NewGuid().ToString("N");
                var recordId = Guid.NewGuid().ToString();

                var manager = NewManager();
                await manager.SaveSingleDataverseEventToSqlStaging(new DataverseAuditLogContent
                {
                    EnvironmentName = "env-test",
                    EntityName = entityName,
                    RecordId = recordId
                }, commonEvent);

                await manager.CommitAllChanges();

                var entityRow = await db.dataverse_entities.SingleOrDefaultAsync(e => e.Name == entityName);
                Assert.IsNotNull(entityRow, "dataverse_entities lookup row must exist.");

                var meta = await db.dataverse_events.SingleOrDefaultAsync(m => m.EventID == commonEvent.Id);
                Assert.IsNotNull(meta);
                Assert.AreEqual(entityRow.ID, meta.EntityId);
                Assert.AreEqual(recordId, meta.RecordId);
            }
        }

        // -- Edge cases -----------------------------------------------------------------------

        /// <summary>
        /// Null audit-record or null base-event must be tolerated by every workload entry-point
        /// (matches the defensive guards in PowerPlatformAuditEventManager) - no exception and no rows.
        /// </summary>
        [TestMethod]
        public async Task Manager_NullInputs_HandledGracefully()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var preApps = await db.power_app_events.CountAsync();
                var preFlows = await db.power_automate_flow_events.CountAsync();
                var preBi = await db.power_bi_events.CountAsync();
                var preCs = await db.copilot_studio_events.CountAsync();
                var preDv = await db.dataverse_events.CountAsync();

                var manager = NewManager();

                await manager.SaveSinglePowerAppEventToSqlStaging(null, null);
                await manager.SaveSinglePowerAutomateEventToSqlStaging(null, null);
                await manager.SaveSinglePowerBIEventToSqlStaging(null, null);
                await manager.SaveSingleCopilotStudioEventToSqlStaging(null, null);
                await manager.SaveSingleDataverseEventToSqlStaging(null, null);

                // Should not throw - manager guards against null and the merge SQLs are no-ops on empty staging.
                await manager.CommitAllChanges();

                Assert.AreEqual(preApps, await db.power_app_events.CountAsync());
                Assert.AreEqual(preFlows, await db.power_automate_flow_events.CountAsync());
                Assert.AreEqual(preBi, await db.power_bi_events.CountAsync());
                Assert.AreEqual(preCs, await db.copilot_studio_events.CountAsync());
                Assert.AreEqual(preDv, await db.dataverse_events.CountAsync());
            }
        }

        /// <summary>
        /// Re-importing the same audit event must be idempotent: a second commit of the same
        /// (event_id, app_id) pair should not double-up the metadata row.
        /// </summary>
        [TestMethod]
        public async Task PowerApp_DuplicateImport_IsIdempotent()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var commonEvent = BuildCommonEvent("LaunchPowerApp", "app-idempotent");
                await PersistAuditEventAsync(db, commonEvent);

                var appId = "app-" + Guid.NewGuid().ToString("N");

                var content = new PowerAppsAuditLogContent
                {
                    AppName = appId,
                    AppDisplayName = "Idempotent App",
                    AppType = "Canvas",
                    ClientType = "Web"
                };

                var manager1 = NewManager();
                await manager1.SaveSinglePowerAppEventToSqlStaging(content, commonEvent);
                await manager1.CommitAllChanges();

                var manager2 = NewManager();
                await manager2.SaveSinglePowerAppEventToSqlStaging(content, commonEvent);
                await manager2.CommitAllChanges();

                var metaCount = await db.power_app_events.CountAsync(m => m.EventID == commonEvent.Id);
                Assert.AreEqual(1, metaCount, "Re-importing the same event must not create a second event_meta_power_app row.");

                var appCount = await db.power_apps.CountAsync(a => a.AppId == appId);
                Assert.AreEqual(1, appCount, "Re-importing must not create a second power_apps row.");
            }
        }
    }
}
