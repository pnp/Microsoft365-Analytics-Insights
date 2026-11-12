using Microsoft.Extensions.Logging;
using System;

namespace Common.Entities.CopilotCosts
{
    /// <summary>
    /// When the importer next asks the Copilot cost collector for a slice, and how loudly to log what it said.
    /// </summary>
    /// <remarks>
    /// <para>Hold ONE for the life of the process. The importer rebuilds its per-cycle objects every cycle, and the
    /// back-off used to live on one of them, so it was lost at every cycle boundary (#847, R8-13).</para>
    /// <para>The cycle pause polls every five seconds, and every outcome used to be logged at Information: about 16,000
    /// lines a day. An outcome is now logged at its own level only when it differs from the previous one; a warning that
    /// persists is repeated once every <see cref="WarningRepeatInterval"/>, so any log window of that length shows it; any
    /// other repeat is Debug.</para>
    /// </remarks>
    public sealed class CostCollectorPollGate
    {
        /// <summary>How often a warning outcome that has not changed is logged again.</summary>
        public static readonly TimeSpan WarningRepeatInterval = TimeSpan.FromHours(1);

        private readonly object _lock = new object();
        private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
        private string _lastOutcome;
        private DateTimeOffset _lastLoggedUtc = DateTimeOffset.MinValue;

        /// <summary>Whether a slice may run now, or the last outcome's back-off is still running.</summary>
        public bool IsDue(DateTimeOffset nowUtc)
        {
            lock (_lock)
            {
                return nowUtc >= _retryAt;
            }
        }

        /// <summary>
        /// Records a slice's outcome - backing off unless there is more work to do - and returns the level to log it at:
        /// <see cref="LogLevel.Warning"/> or <see cref="LogLevel.Information"/> when it changed (or a warning is due its
        /// hourly repeat), otherwise <see cref="LogLevel.Debug"/>.
        /// </summary>
        public LogLevel Record(string outcome, DateTimeOffset nowUtc)
        {
            lock (_lock)
            {
                if (!HasMoreWork(outcome))
                {
                    _retryAt = nowUtc.AddMinutes(outcome == "idle" ? 1 : 5);
                }

                var warning = IsWarning(outcome);
                var changed = !string.Equals(outcome, _lastOutcome, StringComparison.Ordinal);
                _lastOutcome = outcome;

                var level = changed || (warning && nowUtc - _lastLoggedUtc >= WarningRepeatInterval)
                    ? (warning ? LogLevel.Warning : LogLevel.Information)
                    : LogLevel.Debug;
                if (level != LogLevel.Debug)
                {
                    _lastLoggedUtc = nowUtc;
                }
                return level;
            }
        }

        /// <summary>The collector read something and may have more: ask again at the next poll.</summary>
        private static bool HasMoreWork(string outcome) => outcome == "sliceCompleted" || outcome == "observed" || outcome == "partial";

        /// <summary>Only stable, application-authored failure codes; never exception text, SQL or request URLs.</summary>
        public static bool IsWarning(string outcome) =>
            CostApiHealth.SafeCode(outcome) == outcome || outcome == "configurationRequired" || outcome == "storageRequired";
    }
}
