using System;
using System.Threading;

namespace DataUtils.Health
{
    /// <summary>
    /// Process liveness, independent of cycle length. A heartbeat is not proof that an import succeeded.
    /// </summary>
    public sealed class ImporterHeartbeat : IDisposable
    {
        public const string ActivityJob = "Office365ActivityImporter";
        public const string WebTrafficJob = "AppInsightsImporter";
        private readonly object _gate = new object();
        private readonly AnalyticsLogger _logger;
        private readonly string _jobName;
        private readonly StageTracker _stages;
        private readonly Timer _timer;
        private readonly DateTime _processStartedUtc = DateTime.UtcNow;
        private DateTime? _lastCycleUtc;
        private double? _lastCycleSeconds;
        private bool _disposed;

        public ImporterHeartbeat(AnalyticsLogger logger, string jobName)
            : this(logger, jobName, TimeSpan.FromMinutes(1))
        {
        }

        internal ImporterHeartbeat(AnalyticsLogger logger, string jobName, TimeSpan interval, StageTracker stages = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (jobName != ActivityJob && jobName != WebTrafficJob)
                throw new ArgumentException("Unrecognised importer job.", nameof(jobName));
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            _jobName = jobName;
            _stages = stages ?? StageTracker.Process;
            _timer = new Timer(_ => Emit(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Emit();
            _timer.Change(interval, interval);
        }

        public void CycleCompleted(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ImporterHeartbeat));
                _lastCycleUtc = DateTime.UtcNow;
                _lastCycleSeconds = elapsed.TotalSeconds;
            }
            Emit();
        }

        private void Emit()
        {
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    var cycleStage = _stages.Snapshot(StageSlot.Cycle);
                    var backgroundStage = _stages.Snapshot(StageSlot.Background);
                    _logger.TrackImporterHeartbeat(_jobName, _lastCycleUtc, _lastCycleSeconds, _processStartedUtc,
                        ProcessIdentity.BuildLabel, cycleStage?.Stage, cycleStage?.StartedUtc,
                        backgroundStage?.Stage, backgroundStage?.StartedUtc);
                }
                catch (Exception ex)
                {
                    // An instrumentation failure must not terminate a timer thread and kill the importer.
                    Console.Error.WriteLine("Importer heartbeat telemetry failed (" + ex.GetType().Name + "). Import continues.");
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _timer.Dispose();
            }
        }
    }
}
