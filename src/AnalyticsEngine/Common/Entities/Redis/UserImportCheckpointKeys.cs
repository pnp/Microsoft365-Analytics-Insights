using System;

namespace Common.Entities.Redis
{
    /// <summary>
    /// The <c>/users/delta</c> query this product tracks users with, and the version stamp that pins it.
    /// </summary>
    /// <remarks>
    /// Microsoft Graph fixes the <c>$select</c> when a delta token is first minted: a stored token
    /// continues the cycle it was created for, so widening the selection later does NOT start returning
    /// the new property to a tenant that already has one. That makes every <c>$select</c> change a
    /// breaking change for existing deployments unless the stored token is invalidated with it.
    ///
    /// <para>
    /// <see cref="SelectVersion"/> is part of the delta-token cache key, so bumping it discards the
    /// stored token and the next import performs one full enumeration under the new selection. That is
    /// the only thing that makes a newly selected property arrive for users who have not otherwise
    /// changed - and those are the overwhelming majority on an established tenant.
    /// </para>
    ///
    /// <para>
    /// <b>Bump <see cref="SelectVersion"/> in the same change that edits <see cref="Select"/>.</b>
    /// Forgetting it does not fail anywhere: the import keeps running, the new column simply stays
    /// empty forever on every upgraded tenant while looking correct on a fresh install. v2 added
    /// <c>createdDateTime</c> for the Copilot Adoption seat-tenure proxy.
    /// </para>
    ///
    /// <para>
    /// Lives here rather than in the importer, next to the key it versions, because the web portal needs
    /// that key as well: its Administration &gt; User import page reports and clears the stored token
    /// (issue #664). One definition means the importer and the portal cannot disagree about its name.
    /// </para>
    /// </remarks>
    public static class GraphUserDeltaQuery
    {
        /// <summary>Bump whenever <see cref="Select"/> changes. Part of the delta-token cache key.</summary>
        public const string SelectVersion = "v2";

        /// <summary>
        /// Properties tracked for user changes.
        /// </summary>
        /// <remarks>
        /// assignedLicenses / assignedPlans are here as defence-in-depth so that a user whose ONLY
        /// change is a licence assignment is still surfaced by /users/delta on subsequent runs. The
        /// primary correctness guarantee for licence counts comes from UserMetadataUpdater /
        /// UserLicenseProcessor processing the full DB user population each run, not just delta users.
        ///
        /// createdDateTime is Entra's immutable account-creation timestamp, used by Copilot Adoption as
        /// the seat-tenure proxy until real licence-assignment history exists.
        /// </remarks>
        public const string Select =
            "id,accountEnabled,createdDateTime,officeLocation,usageLocation,jobTitle,department,mail,"
            + "userPrincipalName,manager,companyName,postalCode,country,state,assignedLicenses,assignedPlans";
    }

    /// <summary>
    /// The Redis keys that hold the Graph user import's progress. The importer writes them; the web portal's
    /// Administration &gt; User import page reads them and can clear them. Both are stored unprefixed in Redis
    /// database 0, so an operator can also find them with the cache's console.
    /// </summary>
    public static class UserImportCheckpointKeys
    {
        /// <summary>
        /// The key of a tenant's stored <c>/users/delta</c> token: the user import's checkpoint.
        /// </summary>
        /// <remarks>
        /// Versioned by <see cref="GraphUserDeltaQuery.SelectVersion"/> on purpose. Graph fixes the
        /// <c>$select</c> when a token is minted, so a stored token keeps returning the OLD property set
        /// however the query is edited afterwards. Including the version means a selection change
        /// invalidates the token automatically: the next import falls through to a full enumeration and
        /// the newly selected property is populated for users who have not otherwise changed. Without
        /// this, a new column stays empty forever on every upgraded tenant while looking perfectly
        /// correct on a fresh install - which is close to undetectable.
        ///
        /// <para>
        /// The format is load-bearing: changing it orphans every stored token, so every deployment reads its
        /// whole directory again on upgrade. The tenant id is written in the default <c>Guid</c> format -
        /// lower case, with hyphens.
        /// </para>
        /// </remarks>
        public static string DeltaToken(Guid tenantId)
        {
            return $"UserDeltaCode-{tenantId}-{GraphUserDeltaQuery.SelectVersion}";
        }

        /// <summary>
        /// When the user import last completed, in round-trip ("o") UTC format. The importer's cadence gate
        /// reads it to run the import at most once per <c>GraphMetadataImportIntervalHours</c>, so deleting it
        /// makes the import run on the next cycle.
        /// </summary>
        public const string LastCompleted = "GraphUsersMetadataLastImported";
    }
}
