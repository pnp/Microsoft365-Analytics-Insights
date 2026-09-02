using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Net;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;
using WebJob.Office365ActivityImporter.Engine.Graph.Email;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter
{
    /// <summary>
    /// Everything that happens in this process
    /// </summary>
    public class ProgramTasks
    {
        private bool _isInitialized = false;
        private GraphServiceClient _graphClient = null;
        private readonly GraphAppIndentityOAuthContext _graphAppIndentityOAuthContext;
        private readonly AnalyticsLogger _logger;
        private readonly AppConfig _settings;
        private ManualGraphCallClient _manualGraphCallClient = null;
        private GraphUserGroupsCache _graphUserGroupsCache = null;
        private readonly ISingleDateStore _activityReportsLastImportedStore;
        private readonly IImportLastRunStore _graphLastRunStore;
        private readonly ISentEmailMailboxSkipList _sentEmailMailboxSkipList;

        public ProgramTasks(AnalyticsLogger logger, AppConfig settings, ISingleDateStore activityReportsLastImportedStore = null, IImportLastRunStore graphLastRunStore = null, ISentEmailMailboxSkipList sentEmailMailboxSkipList = null)
        {
            _graphAppIndentityOAuthContext = new GraphAppIndentityOAuthContext(logger, settings.ClientID, settings.TenantGUID.ToString(), settings.ClientSecret, settings.KeyVaultUrl, settings.UseClientCertificate);
            _logger = logger;
            _settings = settings;
            _activityReportsLastImportedStore = activityReportsLastImportedStore;
            _graphLastRunStore = graphLastRunStore;
            _sentEmailMailboxSkipList = sentEmailMailboxSkipList;
        }

        /// <summary>
        /// Start listening for queued call notifications and make sure the Graph webhook subscription
        /// is in place. The processor is owned by the caller because it must outlive a single import
        /// cycle - Program.cs creates it once for the process (see issue #378).
        /// </summary>
        internal async Task ProcessCallQueueAndWebhook(Uri webHookUrl, CallQueueProcessor callQueueProcessor)
        {
            if (callQueueProcessor is null) throw new ArgumentNullException(nameof(callQueueProcessor));

            // Fire and forget calls SB receiver
            _ = callQueueProcessor.BeginProcessCallsQueue();

            _logger.LogInformation("Verifying call webhook subscription.");
            var callWebhook = new CallWebhook(_settings, _logger);
            await callWebhook.CreateOrUpdateWebhook(webHookUrl, _settings.ClientSecret);

        }

        /// <summary>
        /// Graph data
        /// </summary>
        internal async Task GetGraphTeamsAndUserData()
        {
            _logger.LogInformation("Starting Teams & Graph import.");

            await InitAuth();

            var graphReader = new GraphImporter(_logger, _graphUserGroupsCache, _graphAppIndentityOAuthContext, _graphClient, _settings, _activityReportsLastImportedStore, _graphLastRunStore, _sentEmailMailboxSkipList);

            try
            {
                await graphReader.GetAndSaveAllGraphData(_settings);
            }
            catch (ODataError ex)
            {
                // Don't make a drama if Graph permissions aren't assigned yet.
                if (ex.ResponseStatusCode == (int)HttpStatusCode.Forbidden)
                {
                    _logger.LogWarning("ERROR: Can't access Teams user data - are application permissions configured correctly?");
                    return;
                }
                else
                {
                    _logger.LogError(ex, ex.Message);
                    throw;
                }
            }

            _logger.LogInformation("Finished Graph API import tasks.");
        }

        async Task InitAuth()
        {
            if (_isInitialized)
            {
                return;
            }
            await _graphAppIndentityOAuthContext.InitClientCredential();
            _graphClient = GraphServiceClientFactory.CreateWithTimeout(_graphAppIndentityOAuthContext.Creds, TimeSpan.FromHours(1));
            _manualGraphCallClient = new ManualGraphCallClient(_graphAppIndentityOAuthContext, _logger);
            _graphUserGroupsCache = new GraphUserGroupsCache(_manualGraphCallClient, _logger);


            _isInitialized = true;

        }

        /// <summary>
        /// Activity API
        /// </summary>
        internal async Task DownloadActivityData()
        {
            await InitAuth();

            // Remember start time
            DateTime startTime = DateTime.Now;

            using (var db = new AnalyticsEntitiesContext())
            {
                var spFilterList = await SharePointOrgUrlsFilterConfig.Load(db);

                if (spFilterList.OrgUrlConfigs.Count == 0)
                {
                    _logger.LogCritical("FATAL ERROR: No org URLs found in database! " +
                        "This means everything would be ignored for SharePoint audit data. Add at least one URL to the org_urls table for this to work.");

                    return;

                }

                _logger.LogInformation("\nBeginning import. Filtering for SharePoint events below these URLs:");

                // Print URLs
                spFilterList.Print(_logger);
                Console.WriteLine();

                _logger.LogInformation($"Starting activity import for {spFilterList.OrgUrlConfigs.Count} url filters");

                // Start new O365 activity download session
                // Reduced from 20000 to 5000, then to 2000 to prevent OutOfMemoryException with large datasets
                const int MAX_IMPORTS_PER_BATCH = 2000;

                // Concurrent-save mode is opt-in and OFF by default (1 = the original strictly-serial save).
                // Set AUDIT_MAX_CONCURRENT_SAVES > 1 to let batches commit in parallel (sharded staging;
                // shared-table writes still serialised). Validate in a non-production environment before use.
                var maxConcurrentSaves = ImportRuntimeOptions.ResolveMaxConcurrentSaves(
                    Environment.GetEnvironmentVariable(ImportRuntimeOptions.MaxConcurrentSavesEnvVariable));
                if (maxConcurrentSaves > ImportRuntimeOptions.DefaultMaxConcurrentSaves)
                {
                    _logger.LogInformation($"Activity import: concurrent-save mode enabled ({ImportRuntimeOptions.MaxConcurrentSavesEnvVariable}={maxConcurrentSaves}).");
                }

                var importer = new ActivityWebImporter(_settings, _logger, MAX_IMPORTS_PER_BATCH, maxConcurrentSaves);

                // Safety valve: the dedup cache is built ONCE per cycle by default (it used to be rebuilt from
                // audit_events for every batch, which materialised ~the whole in-window event set each time -
                // the dominant save cost at scale). Set AUDIT_PERBATCH_DEDUP_CACHE=true to restore the old
                // per-batch build without a redeploy if the new path ever misbehaves.
                var usePerBatchDedupCache = ImportRuntimeOptions.ResolveUsePerBatchDedupCache(
                    Environment.GetEnvironmentVariable(ImportRuntimeOptions.PerBatchDedupCacheEnvVariable));
                if (usePerBatchDedupCache)
                {
                    _logger.LogWarning($"Activity import: per-batch dedup cache ENABLED ({ImportRuntimeOptions.PerBatchDedupCacheEnvVariable}) - reverts the per-cycle cache optimisation; expect slower saves on large tables.");
                }

                var sqlAdaptor = new ActivityReportSqlPersistenceManager(spFilterList, _graphUserGroupsCache, _logger, _settings, maxConcurrentSaves, usePerBatchDedupCache);
                try
                {
                    var stats = await importer.LoadReportsAndSave(sqlAdaptor);

                    // Output stats
                    _logger.LogInformation($"Finished activity import. Time taken in = {DateTime.Now.Subtract(startTime).TotalMinutes.ToString("N2")} minutes. Stats: {stats}");
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    _logger.LogError(ex, $"Got unexpected exception importing activity: {ex.Message}");
                }
            }
        }
    }
}
