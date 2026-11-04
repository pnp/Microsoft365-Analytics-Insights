using Microsoft.Azure.Cosmos;
using System.Net;
using UsageReporting;

namespace Web.Storage
{
    /// <summary>
    /// Cosmos-backed <see cref="IClientAnnotationStore"/>.
    /// </summary>
    /// <remarks>
    /// Uses a container of its own - see <see cref="ClientAnnotation"/> for why it cannot share the
    /// "current" container with the telemetry documents themselves.
    /// </remarks>
    public class CosmosClientAnnotationStore : IClientAnnotationStore
    {
        /// <summary>Partition key of the annotations container. <c>infra/TelemetryService</c> provisions it with it.</summary>
        internal const string PartitionKeyPath = "/" + nameof(ClientAnnotation.id);

        private readonly Container _container;
        private readonly CosmosClient _cosmosClient;
        private readonly IClientAnnotationCosmosConfig _config;

        public CosmosClientAnnotationStore(CosmosClient cosmosClient, IClientAnnotationCosmosConfig config)
        {
            _cosmosClient = cosmosClient ?? throw new ArgumentNullException(nameof(cosmosClient));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _container = cosmosClient.GetContainer(config.DatabaseName, config.ContainerNameAnnotations);
        }

        public async Task<IReadOnlyList<ClientAnnotation>> LoadAllAsync()
        {
            var results = new List<ClientAnnotation>();
            var query = new QueryDefinition("SELECT * FROM c");
            using (var iterator = _container.GetItemQueryIterator<ClientAnnotation>(query))
            {
                while (iterator.HasMoreResults)
                {
                    var page = await iterator.ReadNextAsync();
                    foreach (var item in page)
                    {
                        if (item != null) results.Add(item);
                    }
                }
            }

            return results;
        }

        public async Task<ClientAnnotation?> GetAsync(string anonClientId)
        {
            if (string.IsNullOrWhiteSpace(anonClientId)) return null;

            try
            {
                var result = await _container.ReadItemAsync<ClientAnnotation>(
                    anonClientId, new PartitionKey(anonClientId));
                return result?.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task SaveAsync(ClientAnnotation annotation)
        {
            if (annotation == null) throw new ArgumentNullException(nameof(annotation));
            if (string.IsNullOrWhiteSpace(annotation.id))
            {
                throw new ArgumentException("An annotation needs the anonymous client id it describes.", nameof(annotation));
            }

            await _container.UpsertItemAsync(annotation, new PartitionKey(annotation.id));
        }

        public async Task DeleteAsync(string anonClientId)
        {
            if (string.IsNullOrWhiteSpace(anonClientId)) return;

            try
            {
                await _container.DeleteItemAsync<ClientAnnotation>(anonClientId, new PartitionKey(anonClientId));
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // Deleting a note that is not there is the outcome the caller wanted.
            }
        }

        /// <summary>
        /// Checks the annotations container exists with <see cref="PartitionKeyPath"/>, and returns what is wrong.
        /// Never creates it - see <see cref="CosmosSchema"/>.
        /// </summary>
        public async Task<IReadOnlyList<string>> VerifySchemaAsync(CancellationToken cancellationToken)
        {
            var problem = await CosmosSchema.CheckContainerAsync(
                _cosmosClient, _config.DatabaseName, _config.ContainerNameAnnotations, PartitionKeyPath, cancellationToken);
            return problem == null ? Array.Empty<string>() : new[] { problem };
        }
    }

    public interface IClientAnnotationCosmosConfig
    {
        string DatabaseName { get; set; }

        /// <summary>
        /// Container holding maintainer annotations. MUST NOT be the telemetry "current" container,
        /// whose documents are overwritten wholesale on every client upload.
        /// </summary>
        string ContainerNameAnnotations { get; set; }
    }
}
