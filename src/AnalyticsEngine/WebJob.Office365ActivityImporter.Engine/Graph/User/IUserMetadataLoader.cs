using Microsoft.Graph;
using Microsoft.Graph.Models;
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
        /// Loads all subscribed SKUs for the tenant.
        /// </summary>
        /// <returns>Materialised list of subscribed SKUs, or <c>null</c> if unable to load.</returns>
        Task<List<SubscribedSku>> LoadTenantSkus();

        /// <summary>
        /// Loads users that have a specific SKU assigned
        /// </summary>
        /// <param name="skuId">The SKU ID to filter by</param>
        /// <returns>List of users with the specified SKU</returns>
        Task<List<Microsoft.Graph.Models.User>> LoadUsersBySku(System.Guid skuId);

        /// <summary>
        /// Loads license details for a specific user.
        /// </summary>
        /// <param name="userId">The user ID (Graph object ID)</param>
        /// <returns>Materialised list of license details for the user, or <c>null</c> if unable to load.</returns>
        Task<List<LicenseDetails>> LoadUserLicenseDetails(string userId);

        /// <summary>
        /// Persists any delta token captured during the most recent
        /// <see cref="LoadAllActiveUsers"/> call to the underlying delta value
        /// provider. <see cref="LoadAllActiveUsers"/> buffers the new delta in
        /// memory; callers must invoke this only after the entire user import
        /// has succeeded. If the import fails before commit, the previously
        /// persisted delta is preserved and the failed users will be retried
        /// on the next cycle.
        /// </summary>
        Task CommitDeltaTokenAsync();
    }
}
