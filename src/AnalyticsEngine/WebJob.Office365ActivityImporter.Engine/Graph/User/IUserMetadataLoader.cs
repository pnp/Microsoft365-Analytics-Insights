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
        /// <returns>
        /// List of active users. When <see cref="LastLoadReachedDeltaLink"/> is false afterwards, the list is
        /// only what was read before the load stopped - possibly nothing.
        /// </returns>
        Task<List<GraphUser>> LoadAllActiveUsers();

        /// <summary>
        /// Whether the most recent <see cref="LoadAllActiveUsers"/> read the complete delta result: every page,
        /// ending in an <c>@odata.deltaLink</c> whose token <see cref="CommitDeltaTokenAsync"/> can persist.
        /// </summary>
        /// <remarks>
        /// False means the read stopped early, so the users returned are partial and there is no new checkpoint
        /// to save. The caller must report the import as incomplete rather than as done: from the returned list
        /// alone, "nothing changed in the tenant" and "the read failed" look identical - both are empty. That is
        /// how an expired token stalled the import for weeks while every run looked successful (issue #664).
        /// </remarks>
        bool LastLoadReachedDeltaLink { get; }

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
