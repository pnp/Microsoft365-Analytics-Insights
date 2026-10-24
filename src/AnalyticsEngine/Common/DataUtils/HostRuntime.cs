using System;
using System.Threading.Tasks;

namespace DataUtils
{
    /// <summary>
    /// What every importer host shares for the way a process ends: a fatal boundary that reports an unhandled
    /// exception to App Insights and the console before the process dies, a watcher for Kudu's shutdown request,
    /// and one bounded drain that every exit path goes through so the last minute of telemetry is not lost.
    /// </summary>
    public static class HostRuntime
    {
        /// <summary>Exit code of a process that stopped on an unhandled exception.</summary>
        public const int FatalExitCode = 1;

        /// <summary>Exit code of a process whose command line was refused.</summary>
        public const int UsageExitCode = 2;

        private static readonly object Gate = new object();
        private static AnalyticsLogger _logger;
        private static string _jobName = "Importer";
        private static bool _handlersInstalled;
        private static ShutdownWatcher _shutdown;

        public static bool ShutdownRequested => _shutdown?.IsRequested == true;

        /// <summary>
        /// Registers the process-wide handlers. Call first thing in <c>Main</c>, before configuration is read, so
        /// that even a failure while starting is reported.
        /// </summary>
        public static void InstallFatalHandlers(string jobName)
        {
            lock (Gate)
            {
                _jobName = jobName;
                if (_handlersInstalled) return;
                _handlersInstalled = true;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                ReportFatal(e.ExceptionObject as Exception ?? new InvalidOperationException("Unhandled non-exception object: " + e.ExceptionObject), "AppDomain.UnhandledException");
                if (e.IsTerminating) Exit(FatalExitCode);
            };

            // A faulted task nobody awaited is not fatal on .NET Framework 4.5+, but it is a defect that would
            // otherwise leave no trace at all.
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try
                {
                    var logger = _logger;
                    if (logger != null)
                    {
                        logger.LogError($"Unobserved task exception in {_jobName}: {ExceptionDescriber.Chain(e.Exception)}", e.Exception);
                    }
                    else
                    {
                        ConsoleLog.Write(Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Error, null,
                            $"Unobserved task exception in {_jobName}.", e.Exception);
                    }
                }
                catch (Exception)
                {
                    // Reporting must not throw on the finalizer thread.
                }
                e.SetObserved();
            };
        }

        /// <summary>
        /// Stamps this process's identity on all telemetry, attaches the logger the fatal boundary reports through and
        /// starts watching for Kudu's shutdown request.
        /// </summary>
        public static void Attach(AnalyticsLogger logger, string jobName, string buildLabel)
        {
            ProcessIdentity.Configure(jobName, buildLabel);
            lock (Gate)
            {
                _logger = logger;
                _jobName = jobName;
                if (_shutdown == null)
                {
                    _shutdown = ShutdownWatcher.ForEnvironment(OnShutdownRequested);
                }
            }
        }

        private static void OnShutdownRequested()
        {
            // The process may be killed when Kudu's stop wait runs out, and the main loop may be hours into a phase,
            // so send what is buffered now instead of waiting for the loop to notice.
            _logger?.LogWarning($"Shutdown requested by host ({_jobName}); not starting new work. Flushing telemetry.");
            AnalyticsLogger.DrainSharedTelemetry();
        }

        /// <summary>Sleeps for up to <paramref name="timeout"/>, waking early on a shutdown request. True when shutdown was requested.</summary>
        public static bool Wait(TimeSpan timeout)
        {
            var shutdown = _shutdown;
            if (shutdown == null)
            {
                System.Threading.Thread.Sleep(timeout);
                return false;
            }
            return shutdown.Wait(timeout);
        }

        /// <summary>Throws <see cref="ShutdownRequestedException"/> when the host has asked the process to stop.</summary>
        public static void ThrowIfShutdownRequested()
        {
            if (ShutdownRequested) throw new ShutdownRequestedException();
        }

        /// <summary>
        /// Runs a host's body inside the fatal boundary: an exception that escapes is reported (App Insights exception,
        /// Critical trace, full chain and stack on stderr), telemetry is drained, and the exit code is non-zero.
        /// </summary>
        /// <returns>0, or <see cref="FatalExitCode"/> when the body failed.</returns>
        public static int RunGuarded(string source, Func<Task> body)
        {
            try
            {
                body().GetAwaiter().GetResult();
                return 0;
            }
            catch (ShutdownRequestedException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                ReportFatal(ex, source);
                return FatalExitCode;
            }
        }

        /// <summary>
        /// Logs an exception that is ending the process: once to App Insights as an exception with its chain and SQL
        /// error number, as a Critical trace, and to stderr with the full stack; then drains telemetry. Never throws.
        /// </summary>
        public static void ReportFatal(Exception exception, string source)
        {
            try
            {
                var message = FatalMessage(_jobName, source, exception);
                var logger = _logger;
                if (logger != null)
                {
                    logger.LogCritical(message, exception);
                }
                else
                {
                    ConsoleLog.Write(Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Critical, null, message, exception);
                }
            }
            catch (Exception)
            {
                // The process is already failing; the drain below still runs.
            }
            AnalyticsLogger.DrainSharedTelemetry();
        }

        /// <summary>The one-line description of a fatal error: which job, where, and the whole exception chain.</summary>
        public static string FatalMessage(string jobName, string source, Exception exception) =>
            $"FATAL {jobName}: unhandled exception in {source}; the process is stopping. {ExceptionDescriber.Chain(exception)}";

        /// <summary>Drains telemetry (bounded), then ends the process. Every deliberate exit goes through here.</summary>
        public static void Exit(int exitCode)
        {
            AnalyticsLogger.DrainSharedTelemetry();
            Environment.Exit(exitCode);
        }
    }

    /// <summary>Thrown between phases of a cycle once the host has asked the process to stop, to unwind to the loop.</summary>
    public sealed class ShutdownRequestedException : OperationCanceledException
    {
        public ShutdownRequestedException() : base("Shutdown requested by host.")
        {
        }
    }
}
