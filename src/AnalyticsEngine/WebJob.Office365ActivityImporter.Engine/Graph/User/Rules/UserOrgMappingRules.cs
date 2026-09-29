using Common.Entities.UserOrgs;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// One configured org type paired with its parsed Graph attribute.
    /// </summary>
    public sealed class UserOrgTypeAttribute
    {
        public UserOrgTypeAttribute(int orgTypeId, EntraOrgAttributeSpec spec)
        {
            OrgTypeId = orgTypeId;
            Spec = spec;
        }

        public int OrgTypeId { get; }

        public EntraOrgAttributeSpec Spec { get; }
    }

    /// <summary>
    /// Turns a batch of Graph users into the org assignment updates to apply.
    /// </summary>
    /// <remarks>
    /// Pure: no Graph call, no database, no EF. The interesting behaviour here is a semantic decision
    /// rather than a calculation, and this is where it can be asserted directly.
    /// </remarks>
    public static class UserOrgMappingRules
    {
        /// <summary>
        /// Builds the updates for every Graph user that resolves to a database user.
        /// </summary>
        /// <param name="graphUsers">The users Graph returned this cycle.</param>
        /// <param name="orgTypes">The enabled Entra org types and their parsed attributes.</param>
        /// <param name="userIdsByUpn">
        /// UPN to <c>dbo.users.id</c>. Expected to be case-insensitive, matching the database collation.
        /// </param>
        /// <remarks>
        /// <para>
        /// A user present in the batch gets an update for <b>every</b> configured org type, including a
        /// <c>null</c> one when the attribute has no value. That is what lets a value be cleared: the
        /// user is in this response precisely because something about them changed, so an attribute that
        /// is no longer there has genuinely gone.
        /// </para>
        /// <para>
        /// A missing JSON key and an explicit <c>null</c> are treated identically, because Graph omits a
        /// property that was never set and returns <c>null</c> for one that was cleared - and for
        /// directory extensions the documentation does not commit to which of the two you get.
        /// </para>
        /// <para>
        /// Users <b>absent</b> from <paramref name="graphUsers"/> produce no update at all, so the merge
        /// leaves them alone. This is the property that stops a routine delta cycle - which returns only
        /// changed users - from wiping the org values of the entire tenant.
        /// </para>
        /// <para>
        /// A value that comes back as a <b>list</b> - a multi-valued directory extension - means the org
        /// type is misconfigured: a user holds one value per org type, so there is nothing to store. The
        /// whole type is skipped for this batch - no update for anyone, those without a value included -
        /// and added to <paramref name="listValuedOrgTypeIds"/>. Reading the list as "no value" would clear
        /// everyone who has one, and applying just the users without a value would be half-applying a
        /// configuration known to be wrong; left as it was, the type waits for its attribute to be fixed,
        /// like one Graph rejects.
        /// </para>
        /// <para>
        /// A value longer than <see cref="UserOrgRules.MaxOrgValueLength"/> - <c>extensionAttribute1-15</c>, for
        /// one, hold up to 1,024 characters - is stored shortened, exactly as a CSV's is (see
        /// <see cref="UserOrgRules.NormaliseOrgValue"/>), and counted per type in
        /// <paramref name="shortenedValueCounts"/> so the import can say so: two values that differ only past
        /// the limit become one organisation, and the save-time test only ever sees the one user it was run
        /// against. A type skipped for holding lists stores nothing, so nothing is counted for it.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<UserOrgAssignmentUpdate> BuildUpdates(
            IEnumerable<GraphUser> graphUsers,
            IReadOnlyList<UserOrgTypeAttribute> orgTypes,
            IReadOnlyDictionary<string, int> userIdsByUpn,
            ISet<int> listValuedOrgTypeIds = null,
            IDictionary<int, int> shortenedValueCounts = null)
        {
            var updates = new List<UserOrgAssignmentUpdate>();
            var listValued = new HashSet<int>();
            var shortened = new Dictionary<int, int>();

            if (graphUsers == null || orgTypes == null || orgTypes.Count == 0 || userIdsByUpn == null)
            {
                return updates;
            }

            foreach (var graphUser in graphUsers)
            {
                if (graphUser == null || string.IsNullOrEmpty(graphUser.UserPrincipalName))
                {
                    continue;
                }

                int userId;
                if (!userIdsByUpn.TryGetValue(graphUser.UserPrincipalName, out userId))
                {
                    // No database row for this user - the insert phase skipped them, or they were
                    // removed mid-cycle. There is nothing to assign an org to.
                    continue;
                }

                foreach (var orgType in orgTypes)
                {
                    if (orgType == null || orgType.Spec == null)
                    {
                        continue;
                    }

                    string raw;
                    if (!UserOrgRules.TryExtractSingleValue(graphUser.AdditionalProperties, orgType.Spec, out raw))
                    {
                        listValued.Add(orgType.OrgTypeId);
                        continue;
                    }

                    if (UserOrgRules.WouldTruncate(raw))
                    {
                        int count;
                        shortened.TryGetValue(orgType.OrgTypeId, out count);
                        shortened[orgType.OrgTypeId] = count + 1;
                    }

                    updates.Add(new UserOrgAssignmentUpdate(
                        userId,
                        orgType.OrgTypeId,
                        UserOrgRules.NormaliseOrgValue(raw)));
                }
            }

            if (shortenedValueCounts != null)
            {
                foreach (var pair in shortened)
                {
                    if (listValued.Contains(pair.Key))
                    {
                        continue;
                    }

                    int existing;
                    shortenedValueCounts.TryGetValue(pair.Key, out existing);
                    shortenedValueCounts[pair.Key] = existing + pair.Value;
                }
            }

            // Which types returned a list for anyone is known only once every user has been read, so every
            // update for those types goes - for the users read before the first list as well as after.
            // See the remarks.
            if (listValued.Count > 0)
            {
                updates.RemoveAll(u => listValued.Contains(u.OrgTypeId));
                if (listValuedOrgTypeIds != null)
                {
                    listValuedOrgTypeIds.UnionWith(listValued);
                }
            }

            return updates;
        }

        /// <summary>
        /// Pairs enabled Entra org types with their parsed attributes, skipping any whose stored
        /// attribute no longer parses.
        /// </summary>
        /// <remarks>
        /// Skipping rather than throwing is deliberate: a single unreadable row of configuration must
        /// not be able to stop the user import, which is the failure mode this whole feature is built to
        /// avoid.
        /// </remarks>
        public static IReadOnlyList<UserOrgTypeAttribute> ParseOrgTypes(
            IEnumerable<UserOrgType> orgTypes,
            out IReadOnlyList<string> skippedTypeNames)
        {
            var parsed = new List<UserOrgTypeAttribute>();
            var skipped = new List<string>();

            if (orgTypes != null)
            {
                foreach (var orgType in orgTypes)
                {
                    if (orgType == null)
                    {
                        continue;
                    }

                    EntraOrgAttributeSpec spec;
                    string error;
                    if (EntraOrgAttributeSpec.TryParse(orgType.EntraAttributeName, out spec, out error))
                    {
                        parsed.Add(new UserOrgTypeAttribute(orgType.Id, spec));
                    }
                    else
                    {
                        skipped.Add(orgType.Name);
                    }
                }
            }

            skippedTypeNames = skipped;
            return parsed;
        }
    }
}
