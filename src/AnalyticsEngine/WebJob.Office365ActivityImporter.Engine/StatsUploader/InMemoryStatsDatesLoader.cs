using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    /// <summary>
    /// In-memory fallback for <see cref="IStatsDatesLoader"/>. Used when Redis is not configured
    /// — the importer still uploads stats, but the "last uploaded" timestamp lives only for the
    /// lifetime of the host process.
    ///
    /// The backing field is <c>static</c> on purpose: the importer creates a new loader instance
    /// per import cycle inside its <c>while (runAgain)</c> loop, so an instance field would reset
    /// every cycle and defeat the <c>MIN_WAIT</c> throttle on <see cref="UsageStatsManager"/>
    /// that's supposed to stop us flooding the stats endpoint on short import cycles. With a
    /// process-static field the throttle is honoured for the lifetime of the WebJob process; if
    /// the process is recycled the next cycle uploads again — that's an acceptable trade-off vs
    /// the Redis-backed loader which persists across process restarts.
    /// </summary>
    public class InMemoryStatsDatesLoader : IStatsDatesLoader
    {
        private static readonly object _lock = new object();
        private static DateTime? _lastUploadDt;

        public Task<DateTime?> GetLastUploadDt()
        {
            lock (_lock)
            {
                return Task.FromResult(_lastUploadDt);
            }
        }

        public Task RegisterLastUploadDt()
        {
            lock (_lock)
            {
                _lastUploadDt = DateTime.Now;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Test hook — resets the process-static last-upload timestamp.
        /// </summary>
        internal static void ResetForTests()
        {
            lock (_lock)
            {
                _lastUploadDt = null;
            }
        }
    }
}
