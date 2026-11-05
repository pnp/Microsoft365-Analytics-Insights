using Microsoft.Azure.Cosmos;
using UsageReporting;

namespace Web.Storage
{
    public interface IStatsServiceCosmosConfig
    {
        string DatabaseName { get; set; }
        string ContainerNameHistory { get; set; }
        string ContainerNameCurrent { get; set; }
    }

    /// <summary>
    /// Cosmos DB store for the anonymous usage statistics the importers upload.
    /// </summary>
    /// <remarks>
    /// Lives here, with its only consumer, rather than in <c>UsageReporting</c>: that project is
    /// referenced by the importer engine, so a Cosmos DB SDK dependency there was copied into the
    /// importer, web site and installer release packages - about 4.6 MB each - none of which ever
    /// talk to Cosmos.
    /// </remarks>
    public class CosmosTelemetrySaveAdaptor : ITelemetrySaveAdaptor, ITelemetryQueryAdaptor
    {
        /// <summary>Partition key of both telemetry containers. <c>infra/TelemetryService</c> provisions them with it.</summary>
        internal const string PartitionKeyPath = "/" + nameof(AnonUsageStatsModel.AnonClientId);

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

        public async Task<AnonUsageStatsModel?> LoadCurrentRecordByClientId(AnonUsageStatsModel model)
        {
            AnonUsageStatsModel? r = null;
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


        /// <summary>
        /// Checks the history and current containers exist with <see cref="PartitionKeyPath"/>, and returns what
        /// is wrong. Never creates them - see <see cref="CosmosSchema"/>.
        /// </summary>
        public async Task<IReadOnlyList<string>> VerifySchemaAsync(CancellationToken cancellationToken)
        {
            var problems = new List<string>();
            foreach (var containerName in new[] { _webAppConfig.ContainerNameHistory, _webAppConfig.ContainerNameCurrent })
            {
                var problem = await CosmosSchema.CheckContainerAsync(
                    _cosmosClient, _webAppConfig.DatabaseName, containerName, PartitionKeyPath, cancellationToken);
                if (problem != null) problems.Add(problem);
            }

            return problems;
        }
    }
}
