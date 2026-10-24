using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace DataUtils
{
    /// <summary>
    /// The SQL check a host runs before it starts importing. A failed check is not the end of the process: exiting
    /// would make Kudu restart it straight away into the same failure, a crash loop that fills the log and the
    /// deployment's failure counters. Instead the host says so at Critical severity, backs off and tries again, until
    /// SQL answers or the host is asked to stop.
    /// </summary>
    public static class StartupSqlCheck
    {
        /// <summary>The wait before each retry; the last entry repeats.</summary>
        internal static readonly TimeSpan[] DefaultBackoff =
        {
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)
        };

        /// <summary>
        /// Runs <paramref name="probe"/> until it passes.
        /// </summary>
        /// <param name="probe">Returns true when SQL is reachable and the schema is there. It logs why it failed.</param>
        /// <param name="wait">Waits for the delay, returning true when the host was asked to stop meanwhile. Defaults to <see cref="HostRuntime.Wait"/>.</param>
        /// <returns>True when the probe passed; false when the host was asked to stop first.</returns>
        public static bool WaitUntilReachable(Func<bool> probe, ILogger logger, string jobName,
            Func<TimeSpan, bool> wait = null, IReadOnlyList<TimeSpan> backoff = null)
        {
            if (probe == null) throw new ArgumentNullException(nameof(probe));
            wait = wait ?? HostRuntime.Wait;
            var delays = backoff ?? DefaultBackoff;
            var clock = Stopwatch.StartNew();

            for (var attempt = 1; ; attempt++)
            {
                if (probe())
                {
                    if (attempt > 1)
                    {
                        logger.LogInformation($"STARTUP-SQL-CHECK Recovered: job={jobName}; attempt={attempt.ToString(CultureInfo.InvariantCulture)}; " +
                            $"waitedMs={clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}.");
                    }
                    return true;
                }

                var delay = delays[Math.Min(attempt - 1, delays.Count - 1)];
                logger.LogCritical($"STARTUP-SQL-CHECK Failed: job={jobName}; attempt={attempt.ToString(CultureInfo.InvariantCulture)}; " +
                    $"the importer is not running until SQL is reachable and upgraded; retrying in {delay.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s " +
                    $"(waitedMs={clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)}).");

                if (wait(delay))
                {
                    logger.LogWarning($"STARTUP-SQL-CHECK Abandoned: job={jobName}; shutdown requested by host while waiting for SQL.");
                    return false;
                }
            }
        }
    }
}
