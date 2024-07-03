using Common.Entities;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.Loaders;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace WebJob.Office365ActivityImporter
{
    /// <summary>
    /// Everything that happens in this process
    /// </summary>
    public class ProgramTasks
    {
        internal static async Task ProcessCallQueueAndWebhook(AppConfig configuredSettings, Uri webHookUrl, ILogger telemetry)
        {
            var callQueueProcessor = await CallQueueProcessor.GetCallQueueProcessor(configuredSettings, configuredSettings.TenantGUID.ToString(), null);

            // Fire and forget calls SB receiver
            _ = callQueueProcessor.BeginProcessCallsQueue();

            telemetry.LogInformation("Verifying call webhook subscription.");
            var callWebhook = new CallWebhook(configuredSettings, telemetry);
            await callWebhook.CreateOrUpdateWebhook(webHookUrl, configuredSettings.ClientSecret);

        }

        /// <summary>
        /// Graph data
        /// </summary>
        internal async static Task GetGraphTeamsAndUserData(AnalyticsLogger telemetry, AppConfig configuredSettings)
        {
            telemetry.LogInformation("Starting Teams & Graph import.");
            var graphReader = new GraphImporter(telemetry, configuredSettings);

            try
            {
                await graphReader.GetAndSaveAllGraphData(configuredSettings);
            }
            catch (Microsoft.Graph.ServiceException ex)
            {

                // Don't make a drama if Graph permissions aren't assigned yet.
                if (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    telemetry.LogWarning("ERROR: Can't access Teams user data - are application permissions configured correctly?");
                    return;
                }
                else
                {
                    telemetry.LogError(ex, ex.Message);
                    throw;
                }
            }

            telemetry.LogInformation("Finished Graph API import tasks.");
        }

        /// <summary>
        /// Activity API
        /// </summary>
        internal async static Task DownloadActivityData(AppConfig configuredSettings, AnalyticsLogger telemetry)
        {
            // Remember start time
            DateTime startTime = DateTime.Now;

            using (var db = new AnalyticsEntitiesContext())
            {
                var spFilterList = await SharePointOrgUrlsFilterConfig.Load(db);

                if (spFilterList.OrgUrlConfigs.Count == 0)
                {
                    telemetry.LogCritical("FATAL ERROR: No org URLs found in database! " +
                        "This means everything would be ignored for SharePoint audit data. Add at least one URL to the org_urls table for this to work.");

                    return;

                }

                telemetry.LogInformation("\nBeginning import. Filtering for SharePoint events below these URLs:");

                // Print URLs
                spFilterList.Print(telemetry);
                Console.WriteLine();

                telemetry.LogInformation($"Starting activity import for {spFilterList.OrgUrlConfigs.Count} url filters");

                // Start new O365 activity download session
                const int MAX_IMPORTS_PER_BATCH = 20000;
                var importer = new ActivityWebImporter(configuredSettings, telemetry, MAX_IMPORTS_PER_BATCH);

                var sqlAdaptor = new ActivityReportSqlPersistenceManager(spFilterList, telemetry, configuredSettings);
                try
                {
                    var stats = await importer.LoadReportsAndSave(sqlAdaptor);

                    // Output stats
                    telemetry.LogInformation($"Finished activity import. Time taken in = {DateTime.Now.Subtract(startTime).TotalMinutes.ToString("N2")} minutes. Stats: {stats}");
                }
                catch (System.Net.Http.HttpRequestException ex)
                {
                    telemetry.LogError(ex, $"Got unexpected exception importing activity: {ex.Message}");
                }
            }
        }
    }
}
