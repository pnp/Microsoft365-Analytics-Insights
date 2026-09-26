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
        /// Declares which user-org attributes this import cycle should also read from Graph.
        /// </summary>
        /// <remarks>
        /// Must be called before <see cref="LoadAllActiveUsers"/>. The implementation derives both the
        /// <c>$select</c> and the delta-token cache key from the same selection, so that a token minted
        /// under one set of properties is never reused under another - Graph freezes <c>$select</c> for
        /// the life of a token, so reusing one would silently return the old property set forever.
        /// </remarks>
        void SetOrgSelection(GraphUserOrgSelection orgSelection);

        /// <summary>
        /// Whether Graph rejected the configured org attributes during this cycle, so the load fell back
        /// to reading users without them. Org values must not be written when this is <c>true</c>: the
        /// response carries no org properties, so every user would look as though their value had been
        /// cleared.
        /// </summary>
        bool OrgSelectionWasRejected { get; }

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
