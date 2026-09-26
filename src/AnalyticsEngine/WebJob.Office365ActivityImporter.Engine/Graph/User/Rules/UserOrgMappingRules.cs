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
        /// </remarks>
        public static IReadOnlyList<UserOrgAssignmentUpdate> BuildUpdates(
            IEnumerable<GraphUser> graphUsers,
            IReadOnlyList<UserOrgTypeAttribute> orgTypes,
            IReadOnlyDictionary<string, int> userIdsByUpn)
        {
            var updates = new List<UserOrgAssignmentUpdate>();

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

                    var raw = UserOrgRules.ExtractRawValue(graphUser.AdditionalProperties, orgType.Spec);
                    updates.Add(new UserOrgAssignmentUpdate(
                        userId,
                        orgType.OrgTypeId,
                        UserOrgRules.NormaliseOrgValue(raw)));
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
