// All rights reserved.
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.

using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Configuration;
using System.Diagnostics;

namespace WebJob.AppInsightsImporter
{
    class Program
    {
        /// <summary>
        /// Application entry-point
        /// </summary>
        static void Main(string[] args)
        {
            // Figure out args
            const string ARG_SAVE_REST_RESPONSES = "-logApiResponses", ARG_READ_HITS_DAYS_BEFORE_FROM_APP_INSIGHTS = "-readHitsDaysBeforeToday";
            bool saveRestResponses = false, debugSwitchDetected = false;
            int argIdx = 0, daysBeforeReadOverride = 0;
            foreach (var arg in args)
            {
                if (arg.ToLower() == ARG_SAVE_REST_RESPONSES.ToLower())
                {
                    Console.WriteLine("DEBUG: Will save Application Insights responses to temp disk location.");
                    saveRestResponses = true;
                    debugSwitchDetected = true;
                }
                if (arg.ToLower() == ARG_READ_HITS_DAYS_BEFORE_FROM_APP_INSIGHTS.ToLower())
                {
                    if (args.Length >= argIdx + 2)
                    {
                        var nextArg = args[argIdx + 1];

                        if (int.TryParse(nextArg, out daysBeforeReadOverride) && daysBeforeReadOverride > 0)
                        {
                            Console.WriteLine($"DEBUG: Will request App Insights data from '{daysBeforeReadOverride}' days before today...");
                            debugSwitchDetected = true;
                        }
                    }
                }
                argIdx++;
            }

            // Enabled CTRL+C
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e) { BombOut(); };

            // Check configuration
            bool runAgain = true;

            var importJobSettingsString = ConfigurationManager.AppSettings.Get("ImportJobSettings");
            var importJobSettings = new ImportTaskSettings(importJobSettingsString);


            var config = new AppConfig();

            var logger = new AnalyticsLogger(config.AppInsightsConnectionString, "AppInsightsImporter");

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

            bool validConfig = ValidateAndPrintConfig(config, logger);
            if (validConfig)
            {
                Console.WriteLine();
                logger.LogInformation("Starting Application Insights import.");
            }
            else
            {
                logger.LogInformation("\nCheck settings & restart app.");
                runAgain = false;
            }

            // Should we even be running?
            if (!importJobSettings.WebTraffic)
            {
                logger.LogWarning("WARNING: Web-traffic import is disabled in app configuration. Stopping here until the app is restarted.");
                while (true)
                {
                    System.Threading.Thread.Sleep(int.MaxValue);
                }
            }

            // Stagger this WebJob's first cycle so it doesn't peak on the shared App Service plan at the
            // same time as the activity importer. One-off; later cycles use ImportCyclePauseMinutes.
#if !DEBUG
            if (runAgain && config.ImportStartStaggerMinutes > 0)
            {
                telemetry.LogInformation($"Staggering start by {config.ImportStartStaggerMinutes} min(s) to avoid simultaneous CPU peaks with the activity importer...");
                System.Threading.Thread.Sleep(config.ImportStartStaggerMinutes * 60 * 1000);
            }
#endif

            try
            {
                while (runAgain)
                {
                    var importCycleTimer = new JobTimer(logger, Process.GetCurrentProcess().ProcessName);
                    importCycleTimer.Start();

                    var importer = new Engine.AppInsightsImporter(config, logger);
                    if (daysBeforeReadOverride > 0)
                    {
                        importer.ImportAndSave(saveRestResponses, daysBeforeReadOverride).Wait();
                    }
                    else
                    {
                        importer.ImportAndSave(saveRestResponses, null).Wait();
                    }

#if DEBUG
                    runAgain = false; // Debug only runs once; release runs forever. 
#else
                    runAgain = !debugSwitchDetected;
#endif

                    // Output cycle stats
                    importCycleTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedImportCycle);

                    if (runAgain)
                    {
                        ConsoleApp.WebjobWait(logger, config.ImportCyclePauseMinutes);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.TrackException(ex);

                // ex is normally an AggregateException from the .Wait() above, whose Message is just
                // "One or more errors occurred." - useless on its own. Log the unwrapped inner-exception
                // chain and the full stack so the real failure is visible in the WebJob console & traces,
                // not only in the AppInsights 'exceptions' table.
                logger.LogError($"Got uncaught exception importing & saving data from Application Insights: {CommonExceptionHandler.GetErrorText(ex)}");
                logger.LogError($"Exception detail: {ex}");

#if DEBUG
                throw;
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

        }


        #region Console Methods

        /// <summary>
        /// Exit app
        /// </summary>
        private static void BombOut()
        {
            Console.WriteLine("CTRL+C Pressed...");
            Environment.Exit(0);
        }

        /// <summary>
        /// Output the config and check it's good.
        /// </summary>
        /// <returns>If the config is valid</returns>
        private static bool ValidateAndPrintConfig(AppConfig settings, ILogger logger)
        {
            ConsoleApp.PrintStartupAndLoggingConfig(settings.ConnectionStrings.DatabaseConnectionString, settings.BuildLabel, settings.UserGroupsFilter, logger);

            // Have config object test

            settings.ConnectionStrings.TestSQLSettings(logger);

            if (string.IsNullOrEmpty(settings.AppInsightsConnectionString))
            {
                logger.LogInformation("Critical: no Application Insights connection string found - can't continue. Run the latest installer again to reset configuration.");
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
            System.Threading.Thread.Sleep(PAUSE);
            return true;
        }

        #endregion
    }
}
