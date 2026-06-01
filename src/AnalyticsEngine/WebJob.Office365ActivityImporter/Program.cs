// All rights reserved.
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.

#region Usings
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Installer;
using DataUtils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI; // for AuditTraceConfig
#endregion

namespace WebJob.Office365ActivityImporter
{
    /// <summary>
    /// Imports Activity API & Graph API data
    /// </summary>
    class Program
    {
        // Holder for trace settings
        internal static string TraceAuditEmail = null;
        internal static string TraceAuditDirectory = null;

        /// <summary>
        /// Imports data from the Graph & 0365 Activity APIs.
        /// 
        /// Startup params (from ActivityImportConstants):
        /// --webhook XYZ - override URL to create webhook subscriptions for
        /// --callId XYZ - get & save a call from Graph
        /// </summary>
        static void Main(string[] args) => MainAsync(args).GetAwaiter().GetResult();
        static async Task MainAsync(string[] args)
        {
            int argIdx = 0;

            // Get settings
            AppConfig configuredSettings = null;
            try
            {
                configuredSettings = new AppConfig();
            }
            catch (FormatException)
            {
                Console.WriteLine("Error converting configurations values to int/guid/timespan. Please verify App Settings.");
                ConsoleApp.BombOut(true);
            }

#if DEBUG
            // Insert a test config for local debugging
            using (var db = new AnalyticsEntitiesContext())
            {
                if (db.ConfigStates.Count() == 0)
                {
                    var debugCfg = new BaseSolutionInstallConfig
                    {
                        AllowTelemetry = true,
                    };
                    var debugCfgState = new ConfigState
                    {
                        ConfigJson = JsonConvert.SerializeObject(debugCfg),
                        DateApplied = DateTime.Now,
                        InstalledByUser = Environment.UserName
                    };

                    db.ConfigStates.Add(debugCfgState);
                    await db.SaveChangesAsync();
                    Console.WriteLine("DEBUG test config added to allow telemetry tests");
                }
            }
#endif

            // Create new telemetry client with AppInsights key
            var telemetry = new AnalyticsLogger(configuredSettings.AppInsightsConnectionString, "Office365ActivityImporter");

            // Verify config
            var webhookUrl = configuredSettings.WebAppURL + "api/CallRecordWebhook";
            Uri webHookUrl = null;
            if (StringUtils.IsValidAbsoluteUrl(webhookUrl))
            {
                webHookUrl = new Uri(webhookUrl);
            }

            // Look for start-up args to override execution
            foreach (var arg in args)
            {
                if (arg.ToLower() == ActivityImportConstants.PARAM_WEBHOOK_OVERRIDE)
                {
                    // Override webhook config to param
                    // ngrok http -host-header=localhost 55573
                    if (args.Length >= argIdx + 2)
                    {
                        var nextArg = args[argIdx + 1];
                        if (StringUtils.IsValidAbsoluteUrl(nextArg))
                        {
                            webHookUrl = new Uri(nextArg);
                            Console.WriteLine($"DEBUG: Using custom webhook '{webHookUrl}' URL from args");
                        }
                    }
                }
                else if (arg.ToLower() == ActivityImportConstants.PARAM_CALL_ID.ToLower())
                {
                    if (args.Length >= argIdx + 2)
                    {
                        // Import a single call ID
                        telemetry.LogInformation($"Detected '{ActivityImportConstants.PARAM_CALL_ID}' parameter value. Importing single call-record from Graph and exiting.");
                        var nextArg = args[argIdx + 1];

                        var auth = new GraphAppIndentityOAuthContext(telemetry, configuredSettings.ClientID, configuredSettings.TenantGUID.ToString(), configuredSettings.ClientSecret, configuredSettings.KeyVaultUrl, configuredSettings.UseClientCertificate);

                        var newCall = await Engine.Entities.Serialisation.CallRecordDTO.SaveNewCallToDB(
                            nextArg,
                            new Engine.Graph.ManualGraphCallClient(auth, telemetry),
                            auth.Creds, telemetry, configuredSettings.TenantGUID.ToString());

                        ConsoleApp.BombOut(false);
                    }
                }
                else if (arg.ToLower() == ActivityImportConstants.PARAM_TRACE_AUDIT_EMAIL.ToLower())
                {
                    if (args.Length >= argIdx + 2)
                    {
                        TraceAuditEmail = args[argIdx + 1];
                        AuditTraceConfig.TraceEmail = TraceAuditEmail;
                        Console.WriteLine($"TRACE: Will capture audit imports containing email '{TraceAuditEmail}'.");
                    }
                }
                else if (arg.ToLower() == ActivityImportConstants.PARAM_TRACE_AUDIT_DIR.ToLower())
                {
                    if (args.Length >= argIdx + 2)
                    {
                        TraceAuditDirectory = args[argIdx + 1];
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(TraceAuditDirectory))
                            {
                                System.IO.Directory.CreateDirectory(TraceAuditDirectory);
                                AuditTraceConfig.TraceDirectory = TraceAuditDirectory;
                                Console.WriteLine($"TRACE: Will save matching audit import files to '{TraceAuditDirectory}'.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"TRACE: Failed to create trace directory '{TraceAuditDirectory}': {ex.Message}");
                            TraceAuditDirectory = null;
                            AuditTraceConfig.TraceDirectory = null;
                        }
                    }
                }
                argIdx++;
            }

            // Output program
            PrintStartupDetails(configuredSettings, telemetry);

            // Test DB
            TestDB(telemetry);

            // Loop forever?
            var runAgain = true;

            // Run app
            while (runAgain)
            {
                var importCycleTimer = new JobTimer(telemetry, Process.GetCurrentProcess().ProcessName);
                importCycleTimer.Start();
                var tasks = new ProgramTasks(telemetry, configuredSettings);

                // Start listening for SB messages & register notifications web-hook with Graph 
                if (webHookUrl != null && configuredSettings.ImportJobSettings.Calls)
                {
                    if (string.IsNullOrWhiteSpace(configuredSettings.ConnectionStrings.ServiceBusConnectionString))
                    {
                        telemetry.LogCritical("Teams calls import is enabled but Service Bus is not configured. Skipping Call Queue import & webhook validation. Re-run the installer with Service Bus enabled, or disable the Calls import.");
                    }
                    else
                    {
                        try
                        {
                            await tasks.ProcessCallQueueAndWebhook(webHookUrl);
                        }
                        catch (Exception ex)
                        {
                            telemetry.TrackException(ex);
                            telemetry.LogCritical($"Got exception on {nameof(ProgramTasks.ProcessCallQueueAndWebhook)}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    telemetry.LogInformation("Skipping Call Queue import & webhook validation.");
                }

                try
                {
                    // Get Teams & user data
                    await tasks.GetGraphTeamsAndUserData();
                }
                catch (Exception ex)
                {
                    telemetry.TrackException(ex);
                    telemetry.LogCritical($"Got exception on {nameof(ProgramTasks.GetGraphTeamsAndUserData)}: {ex.Message}");
#if DEBUG
                    throw;
#endif
                }

                // Activity import
                if (configuredSettings.ImportJobSettings.ActivityLog)
                {
#if !DEBUG
                    try
                    {
#endif
                        await tasks.DownloadActivityData();
#if !DEBUG
                    }
                    catch (Exception ex)
                    {
                        telemetry.TrackException(ex);
                        Console.WriteLine($"Got exception on {nameof(ProgramTasks.DownloadActivityData)}: {ex.Message}");
                    }
#endif
                }
                else
                {
                    telemetry.LogInformation("Skipping Activity API import.");
                }

#if DEBUG
                runAgain = false; // Debug only runs once; release runs forever. 
#endif

                // Output cycle stats
                importCycleTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedImportCycle);


                if (runAgain)
                {
                    ConsoleApp.WebjobWait(telemetry);
                }
            } // Go around again?

            ConsoleApp.BombOut(false);
        }


        /// <summary>
        /// Tests the SQL DB configured. Bombs out if a problem
        /// </summary>
        private static void TestDB(ILogger debugTracer)
        {
            debugTracer.LogInformation("Testing SQL configuration...");

            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                try
                {
                    int count = (from allDownloads in db.AuditEventsCommon
                                 select allDownloads).Count();
                    debugTracer.LogInformation($"Found {count.ToString("n0")} events in table already. Test passed!");
                }
                catch (System.Data.SqlClient.SqlException ex)
                {
                    debugTracer.LogError(ex, $"Got a SQL error: {ex.Message}");
                    ConsoleApp.BombOut(true);
                }
            }
        }

        /// <summary>
        /// Confirm and validate settings
        /// </summary>
        private static void PrintStartupDetails(AppConfig settings, ILogger telemetry)
        {
            ConsoleApp.PrintStartupAndLoggingConfig(settings.ConnectionStrings.DatabaseConnectionString, settings.BuildLabel, settings.UserGroupsFilter, telemetry);

            var efConnectionString = ConfigurationManager.ConnectionStrings["SPOInsightsEntities"].ConnectionString;
            var sqlConnectionInfo = new System.Data.SqlClient.SqlConnectionStringBuilder(efConnectionString);

            telemetry.LogInformation("\nConfigured values:");

            telemetry.LogInformation($"Destination SQL Server='{sqlConnectionInfo.DataSource}', DB='{sqlConnectionInfo.InitialCatalog}'.");
            telemetry.LogInformation($"Azure AD tenant='{settings.TenantDomain}, client ID='{settings.ClientID}'.");
            telemetry.LogInformation($"Days back to check for events from Activity API='{settings.DaysBeforeNowToDownload}'.");

            // Print & verify O365 workloads to import
            var validWorkloadsConfig = false;
            var workloadsConfig = settings.ContentTypesString;
            if (!string.IsNullOrWhiteSpace(workloadsConfig))
            {
                var workloadsInConfig = workloadsConfig.Split(";".ToCharArray());
                if (workloadsInConfig.Length > 0)
                {
                    validWorkloadsConfig = true;
                    telemetry.LogInformation("\nConfigured workloads to import:");
                    foreach (var workload in workloadsInConfig)
                    {
                        telemetry.LogInformation($"+{workload}");
                    }
                    Console.WriteLine();
                }
            }
            if (!validWorkloadsConfig)
            {
                telemetry.LogError("CONFIG ERROR: No Office 365 workloads found in configuration key 'ContentTypesListAsString'!");
                ConsoleApp.BombOut(true);
            }
        }
    }
}
