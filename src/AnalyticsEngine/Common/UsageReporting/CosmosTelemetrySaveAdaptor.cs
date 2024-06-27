using Microsoft.Azure.Cosmos;
using System.Threading.Tasks;

namespace UsageReporting
{
    public interface IStatsServiceCosmosConfig
    {
        string DatabaseName { get; set; }
        string ContainerNameHistory { get; set; }
        string ContainerNameCurrent { get; set; }
    }
    public class CosmosTelemetrySaveAdaptor : ITelemetrySaveAdaptor
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


        public async Task Init()
        {
            await _cosmosClient.CreateDatabaseIfNotExistsAsync(_webAppConfig.DatabaseName);
            var db = _cosmosClient.GetDatabase(_webAppConfig.DatabaseName);
            await db.CreateContainerIfNotExistsAsync(id: _webAppConfig.ContainerNameHistory, partitionKeyPath: PARTITION_KEY);
            await db.CreateContainerIfNotExistsAsync(id: _webAppConfig.ContainerNameCurrent, partitionKeyPath: PARTITION_KEY);
        }
    }
}
