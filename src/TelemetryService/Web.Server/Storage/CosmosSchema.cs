using Microsoft.Azure.Cosmos;
using System.Net;

namespace Web.Storage
{
    /// <summary>
    /// Read-only checks that a Cosmos container the service uses exists and is partitioned as the code expects.
    /// </summary>
    /// <remarks>
    /// Checks, never creates. The service signs in to Cosmos DB with Microsoft Entra ID, and Entra data-plane
    /// RBAC does not authorise creating databases or containers through the data SDK: <c>CreateIfNotExists</c>
    /// fails with 403, substatus 5300, for a container that is missing
    /// (https://learn.microsoft.com/azure/cosmos-db/troubleshoot-forbidden#nondata-operations-arent-allowed).
    /// Reading container metadata is allowed (<c>Microsoft.DocumentDB/databaseAccounts/readMetadata</c>, part of
    /// the built-in data roles). So every container is provisioned by <c>infra/TelemetryService</c>, and the service
    /// only reports one that is not there.
    /// </remarks>
    internal static class CosmosSchema
    {
        /// <summary>A description of what is wrong with the container, or null if it is as expected.</summary>
        public static async Task<string?> CheckContainerAsync(
            CosmosClient client,
            string databaseName,
            string containerName,
            string partitionKeyPath,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await client.GetContainer(databaseName, containerName)
                    .ReadContainerAsync(cancellationToken: cancellationToken);

                var actualPath = response.Resource?.PartitionKeyPath;
                return string.Equals(actualPath, partitionKeyPath, StringComparison.Ordinal)
                    ? null
                    : $"Cosmos container '{containerName}' in database '{databaseName}' is partitioned on " +
                      $"'{actualPath}', but the service reads and writes it by '{partitionKeyPath}'.";
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return $"Cosmos container '{containerName}' does not exist in database '{databaseName}'. It needs " +
                       $"partition key '{partitionKeyPath}'.";
            }
        }
    }
}
