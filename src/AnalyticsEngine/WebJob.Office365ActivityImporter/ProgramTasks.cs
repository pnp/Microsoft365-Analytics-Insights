using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;
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
        private readonly AnalyticsLogger _telemetry;
        private readonly AppConfig _settings;
        private ManualGraphCallClient _manualGraphCallClient = null;
        private GraphUserGroupsCache _graphUserGroupsCache = null;  

        public ProgramTasks(AnalyticsLogger telemetry, AppConfig settings)
        {
            _graphAppIndentityOAuthContext = new GraphAppIndentityOAuthContext(telemetry, settings.ClientID, settings.TenantGUID.ToString(), settings.ClientSecret, settings.KeyVaultUrl, settings.UseClientCertificate);
            _telemetry = telemetry;
            _settings = settings;
        }

        internal async Task ProcessCallQueueAndWebhook(Uri webHookUrl)
        {
            var callQueueProcessor = await CallQueueProcessor.GetCallQueueProcessor(_settings, _settings.TenantGUID.ToString(), null);

            // Fire and forget calls SB receiver
            _ = callQueueProcessor.BeginProcessCallsQueue();

            _telemetry.LogInformation("Verifying call webhook subscription.");
            var callWebhook = new CallWebhook(_settings, _telemetry);
            await callWebhook.CreateOrUpdateWebhook(webHookUrl, _settings.ClientSecret);

        }

        /// <summary>
        /// Graph data
        /// </summary>
        internal async Task GetGraphTeamsAndUserData()
        {
            _telemetry.LogInformation("Starting Teams & Graph import.");

            await InitAuth();

            var graphReader = new GraphImporter(_telemetry, _graphUserGroupsCache, _graphAppIndentityOAuthContext, _graphClient, _settings);

            try
            {
                await graphReader.GetAndSaveAllGraphData(_settings);
            }
            catch (Microsoft.Graph.ServiceException ex)
            {
                // Don't make a drama if Graph permissions aren't assigned yet.
                if (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    _telemetry.LogWarning("ERROR: Can't access Teams user data - are application permissions configured correctly?");
                    return;
                }
                else
                {
                    _telemetry.LogError(ex, ex.Message);
                    throw;
                }
            }

            _telemetry.LogInformation("Finished Graph API import tasks.");
        }

        async Task InitAuth()
        {
            if (_isInitialized)
            {
                return;
            }
            await _graphAppIndentityOAuthContext.InitClientCredential();
            _graphClient = new GraphServiceClient(_graphAppIndentityOAuthContext.Creds);
            _graphClient.HttpProvider.OverallTimeout = TimeSpan.FromHours(1);
            _manualGraphCallClient = new ManualGraphCallClient(_graphAppIndentityOAuthContext, _telemetry);
            _graphUserGroupsCache = new GraphUserGroupsCache(_manualGraphCallClient, _telemetry);
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
                    _telemetry.LogCritical("FATAL ERROR: No org URLs found in database! " +
                        "This means everything would be ignored for SharePoint audit data. Add at least one URL to the org_urls table for this to work.");

                    return;

                }

                _telemetry.LogInformation("\nBeginning import. Filtering for SharePoint events below these URLs:");

                // Print URLs
                spFilterList.Print(_telemetry);
                Console.WriteLine();

                _telemetry.LogInformation($"Starting activity import for {spFilterList.OrgUrlConfigs.Count} url filters");

                // Start new O365 activity download session
                const int MAX_IMPORTS_PER_BATCH = 20000;
                var importer = new ActivityWebImporter(_settings, _telemetry, MAX_IMPORTS_PER_BATCH);

                var sqlAdaptor = new ActivityReportSqlPersistenceManager(spFilterList, _graphUserGroupsCache, _telemetry, _settings);
                try
                {
                    var stats = await importer.LoadReportsAndSave(sqlAdaptor);

                    // Output stats
                    _telemetry.LogInformation($"Finished activity import. Time taken in = {DateTime.Now.Subtract(startTime).TotalMinutes.ToString("N2")} minutes. Stats: {stats}");
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    _telemetry.LogError(ex, $"Got unexpected exception importing activity: {ex.Message}");
                }
            }
        }
    }
}
