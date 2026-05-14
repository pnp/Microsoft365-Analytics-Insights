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
    /// Covers Power Apps, Power Automate, Power BI, Copilot Studio, Dataverse - all driven
    /// by the same Audit.General content-type subscription. Mirrors CopilotAuditEventManager.
    /// </summary>
    public class PowerPlatformAuditEventManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly ProjectResourceReader _rr;

        private readonly InsertBatch<PowerAppLogTempEntity> _appInserts;
        private readonly InsertBatch<PowerAppShareLogTempEntity> _appShareInserts;
        private readonly InsertBatch<PowerAutomateFlowLogTempEntity> _flowInserts;
        private readonly InsertBatch<PowerAutomateFlowShareLogTempEntity> _flowShareInserts;
        private readonly InsertBatch<PowerBILogTempEntity> _powerBiInserts;
        private readonly InsertBatch<CopilotStudioLogTempEntity> _copilotStudioInserts;
        private readonly InsertBatch<DataverseLogTempEntity> _dataverseInserts;

        private int _totalAppCount;
        private int _totalAppShareCount;
        private int _totalFlowCount;
        private int _totalFlowShareCount;
        private int _totalPowerBiCount;
        private int _totalCopilotStudioCount;
        private int _totalDataverseCount;

        public PowerPlatformAuditEventManager(string connectionString, ILogger logger)
        {
            _rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            _logger = logger;
            _appInserts = new InsertBatch<PowerAppLogTempEntity>(connectionString, logger);
            _appShareInserts = new InsertBatch<PowerAppShareLogTempEntity>(connectionString, logger);
            _flowInserts = new InsertBatch<PowerAutomateFlowLogTempEntity>(connectionString, logger);
            _flowShareInserts = new InsertBatch<PowerAutomateFlowShareLogTempEntity>(connectionString, logger);
            _powerBiInserts = new InsertBatch<PowerBILogTempEntity>(connectionString, logger);
            _copilotStudioInserts = new InsertBatch<CopilotStudioLogTempEntity>(connectionString, logger);
            _dataverseInserts = new InsertBatch<DataverseLogTempEntity>(connectionString, logger);
        }

        public int StagedAppCount => _totalAppCount;
        public int StagedAppShareCount => _totalAppShareCount;
        public int StagedFlowCount => _totalFlowCount;
        public int StagedFlowShareCount => _totalFlowShareCount;
        public int StagedPowerBiCount => _totalPowerBiCount;
        public int StagedCopilotStudioCount => _totalCopilotStudioCount;
        public int StagedDataverseCount => _totalDataverseCount;

        public Task SaveSinglePowerAppEventToSqlStaging(PowerAppsAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger.LogWarning("PowerPlatformAuditEventManager received null PowerApps auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            _appInserts.Rows.Add(new PowerAppLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                AppId = auditRecord.AppName,
                AppName = string.IsNullOrEmpty(auditRecord.AppDisplayName) ? auditRecord.AppName : auditRecord.AppDisplayName,
                EnvironmentId = auditRecord.EnvironmentName,
                AppSessionId = auditRecord.AppSessionId,
                AppType = auditRecord.AppType,
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
                    _appShareInserts.Rows.Add(new PowerAppShareLogTempEntity
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

            _flowInserts.Rows.Add(new PowerAutomateFlowLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                FlowId = auditRecord.FlowId,
                FlowName = string.IsNullOrEmpty(auditRecord.FlowDisplayName) ? auditRecord.FlowId : auditRecord.FlowDisplayName,
                EnvironmentId = auditRecord.EnvironmentName,
                RunId = auditRecord.RunId,
                RecurrenceType = auditRecord.RecurrenceType,
                ConnectorsCsv = JoinConnectors(auditRecord.ConnectionReferences),
                EventTime = baseOfficeEvent.TimeStamp,
            });
            _totalFlowCount++;

            if (auditRecord.Permissions != null)
            {
                foreach (var p in auditRecord.Permissions)
                {
                    if (string.IsNullOrEmpty(p?.PrincipalName)) continue;
                    _flowShareInserts.Rows.Add(new PowerAutomateFlowShareLogTempEntity
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

            _powerBiInserts.Rows.Add(new PowerBILogTempEntity
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

            _copilotStudioInserts.Rows.Add(new CopilotStudioLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                BotId = auditRecord.BotId,
                BotName = auditRecord.BotName,
                EnvironmentId = auditRecord.EnvironmentName,
                EventTime = baseOfficeEvent.TimeStamp,
            });
            _totalCopilotStudioCount++;
            return Task.CompletedTask;
        }

        public Task SaveSingleDataverseEventToSqlStaging(DataverseAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger.LogWarning("PowerPlatformAuditEventManager received null Dataverse auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            _dataverseInserts.Rows.Add(new DataverseLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                EnvironmentId = auditRecord.EnvironmentName,
                EntityName = auditRecord.EntityName,
                RecordId = auditRecord.RecordId,
            });
            _totalDataverseCount++;
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
                $"{_totalCopilotStudioCount} Copilot Studio events, {_totalDataverseCount} Dataverse events.");

            await _appInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_APP, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_app_events_from_staging_table.sql"));
            await _appShareInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_APP_SHARE, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_app_share_events_from_staging_table.sql"));
            await _flowInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_AUTOMATE, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_automate_events_from_staging_table.sql"));
            await _flowShareInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_AUTOMATE_SHARE, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_automate_share_events_from_staging_table.sql"));
            await _powerBiInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_BI, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_bi_events_from_staging_table.sql"));
            await _copilotStudioInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_COPILOT_STUDIO, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_copilot_studio_events_from_staging_table.sql"));
            await _dataverseInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_DATAVERSE, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_dataverse_events_from_staging_table.sql"));

            _appInserts.Rows.Clear();
            _appShareInserts.Rows.Clear();
            _flowInserts.Rows.Clear();
            _flowShareInserts.Rows.Clear();
            _powerBiInserts.Rows.Clear();
            _copilotStudioInserts.Rows.Clear();
            _dataverseInserts.Rows.Clear();
            _totalAppCount = 0;
            _totalAppShareCount = 0;
            _totalFlowCount = 0;
            _totalFlowShareCount = 0;
            _totalPowerBiCount = 0;
            _totalCopilotStudioCount = 0;
            _totalDataverseCount = 0;
        }

        private string GetSql(string tempTableName, string embeddedScriptName)
        {
            return _rr.ReadResourceString(embeddedScriptName)
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName);
        }

        public void Dispose()
        {
        }
    }
}
