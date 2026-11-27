// All rights reserved.
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.

using Common.Entities;
using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserScope;
using DataUtils;
using DataUtils.Health;
using Microsoft.Extensions.Logging;
using System;
using System.Configuration;
using System.Diagnostics;

namespace WebJob.AppInsightsImporter
{
    class Program
    {
        /// <summary>
        /// DEBUG runs once and lets a cycle exception reach the debugger rather than reporting a failed outcome.
        /// </summary>
        private static readonly bool DebugBuild =
#if DEBUG
            true;
#else
            false;
#endif

        /// <summary>
        /// Application entry-point
        /// </summary>
        static int Main(string[] args)
        {
            HostRuntime.InstallFatalHandlers(ImporterHeartbeat.WebTrafficJob);

            // Figure out args. Anything not understood is refused rather than ignored, so a mistyped debug switch
            // cannot turn into the normal endless import.
            const string ARG_SAVE_REST_RESPONSES = "-logApiResponses", ARG_READ_HITS_DAYS_BEFORE_FROM_APP_INSIGHTS = "-readHitsDaysBeforeToday";
            var parsedArgs = StrictArguments.Parse(args, new[] { ARG_SAVE_REST_RESPONSES }, new[] { ARG_READ_HITS_DAYS_BEFORE_FROM_APP_INSIGHTS });
            int daysBeforeReadOverride = 0;
            string argsError = parsedArgs.Error;
            if (parsedArgs.IsValid && parsedArgs.Has(ARG_READ_HITS_DAYS_BEFORE_FROM_APP_INSIGHTS)
                && (!int.TryParse(parsedArgs.ValueOf(ARG_READ_HITS_DAYS_BEFORE_FROM_APP_INSIGHTS), out daysBeforeReadOverride) || daysBeforeReadOverride <= 0))
            {
                argsError = $"'{ARG_READ_HITS_DAYS_BEFORE_FROM_APP_INSIGHTS}' needs a whole number of days greater than zero.";
            }
            if (argsError != null)
            {
                Console.Error.WriteLine("ERROR: " + argsError);
                Console.Error.WriteLine($"Usage: [{ARG_SAVE_REST_RESPONSES}] [{ARG_READ_HITS_DAYS_BEFORE_FROM_APP_INSIGHTS} <days>]");
                return HostRuntime.UsageExitCode;
            }

            var saveRestResponses = parsedArgs.Has(ARG_SAVE_REST_RESPONSES);
            var debugSwitchDetected = saveRestResponses || daysBeforeReadOverride > 0;
            if (saveRestResponses)
            {
                // The dumps hold raw telemetry (page URLs, user names) but no credentials - see AppInsightsAPIClient
                Console.WriteLine("DEBUG: Will save Application Insights responses under " +
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AppInsightsImporter", "REST") + ". They hold raw telemetry " +
                    "(page URLs, user names): delete them as soon as you have finished, and within 7 days at most.");
            }
            if (daysBeforeReadOverride > 0) Console.WriteLine($"DEBUG: Will request App Insights data from '{daysBeforeReadOverride}' days before today...");

            var exitCode = HostRuntime.RunGuarded("AppInsightsImporter.Main", () => Run(saveRestResponses, debugSwitchDetected, daysBeforeReadOverride));
            AnalyticsLogger.DrainSharedTelemetry();
            return exitCode;
        }

        private static int Run(bool saveRestResponses, bool debugSwitchDetected, int daysBeforeReadOverride)
        {
            // Enabled CTRL+C
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e) { BombOut(); };

            var importJobSettingsString = ConfigurationManager.AppSettings.Get("ImportJobSettings");
            var importJobSettings = new ImportTaskSettings(importJobSettingsString);


            var config = new AppConfig();

            var logger = new AnalyticsLogger(config.AppInsightsConnectionString, ImporterHeartbeat.WebTrafficJob);
            // From here every telemetry item carries the build label, job name and run id, and an unhandled exception or a Kudu stop
            // request is reported through this logger.
            HostRuntime.Attach(logger, ImporterHeartbeat.WebTrafficJob, config.BuildLabel);

            bool oneShot = debugSwitchDetected || DebugBuild;
            if (!importJobSettings.WebTraffic)
            {
                logger.LogWarning("WARNING: Web-traffic import is disabled in app configuration. Stopping here until the app is restarted.");
                // Keep the continuous job idle until Kudu asks it to stop. An explicit one-shot has no work to do.
                while (!oneShot && !HostRuntime.Wait(TimeSpan.FromHours(1)))
                {
                }
                return 0;
            }

            // Apply the SQL-commit concurrency cap (aggressiveness preset) process-wide BEFORE any hit
            // commit runs - the hits merge is the heaviest SQL importer, so cap its staging fan-out to
            // ease the SQL Server CPU/DTU burst (issue #161 / PR #162).
            DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads = config.MaxSqlCommitConcurrency;

