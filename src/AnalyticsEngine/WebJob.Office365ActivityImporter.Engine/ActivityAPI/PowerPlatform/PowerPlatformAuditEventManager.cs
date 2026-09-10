using Common.Entities;
using DataUtils;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform
{
    /// <summary>
    /// Saves Power Platform event metadata to SQL via staging tables + merge scripts.
    /// Covers Power Apps, Power Automate, Power BI, and Copilot Studio - all driven
    /// by the same Audit.General content-type subscription. Mirrors CopilotAuditEventManager.
    /// </summary>
    public class PowerPlatformAuditEventManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IPowerPlatformStagingWriter _stagingWriter;

        private int _totalAppCount;
        private int _totalAppShareCount;
        private int _totalFlowCount;
        private int _totalFlowShareCount;
        private int _totalPowerBiCount;
        private int _totalCopilotStudioCount;

        /// <summary>
        /// Production entry point: builds the SQL staging writer from a connection string. Kept as a thin
        /// overload so SaveSession.Init() and every other existing call site is unchanged. See issue #367.
        /// </summary>
        public PowerPlatformAuditEventManager(string connectionString, ILogger logger)
            : this(new SqlPowerPlatformStagingWriter(connectionString, logger), logger)
        {
        }

        /// <summary>
        /// Testable entry point: the adaptation rules run against any <see cref="IPowerPlatformStagingWriter"/>,
        /// so client-type normalisation, connector joining and share expansion can be asserted with no database.
        /// </summary>
        internal PowerPlatformAuditEventManager(IPowerPlatformStagingWriter stagingWriter, ILogger logger)
        {
            _stagingWriter = stagingWriter ?? throw new ArgumentNullException(nameof(stagingWriter));
            _logger = logger;
        }

        public int StagedAppCount => _totalAppCount;
        public int StagedAppShareCount => _totalAppShareCount;
        public int StagedFlowCount => _totalFlowCount;
        public int StagedFlowShareCount => _totalFlowShareCount;
        public int StagedPowerBiCount => _totalPowerBiCount;
        public int StagedCopilotStudioCount => _totalCopilotStudioCount;

        public Task SaveSinglePowerAppEventToSqlStaging(PowerAppsAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger.LogWarning("PowerPlatformAuditEventManager received null PowerApps auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            _stagingWriter.StagePowerApp(new PowerAppLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                AppId = auditRecord.AppName,
                AppName = string.IsNullOrEmpty(auditRecord.AppDisplayName) ? auditRecord.AppName : auditRecord.AppDisplayName,
                EnvironmentId = auditRecord.EnvironmentName,
                EnvironmentName = auditRecord.EnvironmentDisplayName,
                AppSessionId = auditRecord.AppSessionId,
                ClientType = NormaliseClientType(auditRecord.ClientType, auditRecord.UserAgent),
                ConnectorsCsv = JoinConnectors(auditRecord.ConnectionReferences),
                EventTime = baseOfficeEvent.TimeStamp,
            });
            _totalAppCount++;

            // If the event includes a Permissions array (ShareApp / AddPermissionsToApp), record each recipient.
            if (auditRecord.Permissions != null)
            {
                foreach (var p in auditRecord.Permissions)
                {
                    if (string.IsNullOrEmpty(p?.PrincipalName)) continue;
                    _stagingWriter.StagePowerAppShare(new PowerAppShareLogTempEntity
                    {
                        EventId = baseOfficeEvent.Id,
                        AppId = auditRecord.AppName,
                        SharedWithUpn = p.PrincipalName,
                        RoleName = p.RoleName,
                    });
                    _totalAppShareCount++;
                }
            }

            return Task.CompletedTask;
        }

        public Task SaveSinglePowerAutomateEventToSqlStaging(PowerAutomateAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger.LogWarning("PowerPlatformAuditEventManager received null PowerAutomate auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            auditRecord.NormaliseDocumentedFields();
            if (string.IsNullOrEmpty(auditRecord.FlowId))
            {
                _logger.LogWarning(
                    $"PowerPlatformAuditEventManager: Power Automate {auditRecord.Operation} event '{auditRecord.Id}' has no flow identity - skipping staging row.");
                return Task.CompletedTask;
            }

            _stagingWriter.StageFlow(new PowerAutomateFlowLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                FlowId = auditRecord.FlowId,
                FlowName = string.IsNullOrEmpty(auditRecord.FlowDisplayName) ? auditRecord.FlowId : auditRecord.FlowDisplayName,
                EnvironmentId = auditRecord.EnvironmentName,
                EnvironmentName = auditRecord.EnvironmentDisplayName,
                RunId = auditRecord.RunId,
                ConnectorsCsv = JoinConnectors(auditRecord.ConnectionReferences),
                EventTime = baseOfficeEvent.TimeStamp,
            });
            _totalFlowCount++;

            if (auditRecord.Permissions != null)
            {
                foreach (var p in auditRecord.Permissions)
                {
                    if (string.IsNullOrEmpty(p?.PrincipalName)) continue;
                    _stagingWriter.StageFlowShare(new PowerAutomateFlowShareLogTempEntity
                    {
                        EventId = baseOfficeEvent.Id,
                        FlowId = auditRecord.FlowId,
                        SharedWithUpn = p.PrincipalName,
                        RoleName = p.RoleName,
                    });
                    _totalFlowShareCount++;
                }
            }

            return Task.CompletedTask;
        }

        public Task SaveSinglePowerBIEventToSqlStaging(PowerBIAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger.LogWarning("PowerPlatformAuditEventManager received null PowerBI auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            // Defensive: a ViewReport without a workspace+report id is structurally invalid -
            // persisting the staging row would land a NULL-FK row in event_meta_power_bi because
            // the merge LEFT JOINs would have nothing to match. Skip + warn so a schema change
            // surfaces in the logs instead of silently filling the metadata table with NULLs.
            if (string.IsNullOrEmpty(auditRecord.WorkspaceId) || string.IsNullOrEmpty(auditRecord.ReportId))
            {
                _logger.LogWarning($"PowerPlatformAuditEventManager: PowerBI {auditRecord.Operation} event '{auditRecord.Id}' is missing WorkspaceId or ReportId - skipping staging row to avoid a NULL-FK event_meta_power_bi row.");
                return Task.CompletedTask;
            }

            _stagingWriter.StagePowerBi(new PowerBILogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                WorkspaceId = auditRecord.WorkspaceId,
                WorkspaceName = auditRecord.WorkspaceName,
                ReportId = auditRecord.ReportId,
                ReportName = auditRecord.ReportName,
                ReportType = auditRecord.ReportType,
                DashboardId = auditRecord.DashboardId,
                DashboardName = auditRecord.DashboardName,
                EventTime = baseOfficeEvent.TimeStamp,
            });
            _totalPowerBiCount++;
            return Task.CompletedTask;
        }

        public Task SaveSingleCopilotStudioEventToSqlStaging(CopilotStudioAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger.LogWarning("PowerPlatformAuditEventManager received null CopilotStudio auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            if (string.IsNullOrEmpty(auditRecord.BotId))
            {
                _logger.LogWarning(
                    $"PowerPlatformAuditEventManager: Copilot Studio {auditRecord.Operation} event '{auditRecord.Id}' has no BotId - skipping staging row.");
                return Task.CompletedTask;
            }

            _stagingWriter.StageCopilotStudio(new CopilotStudioLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                BotId = auditRecord.BotId,
                BotName = string.IsNullOrEmpty(auditRecord.BotName) ? auditRecord.BotSchemaName : auditRecord.BotName,
                EnvironmentId = string.IsNullOrEmpty(auditRecord.EnvironmentId) ? auditRecord.EnvironmentName : auditRecord.EnvironmentId,
                EventTime = baseOfficeEvent.TimeStamp,
            });
            _totalCopilotStudioCount++;
            return Task.CompletedTask;
        }

        private static string JoinConnectors(System.Collections.Generic.List<PowerPlatformConnectionRef> refs)
        {
            if (refs == null || refs.Count == 0) return null;
            var names = refs.Where(r => !string.IsNullOrEmpty(r?.ConnectorName))
                            .Select(r => r.ConnectorName.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
            return names.Length == 0 ? null : string.Join("|", names);
        }

        /// <summary>
        /// Map raw client signals (UserAgent or explicit ClientType) into a small canonical set.
        /// </summary>
        private static string NormaliseClientType(string clientType, string userAgent)
        {
            if (!string.IsNullOrEmpty(clientType)) return clientType;
            if (string.IsNullOrEmpty(userAgent)) return null;
            var ua = userAgent.ToLowerInvariant();
            if (ua.Contains("teams")) return "Teams";
            if (ua.Contains("mobile") || ua.Contains("android") || ua.Contains("iphone") || ua.Contains("ipad")) return "Mobile";
            if (ua.Contains("electron") || ua.Contains("powerappsdesktop")) return "Desktop";
            return "Web";
        }

        /// <summary>
        /// Flush staging tables and run the workload-specific merge scripts.
        /// </summary>
        public async Task CommitAllChanges()
        {
            _logger.LogDebug($"PowerPlatform commit: {_totalAppCount} app events, {_totalAppShareCount} app shares, " +
                $"{_totalFlowCount} flow events, {_totalFlowShareCount} flow shares, {_totalPowerBiCount} Power BI events, " +
                $"{_totalCopilotStudioCount} Copilot Studio events.");

            // The staging tables and merge scripts now live in the writer (see #367).
            await _stagingWriter.CommitAllChanges();

            _totalAppCount = 0;
            _totalAppShareCount = 0;
            _totalFlowCount = 0;
            _totalFlowShareCount = 0;
            _totalPowerBiCount = 0;
            _totalCopilotStudioCount = 0;
        }

        public void Dispose()
        {
        }
    }
}
