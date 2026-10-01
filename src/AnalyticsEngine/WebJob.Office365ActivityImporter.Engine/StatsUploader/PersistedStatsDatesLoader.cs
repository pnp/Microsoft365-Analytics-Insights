using Common.Entities.State;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    /// <summary>
    /// Durable <see cref="IStatsDatesLoader"/>: when usage stats were last uploaded, kept in the runtime state store
    /// (<see cref="StatePartitions.ImportSchedule"/>) so the once-a-day upload throttle survives WebJob restarts.
    /// </summary>
    public class PersistedStatsDatesLoader : KeyValueSingleDateStore, IStatsDatesLoader
    {
        /// <summary>The row key in <see cref="StatePartitions.ImportSchedule"/>.</summary>
        public const string StatsLastUploadedKey = "statsLastUploaded";

        public PersistedStatsDatesLoader(IKeyValueStore store) : base(store, StatsLastUploadedKey)
        {
        }

        // IStatsDatesLoader implementations
        public async Task<DateTime?> GetLastUploadDt()
        {
            return await base.GetLastDT();
        }

        public async Task RegisterLastUploadDt()
        {
            await base.SaveDT();
        }
        public async Task RegisterLastUploadDt(DateTime lastUpload)
        {
            await base.SaveDT(lastUpload);
        }
    }
}
