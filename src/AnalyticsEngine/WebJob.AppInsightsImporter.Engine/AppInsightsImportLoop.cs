using DataUtils;
using System;

namespace WebJob.AppInsightsImporter.Engine
{
    public static class AppInsightsImportLoop
    {
        /// <summary>
        /// Between normal cycles, drain only work already due at the last committed normal cursor. Each pass has
        /// its own scope and yields to the next normal cycle; it never advances or postpones that cycle's cursor.
        /// A one-shot runs exactly one normal cycle, including its ordinary bounded retry pass, and never waits.
        /// </summary>
        public static int Run(bool oneShot, Func<AppInsightsImportCycleResult> runNormal,
            Func<DateTime, DateTime, AppInsightsImportCycleResult> runRecovery, int pauseMinutes,
            Action<TimeSpan> wait, Func<bool> shutdownRequested, IClock clock)
        {
            AppInsightsImportCycleResult normal = null;
            return Run(oneShot, () =>
            {
                normal = runNormal();
                return normal?.Outcome ?? AppInsightsCycleOutcome.Failed;
            }, () =>
            {
                var nextNormal = clock.UtcNow.AddMinutes(Math.Max(1, pauseMinutes));
                var pass = normal;
                var failureDelayMinutes = 5;
                while (normal?.CursorAdvancedToUtc != null && pass != null && pass.SearchWork != SearchRecoveryWork.NoneDue && !shutdownRequested())
                {
                    // An unknown checkpoint is a reason to re-read it, never permission to reuse the last batch.
                    var backOff = pass.SearchRetryUnavailable || pass.SearchWork == SearchRecoveryWork.CheckpointUnknown;
                    var minutes = backOff ? failureDelayMinutes : 1;
                    if (clock.UtcNow.AddMinutes(minutes) >= nextNormal) break;
                    wait(TimeSpan.FromMinutes(minutes));
                    if (shutdownRequested() || clock.UtcNow >= nextNormal) break;
                    failureDelayMinutes = backOff ? Math.Min(60, minutes * 2) : 5;
                    pass = runRecovery(normal.CursorAdvancedToUtc.Value, nextNormal);
                }
                var remaining = nextNormal - clock.UtcNow;
                if (remaining > TimeSpan.Zero && !shutdownRequested()) wait(remaining);
            }, shutdownRequested);
        }

        /// <summary>A one-shot invocation reports the cycle's result; the continuous WebJob retries after its normal pause.</summary>
        public static int Run(bool oneShot, Func<AppInsightsCycleOutcome> runCycle, Action pause, Func<bool> shutdownRequested)
        {
            while (!shutdownRequested())
            {
                var outcome = runCycle();
                if (oneShot)
                {
                    return outcome == AppInsightsCycleOutcome.Failed ? HostRuntime.FatalExitCode : 0;
                }
                pause();
            }
            return 0;
        }
    }
}
