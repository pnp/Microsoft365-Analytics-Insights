using Microsoft.Graph;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Interface for loading user metadata from external sources
    /// </summary>
    public interface IUserMetadataLoader
    {
        /// <summary>
        /// Gets the delta value provider used by this loader
        /// </summary>
        IDeltaValueProvider DeltaValueProvider { get; }

        /// <summary>
        /// Loads all active users from the external source
        /// </summary>
        /// <returns>List of active users</returns>
        Task<List<GraphUser>> LoadAllActiveUsers();

        /// <summary>
        /// Loads all subscribed SKUs for the tenant
        /// </summary>
        /// <returns>Collection of subscribed SKUs, or null if unable to load</returns>
        Task<IGraphServiceSubscribedSkusCollectionPage> LoadTenantSkus();

        /// <summary>
        /// Loads users that have a specific SKU assigned
        /// </summary>
        /// <param name="skuId">The SKU ID to filter by</param>
        /// <returns>List of users with the specified SKU</returns>
        Task<List<Microsoft.Graph.User>> LoadUsersBySku(System.Guid skuId);

        /// <summary>
        /// Loads license details for a specific user
        /// </summary>
        /// <param name="userId">The user ID (Graph object ID)</param>
        /// <returns>Collection of license details for the user, or null if unable to load</returns>
        Task<IUserLicenseDetailsCollectionPage> LoadUserLicenseDetails(string userId);
    }
}
