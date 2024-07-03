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

            var telemetry = new AnalyticsLogger(config.AppInsightsConnectionString, "AppInsightsImporter");

            bool validConfig = ValidateAndPrintConfig(config, telemetry);
            if (validConfig)
            {
                Console.WriteLine();
                telemetry.LogInformation("Starting Application Insights import.");
            }
            else
            {
                telemetry.LogInformation("\nCheck settings & restart app.");
                runAgain = false;
            }

            // Should we even be running?
            if (!importJobSettings.WebTraffic)
            {
                telemetry.LogWarning("WARNING: Web-traffic import is disabled in app configuration. Stopping here until the app is restarted.");
                while (true)
                {
                    System.Threading.Thread.Sleep(int.MaxValue);
                }
            }

            try
            {
                while (runAgain)
                {
                    var importCycleTimer = new JobTimer(telemetry, Process.GetCurrentProcess().ProcessName);
                    importCycleTimer.Start();

                    var importer = new Engine.AppInsightsImporter(config, telemetry);
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
                        ConsoleApp.WebjobWait(telemetry);
                    }
                }
            }
            catch (Exception ex)
            {
                telemetry.TrackException(ex);
                telemetry.LogError($"Got uncaught exception importing & saving data from Application Insights: {ex.Message}");

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
        private static bool ValidateAndPrintConfig(AppConfig config, ILogger telemetry)
        {
            ConsoleApp.PrintStartupAndLoggingConfig(telemetry);

            // Have config object test

            var accountName = StringUtils.FindValueForProp(config.ConnectionStrings.DatabaseConnectionString, "AccountName");

            string efConnectionString = System.Configuration.ConfigurationManager.ConnectionStrings["SPOInsightsEntities"].ConnectionString;
            var sqlConnectionInfo = new System.Data.SqlClient.SqlConnectionStringBuilder(efConnectionString);

            telemetry.LogInformation("Configured values:");
            //pIIConfig.PrintConfig(telemetry);     // PII anonisation is broken for the time being in this project, but nobody uses it anyway...

            telemetry.LogInformation($"Destination SQL Server configuration: \ndata source='{sqlConnectionInfo.DataSource}', initial catalog='{sqlConnectionInfo.InitialCatalog}'");

            config.ConnectionStrings.TestSQLSettings(telemetry);

            if (string.IsNullOrEmpty(config.AppInsightsApiKey))
            {
                telemetry.LogInformation("Critical: no Application Insights API key found - can't continue. Run the latest installer again to reset configuration.");
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
