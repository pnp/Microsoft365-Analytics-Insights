using System.Diagnostics;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.Entities;

namespace Tests.UnitTests.StressHarness
{
    /// <summary>
    /// Thin decorator over a real <see cref="IActivityReportPersistenceManager"/> that accumulates
    /// per-run save metrics (number of CommitAll calls = batches, events committed, total time inside the
    /// serialized SQL save phase, and the merged <see cref="ImportStat"/>). Lets the load test report how
    /// much of the wall-time is the serialized SQL save vs. the (parallel) download side - i.e. the thing
    /// the save-serialization optimisation (C) targets.
    /// </summary>
    public class CountingActivityReportPersistenceManager : IActivityReportPersistenceManager
    {
        private readonly IActivityReportPersistenceManager _inner;
        private readonly object _lock = new object();

        public CountingActivityReportPersistenceManager(IActivityReportPersistenceManager inner)
        {
            _inner = inner;
            MergedStats = new ImportStat();
        }

        public long CommitAllCalls { get; private set; }
        public long EventsIntoCommit { get; private set; }
        public double TotalCommitMs { get; private set; }
        public ImportStat MergedStats { get; }

        public async Task<ImportStat> CommitAll(ActivityReportSet activities)
        {
            int count = activities.Count;
            var sw = Stopwatch.StartNew();
            var stat = await _inner.CommitAll(activities);
            sw.Stop();

            lock (_lock)
            {
                CommitAllCalls++;
                EventsIntoCommit += count;
                TotalCommitMs += sw.Elapsed.TotalMilliseconds;
                MergedStats.AddStats(stat);
            }
            return stat;
        }
    }
}
