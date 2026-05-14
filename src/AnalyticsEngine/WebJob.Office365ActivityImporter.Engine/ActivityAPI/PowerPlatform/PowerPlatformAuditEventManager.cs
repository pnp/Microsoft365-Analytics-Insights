using Common.Entities;
using DataUtils;
using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform
{
    /// <summary>
    /// Saves Power Platform event metadata to SQL via staging tables + merge scripts.
    /// Mirrors the CopilotAuditEventManager pattern.
    /// </summary>
    public class PowerPlatformAuditEventManager : IDisposable
    {
        private readonly ILogger _logger;
        private readonly InsertBatch<PowerAppLogTempEntity> _appInserts;
        private readonly InsertBatch<PowerAutomateFlowLogTempEntity> _flowInserts;
        private readonly InsertBatch<PowerPlatformAdminLogTempEntity> _adminInserts;
        private readonly ProjectResourceReader _rr;

        private int _totalAppCount;
        private int _totalFlowCount;
        private int _totalAdminCount;

        public PowerPlatformAuditEventManager(string connectionString, ILogger logger)
        {
            _rr = new ProjectResourceReader(System.Reflection.Assembly.GetExecutingAssembly());
            _logger = logger;
            _appInserts = new InsertBatch<PowerAppLogTempEntity>(connectionString, logger);
            _flowInserts = new InsertBatch<PowerAutomateFlowLogTempEntity>(connectionString, logger);
            _adminInserts = new InsertBatch<PowerPlatformAdminLogTempEntity>(connectionString, logger);
        }

        public int StagedAppCount => _totalAppCount;
        public int StagedFlowCount => _totalFlowCount;
        public int StagedAdminCount => _totalAdminCount;

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
            });
            _totalAppCount++;
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
            });
            _totalFlowCount++;
            return Task.CompletedTask;
        }

        public Task SaveSinglePowerPlatformAdminEventToSqlStaging(PowerPlatformAdminAuditLogContent auditRecord, CommonAuditEvent baseOfficeEvent)
        {
            if (auditRecord == null || baseOfficeEvent == null)
            {
                _logger.LogWarning("PowerPlatformAuditEventManager received null PowerPlatformAdmin auditRecord or baseOfficeEvent.");
                return Task.CompletedTask;
            }

            _adminInserts.Rows.Add(new PowerPlatformAdminLogTempEntity
            {
                EventId = baseOfficeEvent.Id,
                EnvironmentId = auditRecord.EnvironmentName,
                EventJson = SafeSerialiseEvent(auditRecord),
            });
            _totalAdminCount++;
            return Task.CompletedTask;
        }

        private string SafeSerialiseEvent(AbstractAuditLogContent auditRecord)
        {
            if (!string.IsNullOrEmpty(auditRecord.OriginalImportFileContents))
            {
                return auditRecord.OriginalImportFileContents;
            }
            try
            {
                return JsonConvert.SerializeObject(auditRecord);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to serialise PowerPlatformAdmin audit event JSON");
                return null;
            }
        }

        /// <summary>
        /// Flush staging tables and run the workload-specific merge scripts, clearing internal state on completion.
        /// </summary>
        public async Task CommitAllChanges()
        {
            _logger.LogDebug($"PowerPlatform commit: {_totalAppCount} Power Apps, {_totalFlowCount} flow events, {_totalAdminCount} admin events.");

            await _appInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_APP, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_app_events_from_staging_table.sql"));
            await _flowInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_AUTOMATE, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_automate_events_from_staging_table.sql"));
            await _adminInserts.SaveToStagingTable(GetSql(ActivityImportConstants.STAGING_TABLE_POWER_PLATFORM_ADMIN, "WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform.SQL.insert_power_platform_admin_events_from_staging_table.sql"));

            _appInserts.Rows.Clear();
            _flowInserts.Rows.Clear();
            _adminInserts.Rows.Clear();
            _totalAppCount = 0;
            _totalFlowCount = 0;
            _totalAdminCount = 0;
        }

        private string GetSql(string tempTableName, string embeddedScriptName)
        {
            return _rr.ReadResourceString(embeddedScriptName)
                .Replace(ActivityImportConstants.STAGING_TABLE_VARNAME, tempTableName);
        }

        public void Dispose()
        {
            // No-op for now.
        }
    }
}
