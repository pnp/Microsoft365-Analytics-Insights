using Microsoft.Extensions.Logging;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform
{
    /// <summary>
    /// Routing-stage filters for Power Platform / Power Automate audit events.
    ///
    /// The activity report loader sees every audit event across every workload; this class
    /// owns the rules that decide which Power Automate operations are worth persisting,
    /// so the loader stays a thin dispatcher and the rules + their log messages live in
    /// one place.
    /// </summary>
    public static class PowerPlatformAuditLogFilter
    {
        /// <summary>
        /// Returns true when an audit event with this <paramref name="operation"/> should be
        /// persisted as a Power Automate flow event. Only flow-run operations (see
        /// <see cref="ActivityImportConstants.PowerPlatformOps.FlowRunOps"/>) are kept;
        /// lifecycle operations (CreateFlow, EditFlow, DeleteFlow, SharedFlow, ...) are
        /// dropped here, before any staging or merge work happens.
        ///
        /// When the operation is rejected, an informational log line is written naming the
        /// operation so unexpected/unknown names can be discovered from real audit data and
        /// added to <see cref="ActivityImportConstants.PowerPlatformOps.FlowRunOps"/>.
        /// </summary>
        public static bool ShouldPersistPowerAutomateOperation(string operation, ILogger logger)
        {
            if (ActivityImportConstants.PowerPlatformOps.IsFlowRunOp(operation))
            {
                return true;
            }

            logger?.LogInformation($"PowerPlatform: skipping Power Automate event with non-run operation '{operation}'. Only flow-run operations are persisted - extend ActivityImportConstants.PowerPlatformOps.FlowRunOps if this should be tracked.");
            return false;
        }
    }
}