            // If no command line override was supplied, fall back to the config/app-setting value (if any).
            // The command line argument takes precedence over the config value.
            if (daysBeforeReadOverride == 0 && config.ReadHitsDaysBeforeToday.HasValue && config.ReadHitsDaysBeforeToday.Value > 0)
            {
                daysBeforeReadOverride = config.ReadHitsDaysBeforeToday.Value;
                Console.WriteLine($"Using ReadHitsDaysBeforeToday='{daysBeforeReadOverride}' from app configuration.");
            }

            if (!ValidateAndPrintConfig(config, logger, oneShot))
            {
                if (HostRuntime.ShutdownRequested)
                {
                    logger.LogInformation("Shutdown requested by host before the import started; exiting.");
                    return 0;
                }
                logger.LogCritical("Check settings & restart app. The Application Insights import is not running.");
                return HostRuntime.FatalExitCode;
            }
            Console.WriteLine();
            logger.LogInformation("Starting Application Insights import.");

            int exitCode;
            var stages = StageTracker.Process;
            using (var heartbeat = new DataUtils.Health.ImporterHeartbeat(logger, DataUtils.Health.ImporterHeartbeat.WebTrafficJob))
            using (var health = Common.Entities.Health.DefaultRuntimeHealth.Start(config, logger, ImporterHeartbeat.WebTrafficJob))
            try
            {
                // Heartbeat during the initial stagger too: a planned wait is not a dead process.
#if !DEBUG
                if (config.ImportStartStaggerMinutes > 0)
                {
                    logger.LogInformation($"Staggering start by {config.ImportStartStaggerMinutes} min(s) to avoid simultaneous CPU peaks with the activity importer...");
                    HostRuntime.Wait(TimeSpan.FromMinutes(config.ImportStartStaggerMinutes));
                }
#endif
                // UserGroupsFilter, shared across cycles: the resolved scope (and the last good one to fall back on), and
                // how far this process has read - see AppInsightsScanWatermark. Also which days failed, so each is retried
                // next cycle but only a few times - see AppInsightsDayFailureTracker - and the ingestion cursor's store,
                // durable in the runtime state table, or in memory (created once here) without Storage.
                var userScopeProvider = UserImportScopeProvider.CreateForGraph(config, logger);
                var scanWatermark = new Engine.AppInsightsScanWatermark();
                var dayFailures = new Engine.AppInsightsDayFailureTracker();
                var cursorStore = new Engine.KeyValueIngestionCursorStore(
                    StateStore.TryOpen(config, StatePartitions.AppInsightsImport, logger) ?? new InMemoryKeyValueStore(),
                    Engine.AppInsightsAPIClient.ParseConnectionStringValue(config.AppInsightsConnectionString, "ApplicationId"));
                logger.LogInformation($"App Insights ingestion cursor: {cursorStore.Description}" +
                    (StateStore.IsConfigured(config) ? "." : ". No Storage connection string, so after a restart the import starts again from the newest stored hit."));

                Engine.AppInsightsImportCycleResult RunCycle(DateTime? normalThroughUtc = null, DateTime? nextNormalUtc = null)
                {
                    // One operation id per cycle, so every trace, exception and event of a cycle can be pulled out together.
                    using (logger.BeginOperationScope(Guid.NewGuid().ToString("N")))
                    {
                        var importCycleTimer = new JobTimer(logger, Process.GetCurrentProcess().ProcessName);
                        importCycleTimer.Start();

                        // A transient failure must not restart the continuous web-job and lose the state above.
                        // In Release report the failed cycle; DEBUG still stops on the exception in the catch below.
                        Engine.AppInsightsImportCycleResult cycleResult = null;
                        Exception cycleException = null;
                        try
                        {
                            // A supplemental refresh belongs to its bounded pass, not to the next normal poll.
                            var userScope = normalThroughUtc.HasValue ? null :
                                userScopeProvider.GetScopeForCycleAsync(HostRuntime.ShutdownToken).GetAwaiter().GetResult();
                            HostRuntime.ThrowIfShutdownRequested();
                            var importer = new Engine.AppInsightsImporter(config, logger, userScope: userScope, scanWatermark: scanWatermark,
                                dayFailureTracker: dayFailures, cursorStore: cursorStore);
                            int? readDaysBefore = daysBeforeReadOverride > 0 ? daysBeforeReadOverride : (int?)null;
                            var stage = stages.Begin(logger, normalThroughUtc.HasValue ? "AppInsightsSearchRecovery" : "AppInsightsImport");
                            try
                            {
                                cycleResult = normalThroughUtc.HasValue
                                    ? importer.RecoverPendingSearchesAsync(normalThroughUtc.Value, nextNormalUtc.Value,
                                        HostRuntime.ShutdownToken, userScopeProvider.GetScopeForCycleAsync).GetAwaiter().GetResult()
                                    : importer.ImportAndSave(saveRestResponses, readDaysBefore, HostRuntime.ShutdownToken).GetAwaiter().GetResult();
                                HostRuntime.ThrowIfShutdownRequested();
                                if (cycleResult.Outcome == Engine.AppInsightsCycleOutcome.Failed)
                                {
                                    stage.Failed(reason: string.Join(",", cycleResult.Reasons));
                                }
                                else
                                {
                                    stage.Succeeded();
                                }
                            }
                            catch (OperationCanceledException) when (HostRuntime.ShutdownRequested)
                            {
                                stage.Skipped("HostShutdown");
                                throw new ShutdownRequestedException();
                            }
                            catch (Exception ex)
                            {
                                stage.Failed(ex);
                                throw;
                            }
                        }
                        catch (Exception ex) when (!DebugBuild && !(ex is ShutdownRequestedException))
                        {
                            // One telemetry owner: the exception is tracked here, once, with its chain in the message and its stack on stderr.
                            cycleResult = null;
                            cycleException = ex;
                            var retry = oneShot ? string.Empty : " Trying again after the normal pause.";
                            logger.LogError($"Import cycle failed: {CommonExceptionHandler.GetErrorText(ex)}.{retry}", ex);
                        }

                        // A search-only pass reports its own outcome, but cannot certify normal ingestion for liveness.
                        var outcome = Engine.AppInsightsImportTelemetry.TrackCycle(logger, cycleResult, importCycleTimer.Elapsed, cycleException);
                        if (!normalThroughUtc.HasValue && outcome != Engine.AppInsightsCycleOutcome.Failed)
                        {
                            heartbeat.CycleCompleted(importCycleTimer.Elapsed);
                            importCycleTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedImportCycle);
                        }
                        return cycleResult;
                    }
                }

