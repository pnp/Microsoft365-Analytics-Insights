using System;

namespace Common.Entities.State
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
    /// <see cref="SelectVersion"/> is part of the delta-token key, so bumping it discards the
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
        /// <summary>Bump whenever <see cref="Select"/> changes. Part of the delta-token key.</summary>
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
    /// The keys that hold the Graph user import's progress. The importer writes them; the web portal's
    /// Administration &gt; User import page reads them and can clear them. Both live in the
    /// <see cref="StateStore.TableName"/> Azure Table - the delta token in the <see cref="StatePartitions.UserImport"/>
    /// partition, the last-completed stamp in <see cref="StatePartitions.ImportSchedule"/> - with the key as the row
    /// key, so an operator can also find them in Azure Storage Explorer or the portal's Storage browser.
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
        /// lower case, with hyphens. (The format is unchanged from when this was a Redis key, but Redis
        /// contents are not migrated, so the first user import after that upgrade reads every user once.)
        /// </para>
        /// </remarks>
        public static string DeltaToken(Guid tenantId)
        {
            return $"UserDeltaCode-{tenantId}-{GraphUserDeltaQuery.SelectVersion}";
        }

        /// <summary>
        /// The key of a tenant's stored <c>/users/delta</c> token when the import also reads organisation attributes
        /// from Entra: <see cref="DeltaToken(Guid)"/> followed by the selection's qualifier.
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="orgAttributeQualifier">
        /// <see cref="UserOrgs.GraphUserOrgSelection.DeltaKeyQualifier"/>. Empty or <c>null</c> - no Entra organisation
        /// types configured - gives exactly <see cref="DeltaToken(Guid)"/>, so a deployment that never uses the feature
        /// keeps its token on upgrade.
        /// </param>
        /// <remarks>
        /// Graph freezes <c>$select</c> for the life of a token, so a token minted without an organisation attribute
        /// must never be resumed by a request that asks for one. The importer reads and writes this key, and the web
        /// portal's User import page reports and clears it, so both work out the qualifier the same way, from the
        /// enabled Entra organisation types.
        /// </remarks>
        public static string DeltaToken(Guid tenantId, string orgAttributeQualifier)
        {
            return DeltaToken(tenantId) + (orgAttributeQualifier ?? string.Empty);
        }

        /// <summary>
        /// The key recording which <c>UserGroupsFilter</c> the stored <c>/users/delta</c> token under
        /// <see cref="DeltaToken(Guid, string)"/> was taken under, as
        /// <see cref="Common.Entities.Config.UserGroupsFilterModel.Fingerprint"/> (empty or absent: no filter).
        /// </summary>
        /// <param name="tenantId">The tenant.</param>
        /// <param name="orgAttributeQualifier">
        /// The qualifier of the token it describes, exactly as for <see cref="DeltaToken(Guid, string)"/>: each token
        /// has its own record, because a token kept for one organisation-attribute selection is resumed when that
        /// selection comes back and must be judged by the filter it was taken under.
        /// </param>
        /// <remarks>
        /// A delta token only returns people who have changed since it was taken. While a filter is set, everyone
        /// outside it is left out of the users table, so when the filter is removed or changed those people would
        /// never be imported unless they happened to change. The importer compares this value with the current
        /// filter and, when they differ, discards the token so the next read covers the whole directory.
        /// Versioned with the token it describes.
        /// </remarks>
        public static string DeltaTokenUserScope(Guid tenantId, string orgAttributeQualifier)
        {
            return $"UserDeltaCodeScope-{tenantId}-{GraphUserDeltaQuery.SelectVersion}" + (orgAttributeQualifier ?? string.Empty);
        }

        /// <summary>
        /// The enabled group-membership fingerprint that was current when the delta token under
        /// <see cref="DeltaToken(Guid, string)"/> was produced by a full directory read.
        /// </summary>
        /// <remarks>
        /// Group membership changes are not user-object changes and therefore do not appear in <c>/users/delta</c>.
        /// Comparing this marker with the resolved membership lets the importer discard a token when somebody enters
        /// scope, even if an old row for that person already exists in <c>dbo.users</c>.
        /// </remarks>
        public static string DeltaTokenUserScopeMembers(Guid tenantId, string orgAttributeQualifier)
        {
            return $"UserDeltaCodeScopeMembers-{tenantId}-{GraphUserDeltaQuery.SelectVersion}" + (orgAttributeQualifier ?? string.Empty);
        }

        /// <summary>
        /// When the user import last completed, in round-trip ("o") UTC format. The importer's cadence gate
        /// reads it to run the import at most once per <c>GraphMetadataImportIntervalHours</c>, so deleting it
        /// makes the import run on the next cycle.
        /// </summary>
        public const string LastCompleted = "GraphUsersMetadataLastImported";
    }
}
