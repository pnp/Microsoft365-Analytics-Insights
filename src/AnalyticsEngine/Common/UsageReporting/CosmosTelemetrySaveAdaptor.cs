using Microsoft.Azure.Cosmos;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace UsageReporting
{
    public interface IStatsServiceCosmosConfig
    {
        string DatabaseName { get; set; }
        string ContainerNameHistory { get; set; }
        string ContainerNameCurrent { get; set; }
    }
    public class CosmosTelemetrySaveAdaptor : ITelemetrySaveAdaptor, ITelemetryQueryAdaptor
    {
        private static string PARTITION_KEY = "/" + nameof(AnonUsageStatsModel.AnonClientId);
        private readonly Container _historyStatsContainer;
        private readonly Container _currentStatsContainer;
        private readonly CosmosClient _cosmosClient;
        private readonly IStatsServiceCosmosConfig _webAppConfig;

        public CosmosTelemetrySaveAdaptor(CosmosClient cosmosClient, IStatsServiceCosmosConfig webAppConfig)
        {
            _historyStatsContainer = cosmosClient.GetContainer(webAppConfig.DatabaseName, webAppConfig.ContainerNameHistory);
            _currentStatsContainer = cosmosClient.GetContainer(webAppConfig.DatabaseName, webAppConfig.ContainerNameCurrent);
            _cosmosClient = cosmosClient;
            _webAppConfig = webAppConfig;
        }

        public async Task<AnonUsageStatsModel> LoadCurrentRecordByClientId(AnonUsageStatsModel model)
        {
            AnonUsageStatsModel r = null;
            try
            {
                var result = await _currentStatsContainer.ReadItemAsync<AnonUsageStatsModel>(model.AnonClientId, new PartitionKey(model.AnonClientId));
                if (result != null && result.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    r = result.Resource;
                }
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Ignore
            }

            return r;
        }
        public async Task SaveOrUpdate(AnonUsageStatsModel model)
        {
            var historicalUpdate = new HistoricalUpdate(model);
            await _historyStatsContainer.UpsertItemAsync(historicalUpdate);
            await _currentStatsContainer.UpsertItemAsync(model);
        }

        /// <summary>
        /// Reads the latest record for every client from the "current" container.
        /// Cross-partition query, capped to <paramref name="maxItems"/> so the dashboard cannot
        /// pull an unbounded scan if the install grows.
        /// </summary>
        public async Task<IReadOnlyList<AnonUsageStatsModel>> LoadAllCurrentAsync(int? maxItems = null)
        {
            var results = new List<AnonUsageStatsModel>();
            var query = new QueryDefinition("SELECT * FROM c");
            using (var iterator = _currentStatsContainer.GetItemQueryIterator<AnonUsageStatsModel>(query))
            {
                while (iterator.HasMoreResults)
                {
                    var page = await iterator.ReadNextAsync();
                    foreach (var item in page)
                    {
                        if (item == null)
                        {
                            continue;
                        }
                        results.Add(item);
                        if (maxItems.HasValue && results.Count >= maxItems.Value)
                        {
                            return results;
                        }
                    }
                }
            }

            return results;
        }


        public async Task Init()
        {
            await _cosmosClient.CreateDatabaseIfNotExistsAsync(_webAppConfig.DatabaseName);
            var db = _cosmosClient.GetDatabase(_webAppConfig.DatabaseName);
            await db.CreateContainerIfNotExistsAsync(id: _webAppConfig.ContainerNameHistory, partitionKeyPath: PARTITION_KEY);
            await db.CreateContainerIfNotExistsAsync(id: _webAppConfig.ContainerNameCurrent, partitionKeyPath: PARTITION_KEY);
        }
    }
}