                exitCode = Engine.AppInsightsImportLoop.Run(oneShot, () => RunCycle(), (through, deadline) => RunCycle(through, deadline),
                    config.ImportCyclePauseMinutes, delay =>
                    {
                        logger.LogInformation($"Waiting {delay.TotalMinutes:0.##} min(s)...");
                        HostRuntime.Wait(delay);
                    }, () => HostRuntime.ShutdownRequested, SystemClock.Instance);
                if (HostRuntime.ShutdownRequested)
                {
                    logger.LogInformation("Shutdown requested by host: the import loop has stopped; flushing telemetry and exiting.");
                }
            }
            catch (Exception ex) when (!(ex is ShutdownRequestedException))
            {
                // The exception is normally unwrapped from the AggregateException of a blocking wait; the message carries the
                // whole inner-exception chain and the console gets the full stack, as well as the 'exceptions' table.
                logger.LogCritical($"Got uncaught exception importing & saving data from Application Insights: {CommonExceptionHandler.GetErrorText(ex)}", ex);

#if DEBUG
                throw;
#else
                return HostRuntime.FatalExitCode;
#endif
            }


            if (debugSwitchDetected)
            {
                Console.WriteLine("Debug switch detected. Exiting from loop.");
            }
#if DEBUG
            Console.WriteLine("DEBUG MODE: All done. Press any key to continue.");
            Console.ReadKey();
#endif
            return exitCode;
        }


        #region Console Methods

        /// <summary>
        /// Exit app
        /// </summary>
        private static void BombOut()
        {
            Console.WriteLine("CTRL+C Pressed...");
            HostRuntime.Exit(0);
        }

        /// <summary>
        /// Only a continuous WebJob waits for SQL to recover. An explicit one-shot must report failure to its caller.
        /// </summary>
        /// <returns>If the config is valid</returns>
        private static bool ValidateAndPrintConfig(AppConfig settings, AnalyticsLogger logger, bool oneShot)
        {
            ConsoleApp.PrintStartupAndLoggingConfig(settings.ConnectionStrings.DatabaseConnectionString, settings.BuildLabel, settings.UserGroupsFilter, logger);

            if (string.IsNullOrEmpty(settings.AppInsightsConnectionString))
            {
                logger.LogCritical("No Application Insights connection string found - can't continue. Run the latest installer again to reset configuration.");
                return false;
            }

            if (HostRuntime.ShutdownRequested) return false;
            var reachable = oneShot
                ? settings.ConnectionStrings.TestWebTrafficSqlOnceAsync(logger, HostRuntime.ShutdownToken).GetAwaiter().GetResult()
                : StartupSqlCheck.WaitUntilReachable(ct => settings.ConnectionStrings.TestWebTrafficSqlOnceAsync(logger, ct), logger,
                    ImporterHeartbeat.WebTrafficJob, HostRuntime.ShutdownToken);
            if (!reachable)
            {
                return false;
            }

            int PAUSE = 0;
#if DEBUG
            PAUSE = 2000;
#else
            PAUSE = 20000;
#endif
            Console.WriteLine("\nPausing {0} seconds in case any of these configuration values are wrong (CTRL+C to abort)...",
                (PAUSE / 1000));
            HostRuntime.Wait(TimeSpan.FromMilliseconds(PAUSE));
            return !HostRuntime.ShutdownRequested;
        }

        #endregion
    }
}
