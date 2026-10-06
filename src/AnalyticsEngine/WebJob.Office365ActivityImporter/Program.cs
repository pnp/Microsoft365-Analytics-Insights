// All rights reserved.
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
// PARTICULAR PURPOSE.

#region Usings
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Installer;
using Common.Entities.State;
using Common.Entities.UserScope;
using DataUtils;
using DataUtils.Health;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI; // for AuditTraceConfig
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;
using WebJob.Office365ActivityImporter.Engine.MessageTracing;
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
            MessageTraceBlobUploader messageTraceUploader = ConfigureMessageTracing(configuredSettings, logger);

            // The UserGroupsFilter scope, shared by every import in this process - the Graph sections, the audit
            // import, the calls queue and the agent-cost import - so they all apply the same resolution and the
            // groups are read once per refresh interval, not once per import. Process-lifetime for the same reason
            // as the stores below: it also remembers the last good scope to fall back on if a refresh fails.
            var userScopeProvider = UserImportScopeProvider.CreateForGraph(configuredSettings, logger);

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
                            auth.Creds, logger, configuredSettings.TenantGUID.ToString(),
                            await userScopeProvider.GetScopeAsync());

                        await ShutdownMessageTracingAsync(messageTraceUploader);
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
            // the stats endpoint). Both implementations are cheap to construct; the durable one opens
            // the runtime state table lazily, on first use.
            IStatsDatesLoader statsDatesLoader;
            var importScheduleStore = StateStore.TryOpen(configuredSettings, StatePartitions.ImportSchedule, logger);
            if (importScheduleStore != null)
            {
                logger.LogInformation($"Runtime state (import schedule, checkpoints, delta tokens) is kept in Azure Table storage: table '{StateStore.TableName}'.");
                statsDatesLoader = new PersistedStatsDatesLoader(importScheduleStore);
            }
            else
            {
                logger.LogInformation("No Storage connection string configured - using in-memory throttle for stats upload (the MIN_WAIT window resets each time the WebJob process restarts).");
                statsDatesLoader = new InMemoryStatsDatesLoader();
            }

            // Activity/usage reports also run at most once a day. Like the stats throttle above, this needs a
            // store that survives across cycles - the state table when Storage is configured, otherwise an in-memory
            // fallback built ONCE here (a fresh per-cycle instance would always look "never imported" and re-run the
            // multi-hour usage-report phase every cycle).
            ISingleDateStore activityReportsLastImportedStore = ActivityReportsLastImportedStoreFactory.Create(configuredSettings, logger);

            // Per-report completion stamps, feeding the finalized-date skip list. Also created ONCE here for
            // the same reason: the in-memory fallback must survive across cycles or every report re-downloads
            // its full window every time.
            IReportCompletionStore reportCompletionStore = ReportCompletionStoreFactory.Create(configuredSettings, logger);

            // Cadence-gate store for the non-fresh Graph imports (user metadata, user apps, Teams).
            // Created ONCE here, outside the cycle loop, so the in-memory fallback retains its "last run"
            // timestamps across cycles (mirrors statsDatesLoader above). The state table when Storage is
            // configured (gate also survives WebJob restarts); otherwise in-memory (resets on restart).
            // Reads/writes are fail-open so a storage blip never skips an import.
            IImportLastRunStore graphLastRunStore;
            if (importScheduleStore != null)
            {
                graphLastRunStore = new PersistedImportLastRunStore(importScheduleStore, logger);
            }
            else
            {
                logger.LogInformation("No Storage connection string configured - using in-memory import cadence gating (resets when the WebJob restarts).");
                graphLastRunStore = new InMemoryImportLastRunStore();
            }

            // Negative cache of users with no Exchange mailbox, for the sent-emails import. Also created
            // ONCE here so the in-memory fallback survives across cycles - a per-cycle instance would
            // always start empty and re-check (and 404 on) every mailbox-less user every 10 minutes.
            ISentEmailMailboxSkipList sentEmailMailboxSkipList;
            var sentEmailStateStore = StateStore.TryOpen(configuredSettings, StatePartitions.SentEmails, logger);
            if (sentEmailStateStore != null)
            {
                sentEmailMailboxSkipList = new PersistedSentEmailMailboxSkipList(sentEmailStateStore, logger);
            }
            else
            {
                sentEmailMailboxSkipList = new InMemorySentEmailMailboxSkipList();
            }

            // Service Bus listener for Teams call notifications. Created ONCE here, outside the cycle
            // loop, because the processor must outlive a single import cycle - ProgramTasks is rebuilt
            // every cycle, so holding it there would create (and leak) a new Service Bus client and
            // message pump every time. This replaces the process-wide static singleton that used to
            // live inside CallQueueProcessor (issue #378); it is only built on the first cycle that
            // actually needs it, and only published after Init() succeeds, so a failed start-up is
            // retried on the next cycle exactly as before.
            CallQueueProcessor callQueueProcessor = null;

            // Run app
            while (runAgain)
            {
                var importCycleTelemetryScope = logger.BeginOperationScope(Guid.NewGuid().ToString("N"));
                var importCycleTimer = new JobTimer(logger, Process.GetCurrentProcess().ProcessName);
                importCycleTimer.Start();

                // States which users this cycle's imports cover - including when a filter matches no group or is
                // failing open - every cycle, not only when the scope is refreshed.
                await userScopeProvider.GetScopeForCycleAsync();

                var tasks = new ProgramTasks(logger, configuredSettings, activityReportsLastImportedStore, graphLastRunStore, sentEmailMailboxSkipList, reportCompletionStore, userScopeProvider);

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
                            if (callQueueProcessor == null)
                            {
                                var newProcessor = new CallQueueProcessor(configuredSettings, configuredSettings.TenantGUID.ToString(), userScopeProvider);
                                await newProcessor.Init();
                                callQueueProcessor = newProcessor;
                            }

                            await tasks.ProcessCallQueueAndWebhook(webHookUrl, callQueueProcessor);
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
                    // Get Teams & user data: every Graph section except the deferred usage reports, which run
                    // after the Activity API import below (issue #706).
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

                // Activity import (Office 365 Management Activity API). Runs when any workload needs
                // Audit.SharePoint or Audit.General (SharePoint audit, Copilot, or Power Platform).
                if (configuredSettings.ImportJobSettings.UsesActivityApi)
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
                // every cycle. Deliberately OUTSIDE the try/catch above, outside DownloadActivityData, AND
                // outside the import feature gate: an aborted cycle, a cycle that downloaded nothing, a
                // cycle skipped for having no organisation URLs, and a tenant that has since DISABLED both
                // import toggles must all still heal the database. The Copilot Adoption page can be viewed
                // with the audit import switched off (it also runs on the Graph usage reports), so gating
                // this on the importer would leave those rows invisible indefinitely.
                //
                // The script self-guards on the columns existing, so it is a no-op on a database that has
                // not been upgraded yet. Costs 4 logical reads when there is nothing to do, and never throws.
                await ActivityImporter.Engine.ActivityAPI.Copilot.CopilotAuditEventManager
                    .RepairDenormalisedColumnsAsync(configuredSettings.ConnectionStrings.DatabaseConnectionString, logger);

                // Merge Copilot agents an older importer split across several copilot_agents rows (#699). It is
                // here, outside every gate, for the same reasons as the repair above. Two scans of copilot_agents
                // when there is nothing to merge, and it never throws.
                await ActivityImporter.Engine.ActivityAPI.Copilot.CopilotAuditEventManager
                    .RepairSplitAgentsAsync(configuredSettings.ConnectionStrings.DatabaseConnectionString, logger);

                // Agent cost imports (Copilot Studio billed credits + Azure Cost Management). Kept as their
                // own phase rather than folded into the Graph or Activity API imports: they authenticate to
                // different audiences (api.powerplatform.com and management.azure.com) and need role
                // assignments neither of those imports require, so a failure in one must not implicate the
                // others. The phase handles its own errors and cadence gating.
                await tasks.ImportAgentCosts();

                // Deferred Graph sections: the once-a-day usage-report phase (issue #706). It used to run inside
                // GetGraphTeamsAndUserData above, so on the cycle it was due the audit import (Copilot, Power
                // Platform, DLP, SharePoint), the Copilot repairs and the agent-cost import all waited for it - for
                // hours on a large tenant, much of it honouring Graph's ~10-minute Retry-After. Those reports are
                // 2-3 days behind at source while everything above is near-real-time, so the phase now runs last.
                // It has its own try/catch, outside the Activity API gate and its try/catch, so it runs whether that
                // import is disabled, skipped or failed, and a failure in either never skips the other.
                try
                {
                    await tasks.GetDeferredGraphData();
                }
                catch (Exception ex)
                {
                    logger.TrackException(ex);
                    logger.LogCritical($"Got exception on {nameof(ProgramTasks.GetDeferredGraphData)}: {ex.Message}");
#if DEBUG
                    throw;
#endif
                }

#if DEBUG
                runAgain = false; // Debug only runs once; release runs forever. 
#endif

                // Output cycle stats
                importCycleTimer.TrackFinishedEventAndStopTimer(AnalyticsLogger.AnalyticsEvent.FinishedImportCycle);
                if (messageTraceUploader != null)
                {
                    TrackMessageTracingHealth(logger, messageTraceUploader);
                    messageTraceUploader.LogSummary(logger);
                }

                // Upload latest stats if not done recently. Re-enabled in this build after the
                // Feb-2026 deprecation (commit 3485bd2) — the server endpoint is back online and we
                // want telemetry from tenants on the latest release. The signing scheme on
                // AnonUsageStatsModel deliberately matches the older importers so the server keeps
                // accepting payloads from versions that pre-date this re-enable.
                // statsDatesLoader is hoisted outside this loop so the in-memory fallback retains
                // its "last uploaded" timestamp across cycles.
                using (var db = new AnalyticsEntitiesContext())
                {
                    // Anonymised Copilot/licence adoption metrics ride along with the deployment stats.
                    // The collector does its own availability and weekly-cadence gating and fails soft,
                    // so on most cycles this costs a single state-table read. It reuses graphLastRunStore -
                    // hoisted outside this loop - so the in-memory fallback keeps its cadence stamp
                    // across cycles rather than re-running the analysis every time.
                    var adoptionStatsCollector = new AdoptionStatsCollector(configuredSettings, graphLastRunStore, logger);

                    var sqlUsageBuilder = new SqlUsageStatsBuilder(db, logger, configuredSettings.TenantGUID, adoptionStatsCollector);
                    using (var statsUploader = new WebApiStatsUploader(configuredSettings.StatsApiUrl, configuredSettings.StatsApiSecret, logger))
                    {
                        var stats = new UsageStatsManager(sqlUsageBuilder, statsDatesLoader, statsUploader, logger);
                        await stats.ProcessAndFailSilently();
                    }
                }

                importCycleTelemetryScope.Dispose();

                if (runAgain)
                {
                    ConsoleApp.WebjobWait(logger, configuredSettings.ImportCyclePauseMinutes);
                }
            } // Go around again?

            await ShutdownMessageTracingAsync(messageTraceUploader);
            ConsoleApp.BombOut(false);
        }

        private static async Task ShutdownMessageTracingAsync(MessageTraceBlobUploader messageTraceUploader)
        {
            if (messageTraceUploader != null)
            {
                await messageTraceUploader.FlushAsync(TimeSpan.FromSeconds(10));
                messageTraceUploader.Dispose();
            }
            HttpMessageTracing.Current = HttpMessageTracing.Disabled;
        }

        internal static MessageTraceBlobUploader ConfigureMessageTracing(AppConfig settings, AnalyticsLogger logger)
        {
            HttpMessageTracing.Current = HttpMessageTracing.Disabled;
            if (settings == null || string.IsNullOrWhiteSpace(settings.MessageTraceMatch))
            {
                logger.TrackHealthCheck(HealthComponent.MessageTracing, HealthStatus.Healthy,
                    "Message tracing is off.",
                    reasonKey: "messageTracing.disabled");
                return null;
            }

            if (!MessageTracePatternMatcher.TryCreate(settings.MessageTraceMatch, logger, out var matcher, out var failure))
            {
                logger.LogWarning((failure ?? "MessageTraceMatch is invalid; tracing is disabled.") + " The import will continue.");
                logger.TrackHealthCheck(HealthComponent.MessageTracing, HealthStatus.Degraded,
                    "Message tracing was requested but its configuration is invalid, so tracing is disabled and imports continue normally. Check MessageTraceMatch and MessageTraceContainer in App Service application settings.",
                    reasonKey: "messageTracing.invalidPattern");
                return null;
            }

            try
            {
                var uploader = MessageTraceBlobUploader.Create(settings, logger);
                HttpMessageTracing.Current = new MessageTraceInspectingTracer(matcher, uploader, settings.MessageTraceMaxBodyBytes, settings.MessageTraceMaxPerHour, logger);
                var patterns = string.Join("; ", matcher.Patterns);
                logger.LogWarning($"MESSAGE TRACING IS ENABLED: every API response that matches '{patterns}' is saved in full to blob container '{settings.MessageTraceContainer}' in the solution's storage account. These responses can contain personal data. Remove the MessageTraceMatch app setting to turn it off.");
                logger.TrackHealthCheck(HealthComponent.MessageTracing, HealthStatus.Degraded,
                    "Message tracing is enabled. Matching API responses are being saved in full to Azure Blob storage and can contain personal data; remove the MessageTraceMatch app setting to turn it off.",
                    reasonKey: "messageTracing.enabled");
                return uploader;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Message tracing could not be initialised and is disabled; the import will continue. {ex.GetType().Name}: {ex.Message}");
                TrackMessageTracingStorageUnavailable(logger);
                return null;
            }
        }

        /// <summary>
        /// Per-cycle Health for an enabled tracer. The container is opened lazily on the first save, so a storage
        /// problem only shows up after that; once an open has failed, Health says traces aren't being saved
        /// rather than that they are, until an open succeeds.
        /// </summary>
        private static void TrackMessageTracingHealth(AnalyticsLogger logger, MessageTraceBlobUploader uploader)
        {
            if (uploader.IsStorageUnavailable)
            {
                TrackMessageTracingStorageUnavailable(logger);
                return;
            }
            TrackMessageTracingEnabled(logger);
        }

        private static void TrackMessageTracingStorageUnavailable(AnalyticsLogger logger)
        {
            logger.TrackHealthCheck(HealthComponent.MessageTracing, HealthStatus.Degraded,
                "Message tracing is requested but can't save to Azure Blob storage, so no responses are being saved; imports continue normally. Check the Storage connection string, the blob container name, network access to the storage account and the Storage Blob Data Contributor role.",
                reasonKey: "messageTracing.storageUnavailable");
        }

        private static void TrackMessageTracingEnabled(AnalyticsLogger logger)
        {
            logger.TrackHealthCheck(HealthComponent.MessageTracing, HealthStatus.Degraded,
                "Message tracing is enabled. Matching API responses are being saved in full to Azure Blob storage and can contain personal data; remove the MessageTraceMatch app setting to turn it off.",
                reasonKey: "messageTracing.enabled");
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
                catch (Microsoft.Data.SqlClient.SqlException ex)
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
            var sqlConnectionInfo = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(efConnectionString);

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
