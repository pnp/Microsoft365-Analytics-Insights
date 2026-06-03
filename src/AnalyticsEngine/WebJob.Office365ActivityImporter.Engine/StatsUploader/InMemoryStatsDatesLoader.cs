using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    /// <summary>
    /// In-memory fallback for <see cref="IStatsDatesLoader"/>. Used when Redis is not configured —
    /// the importer still uploads stats, but the "last uploaded" timestamp lives only for the
    /// lifetime of this instance.
    ///
    /// IMPORTANT: callers must hold a single instance for the lifetime of the WebJob process
    /// (i.e. construct it ONCE, outside the per-cycle <c>while(runAgain)</c> loop). A fresh
    /// instance per cycle would always return null from <see cref="GetLastUploadDt"/> and defeat
    /// the throttle in <see cref="UsageStatsManager"/>, hammering the stats endpoint.
    /// </summary>
    public class InMemoryStatsDatesLoader : IStatsDatesLoader
    {
        private readonly object _lock = new object();
        private DateTime? _lastUploadDt;

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
    }
}
