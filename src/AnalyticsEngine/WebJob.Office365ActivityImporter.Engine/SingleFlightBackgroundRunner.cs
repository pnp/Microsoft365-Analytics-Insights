using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Runs one piece of long background work at most one run at a time ("single flight"), without the caller waiting
    /// for it.
    ///
    /// Built for the deferred Graph pass, the once-a-day usage reports (issue #706). On a large tenant that phase takes
    /// hours. The import cycle is a loop that waits for each step before starting the next, so running the phase
    /// anywhere inside it only changes which audit import waits for it. Started here, it runs alongside the following
    /// cycles instead, and those cycles do not start another run while it is still going.
    /// </summary>
    /// <remarks>
    /// <para>Each run gets its own telemetry operation id (<see cref="AnalyticsLogger.BeginOperationScope"/>). The id
    /// is held in an <c>AsyncLocal</c>, so it follows the run and does not change the operation of the cycle that
    /// started it.</para>
    /// <para>The task in <see cref="InFlight"/> never faults. A run that throws is tracked and logged here, because
    /// nobody awaits the task in a Release build, and an exception in a task that nobody awaits would go unobserved.</para>
    /// <para>A process stop in the middle of a run abandons it, as it would have abandoned the same work run inline.</para>
    /// </remarks>
    public sealed class SingleFlightBackgroundRunner
    {
        private readonly AnalyticsLogger _logger;
        private readonly string _name;
        private readonly object _sync = new object();
        private Task _inFlight = Task.CompletedTask;
        private DateTime _runStartedUtc;
        private string _runOperationId;

        /// <param name="name">Operator-facing name of the work, used in every log line this class writes.</param>
        public SingleFlightBackgroundRunner(AnalyticsLogger logger, string name)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("The work needs a name for its log lines.", nameof(name));
            _name = name;
        }

        /// <summary>
        /// The current run, or a completed task when none is in progress. It never faults, so it can be awaited
        /// without a try/catch - for example by a Debug build before the process exits.
        /// </summary>
        public Task InFlight
        {
            get
            {
                lock (_sync)
                {
                    return _inFlight;
                }
            }
        }

        /// <summary>Whether a run is in progress.</summary>
        public bool IsRunning => !InFlight.IsCompleted;

        /// <summary>
        /// Starts <paramref name="work"/> on the thread pool and returns without waiting for it, unless a run started
        /// earlier is still in progress. Then nothing is started and an information line says so.
        /// </summary>
        /// <returns>True when a run was started.</returns>
        public bool TryStart(Func<Task> work)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));

            lock (_sync)
            {
                if (!_inFlight.IsCompleted)
                {
                    _logger.LogInformation($"{_name} is still running from an earlier cycle (started {_runStartedUtc:u}, " +
                        $"operation '{_runOperationId}'), so another run is not started this cycle.");
                    return false;
                }

                var operationId = Guid.NewGuid().ToString("N");
                _runStartedUtc = DateTime.UtcNow;
                _runOperationId = operationId;

                _logger.LogInformation($"{_name}: started in the background as operation '{operationId}'. " +
                    "The import cycle carries on without waiting for it.");

                _inFlight = Task.Run(() => RunObservedAsync(work, operationId));
                return true;
            }
        }

        private async Task RunObservedAsync(Func<Task> work, string operationId)
        {
            using (_logger.BeginOperationScope(operationId))
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    try
                    {
                        _logger.TrackException(ex);
                        _logger.LogCritical($"Got exception on {_name}: {ex.Message}");
                    }
                    catch
                    {
                        // A failure to log must not fault the task: in Release nothing awaits it.
                    }
                }
            }
        }
    }
}
