// All rights reserved.
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.

#region Usings
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Installer;
using Common.Entities.Redis;
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
using WebJob.Office365ActivityImporter.Engine.Graph.Email;
using WebJob.Office365ActivityImporter.Engine.StatsUploader;
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
            var logger = new AnalyticsLogger(configuredSettings.AppInsightsConnectionString, "Office365ActivityImporter");

            // Apply the SQL-commit concurrency cap (aggressiveness preset) process-wide BEFORE any
            // InsertBatch import runs, so commits don't burst the shared SQL tier at the legacy 20
            // threads (issue #161 / PR #162 - easing SQL Server CPU/DTU spikes on commit).
            DataUtils.Sql.Inserts.InsertBatchConcurrency.MaxConcurrentThreads = configuredSettings.MaxSqlCommitConcurrency;

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
                        logger.LogInformation($"Detected '{ActivityImportConstants.PARAM_CALL_ID}' parameter value. Importing single call-record from Graph and exiting.");
                        var nextArg = args[argIdx + 1];

                        var auth = new GraphAppIndentityOAuthContext(logger, configuredSettings.ClientID, configuredSettings.TenantGUID.ToString(), configuredSettings.ClientSecret, configuredSettings.KeyVaultUrl, configuredSettings.UseClientCertificate);

                        var newCall = await Engine.Entities.Serialisation.CallRecordDTO.SaveNewCallToDB(
                            nextArg,
                            new Engine.Graph.ManualGraphCallClient(auth, logger),
                            auth.Creds, logger, configuredSettings.TenantGUID.ToString());

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
            PrintStartupDetails(configuredSettings, logger);

            // Test DB
            TestDB(logger);

            // Loop forever?
            var runAgain = true;

            // Stats-upload "last uploaded" tracker. Instantiated ONCE here, outside the import
            // cycle loop, because the in-memory fallback otherwise loses its last-upload timestamp
            // every cycle (defeating the 1-day MIN_WAIT throttle on UsageStatsManager and hammering
            // the stats endpoint). Both loader implementations are cheap to construct and hold
            // their own connection state, so creating them once is also fine for the Redis path.
            IStatsDatesLoader statsDatesLoader;
            if (!string.IsNullOrEmpty(configuredSettings.ConnectionStrings.RedisConnectionString))
            {
                statsDatesLoader = new RedisStatsDatesLoader(configuredSettings);
            }
            else
            {
                logger.LogInformation("No Redis connection string configured - using in-memory throttle for stats upload (the MIN_WAIT window resets each time the WebJob process restarts).");
                statsDatesLoader = new InMemoryStatsDatesLoader();
            }

            // Activity/usage reports also run at most once a day. Like the stats throttle above, this needs a
            // store that survives across cycles - Redis when configured, otherwise an in-memory fallback built
            // ONCE here (a fresh per-cycle instance would always look "never imported" and re-run the multi-hour
            // usage-report phase every cycle, even without Redis).
            ISingleDateStore activityReportsLastImportedStore = ActivityReportsLastImportedStoreFactory.Create(configuredSettings, logger);

            // Cadence-gate store for the non-fresh Graph imports (user metadata, user apps, Teams).
            // Created ONCE here, outside the cycle loop, so the in-memory fallback retains its "last run"
            // timestamps across cycles (mirrors statsDatesLoader above). Redis when configured (gate also
            // survives WebJob restarts); otherwise in-memory (resets on restart). Reads/writes are
            // fail-open so a Redis blip never skips an import.
            IImportLastRunStore graphLastRunStore;
            if (!string.IsNullOrEmpty(configuredSettings.ConnectionStrings.RedisConnectionString))
            {
                try
                {
                    var cache = CacheConnectionManager.GetConnectionManager(
                        configuredSettings.ConnectionStrings.RedisConnectionString,
                        tenantId: configuredSettings.TenantGUID.ToString(),
                        clientId: configuredSettings.ClientID,
                        clientSecret: configuredSettings.ClientSecret);
                    graphLastRunStore = new RedisImportLastRunStore(cache, logger);
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Could not connect to Redis for import cadence gating ({ex.Message}); using in-memory fallback (the gate resets when the WebJob restarts).");
                    graphLastRunStore = new InMemoryImportLastRunStore();
                }
            }
            else
            {
                logger.LogInformation("No Redis configured - using in-memory import cadence gating (resets when the WebJob restarts).");
                graphLastRunStore = new InMemoryImportLastRunStore();
            }

            // Negative cache of users with no Exchange mailbox, for the sent-emails import. Also created
            // ONCE here so the in-memory fallback survives across cycles - a per-cycle instance would
            // always start empty and re-check (and 404 on) every mailbox-less user every 10 minutes.
            ISentEmailMailboxSkipList sentEmailMailboxSkipList;
            if (!string.IsNullOrEmpty(configuredSettings.ConnectionStrings.RedisConnectionString))
            {
                sentEmailMailboxSkipList = new RedisSentEmailMailboxSkipList(
                    configuredSettings.ConnectionStrings.RedisConnectionString,
                    logger,
                    tenantId: configuredSettings.TenantGUID.ToString(),
                    clientId: configuredSettings.ClientID,
                    clientSecret: configuredSettings.ClientSecret);
            }
            else
            {
                sentEmailMailboxSkipList = new InMemorySentEmailMailboxSkipList();
            }

            // Run app
            while (runAgain)
            {
                var importCycleTimer = new JobTimer(logger, Process.GetCurrentProcess().ProcessName);
                importCycleTimer.Start();
                var tasks = new ProgramTasks(logger, configuredSettings, activityReportsLastImportedStore, graphLastRunStore, sentEmailMailboxSkipList);

                // Start listening for SB messages & register notifications web-hook with Graph 
                if (webHookUrl != null && configuredSettings.ImportJobSettings.Calls)
                {
                    if (string.IsNullOrWhiteSpace(configuredSettings.ConnectionStrings.ServiceBusConnectionString))
                    {
                        logger.LogCritical("Teams calls import is enabled but Service Bus is not configured. Skipping Call Queue import & webhook validation. Re-run the installer with Service Bus enabled, or disable the Calls import.");
                    }
                    else
                    {
                        try
                        {
                            await tasks.ProcessCallQueueAndWebhook(webHookUrl);
                        }
                        catch (Exception ex)
                        {
                            logger.TrackException(ex);
                            logger.LogCritical($"Got exception on {nameof(ProgramTasks.ProcessCallQueueAndWebhook)}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    logger.LogInformation("Skipping Call Queue import & webhook validation.");
                }

                try
                {
                    // Get Teams & user data
                    await tasks.GetGraphTeamsAndUserData();
                }
                catch (Exception ex)
                {
                    logger.TrackException(ex);
                    logger.LogCritical($"Got exception on {nameof(ProgramTasks.GetGraphTeamsAndUserData)}: {ex.Message}");
#if DEBUG
                    throw;
#endif
                }

                // Activity import (Office 365 Management Activity API). Runs when SharePoint audit
                // (ActivityLog) and/or Copilot interactions (delivered via Audit.General) are enabled.
                if (configuredSettings.ImportJobSettings.ActivityLog || configuredSettings.ImportJobSettings.Copilot)
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
                        logger.TrackException(ex);
                        // Also emit a trace (TrackException goes to a separate telemetry stream): a save/merge
                        // failure that escapes here means the whole cycle was aborted. Batch-level failures are
                        // now isolated in LoadReportsAndSave, so reaching here indicates a non-batch failure.
                        logger.LogError($"Audit import cycle ABORTED by an unhandled exception in {nameof(ProgramTasks.DownloadActivityData)}: {ex.Message}. The cycle will restart on the next timer interval.");
                    }
#endif
                }
                else
                {
                    logger.LogInformation("Skipping Activity API import.");
                }

                // Repair any Copilot interactions left without their denormalised user_id / time_stamp
                // (migration DenormaliseCopilotChatUserAndTime). Such rows are INVISIBLE to every Copilot
                // report, because they all filter on copilot_chats.time_stamp - so this must be reached on
                // every cycle, not only successful ones. Deliberately placed OUTSIDE the try/catch above and
                // outside DownloadActivityData: an aborted cycle (e.g. a persistent Activity API auth
                // failure), a cycle that downloaded nothing, and a cycle skipped for having no organisation
                // URLs configured must all still heal the database, since the repair only needs SQL.
                // Costs 4 logical reads when there is nothing to do, and never throws.
                if (configuredSettings.ImportJobSettings.ActivityLog || configuredSettings.ImportJobSettings.Copilot)
                {
                    await ActivityImporter.Engine.ActivityAPI.Copilot.CopilotAuditEventManager
                        .RepairDenormalisedColumnsAsync(configuredSettings.ConnectionStrings.DatabaseConnectionString, logger);
                }

#if DEBUG
                runAgain = false; // Debug only runs once; release runs forever. 
#endif

                // Output cycle stats
                importCycleTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedImportCycle);

                // Upload latest stats if not done recently. Re-enabled in this build after the
                // Feb-2026 deprecation (commit 3485bd2) — the server endpoint is back online and we
                // want telemetry from tenants on the latest release. The signing scheme on
                // AnonUsageStatsModel deliberately matches the older importers so the server keeps
                // accepting payloads from versions that pre-date this re-enable.
                // statsDatesLoader is hoisted outside this loop so the in-memory fallback retains
                // its "last uploaded" timestamp across cycles.
                using (var db = new AnalyticsEntitiesContext())
                {
                    var sqlUsageBuilder = new SqlUsageStatsBuilder(db, logger, configuredSettings.TenantGUID);
                    using (var statsUploader = new WebApiStatsUploader(configuredSettings.StatsApiUrl, configuredSettings.StatsApiSecret, logger))
                    {
                        var stats = new UsageStatsManager(sqlUsageBuilder, statsDatesLoader, statsUploader, logger);
                        await stats.ProcessAndFailSilently();
                    }
                }

                if (runAgain)
                {
                    ConsoleApp.WebjobWait(logger, configuredSettings.ImportCyclePauseMinutes);
                }
            } // Go around again?

            ConsoleApp.BombOut(false);
        }


        /// <summary>
        /// Tests the SQL DB configured. Bombs out if a problem
        /// </summary>
        private static void TestDB(ILogger logger)
        {
            logger.LogInformation("Testing SQL configuration...");

            using (AnalyticsEntitiesContext db = new AnalyticsEntitiesContext())
            {
                try
                {
                    int count = (from allDownloads in db.AuditEventsCommon
                                 select allDownloads).Count();
                    logger.LogInformation($"Found {count.ToString("n0")} events in table already. Test passed!");
                }
                catch (System.Data.SqlClient.SqlException ex)
                {
                    logger.LogError(ex, $"Got a SQL error: {ex.Message}");
                    ConsoleApp.BombOut(true);
                }
            }
        }

        /// <summary>
        /// Confirm and validate settings
        /// </summary>
        private static void PrintStartupDetails(AppConfig settings, ILogger logger)
        {
            ConsoleApp.PrintStartupAndLoggingConfig(settings.ConnectionStrings.DatabaseConnectionString, settings.BuildLabel, settings.UserGroupsFilter, logger);

            var efConnectionString = ConfigurationManager.ConnectionStrings["SPOInsightsEntities"].ConnectionString;
            var sqlConnectionInfo = new System.Data.SqlClient.SqlConnectionStringBuilder(efConnectionString);

            logger.LogInformation("\nConfigured values:");

            logger.LogInformation($"Destination SQL Server='{sqlConnectionInfo.DataSource}', DB='{sqlConnectionInfo.InitialCatalog}'.");
            logger.LogInformation($"Azure AD tenant='{settings.TenantDomain}, client ID='{settings.ClientID}'.");
            logger.LogInformation($"Days back to check for events from Activity API='{settings.DaysBeforeNowToDownload}'.");
            logger.LogInformation($"Import aggressiveness='{settings.ImportAggressiveness}' (audit-load threads={settings.MaxAuditReportLoadConcurrency}, " +
                $"SQL-commit threads={settings.MaxSqlCommitConcurrency}, " +
                $"cycle pause={settings.ImportCyclePauseMinutes} min, non-fresh Graph import interval={settings.GraphMetadataImportIntervalHours}h).");

            // Print & verify O365 workloads to import
            var validWorkloadsConfig = false;
            var workloadsConfig = settings.ContentTypesString;
            if (!string.IsNullOrWhiteSpace(workloadsConfig))
            {
                var workloadsInConfig = workloadsConfig.Split(";".ToCharArray());
                if (workloadsInConfig.Length > 0)
                {
                    validWorkloadsConfig = true;
                    logger.LogInformation("\nConfigured workloads to import:");
                    foreach (var workload in workloadsInConfig)
                    {
                        logger.LogInformation($"+{workload}");
                    }
                    Console.WriteLine();
                }
            }
            if (!validWorkloadsConfig)
            {
                logger.LogError("CONFIG ERROR: No Office 365 workloads found in configuration key 'ContentTypesListAsString'!");
                ConsoleApp.BombOut(true);
            }
        }
    }
}
