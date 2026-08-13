using Microsoft.Extensions.Logging;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform
{
    /// <summary>
    /// Routing-stage validation for Power Platform / Power Automate audit events.
    ///
    /// The activity report loader sees every audit event across every workload; this class
    /// owns the rules that decide whether a Power Automate record identifies a flow that can
    /// be persisted, so the loader stays a thin dispatcher.
    /// </summary>
    public static class PowerPlatformAuditLogFilter
    {
        /// <summary>
        /// Normalises Microsoft's documented Power Automate fields and returns true when the
        /// record identifies a flow. Purview provides lifecycle and permission events rather
        /// than individual flow runs, so filtering by a guessed run-operation name would drop
        /// the records that carry flow, connector, and sharing metadata.
        /// </summary>
        public static bool ShouldPersistPowerAutomateRecord(PowerAutomateAuditLogContent auditRecord, ILogger logger)
        {
            if (auditRecord == null)
            {
                return false;
            }

            auditRecord.NormaliseDocumentedFields();
            if (!string.IsNullOrEmpty(auditRecord.FlowId))
            {
                return true;
            }

            logger?.LogDebug(
                $"PowerPlatform: skipping Power Automate event '{auditRecord.Operation}' because it has neither a FlowId nor a usable FlowDetailsUrl.");
            return false;
        }
    }
}
