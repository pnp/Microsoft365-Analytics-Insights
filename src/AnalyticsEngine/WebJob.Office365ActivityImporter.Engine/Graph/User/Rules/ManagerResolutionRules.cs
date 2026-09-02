using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// The pure parts of manager resolution: which manager user principal names a batch will need to
    /// look up in the database, and how the loaded rows are indexed.
    ///
    /// Extracted for issues #371 / #381. The precedence chain in <c>UserDataMapper</c> had no test of
    /// any kind, and its last-but-one branch issued a database query <b>per user</b>; deciding the
    /// batch's lookup set up front is what turns that into one query per batch. Everything here runs
    /// with zero SQL Server and zero Graph dependency.
    /// </summary>
    internal static class ManagerResolutionRules
    {
        /// <summary>
        /// Returns the distinct UPNs of the managers referenced by <paramref name="batch"/> that could
        /// reach the database-by-UPN branch of the resolution chain.
        /// </summary>
        /// <remarks>
        /// A manager only reaches that branch when Graph reported a manager id for the user
        /// <i>and</i> that manager is present in the current Graph batch <i>and</i> has a UPN - the
        /// branch reads the UPN off the cached Graph user. Anything else can never produce a query, so
        /// prefetching it would be wasted work.
        ///
        /// The result is deliberately a superset of what the chain actually queries: whether the
        /// earlier in-memory branches hit depends on dictionary state that changes as the batch is
        /// processed, so it cannot be decided up front. Over-fetching costs nothing extra - it is the
        /// same single query either way - and under-fetching would silently reintroduce a per-user
        /// query.
        ///
        /// Comparison is case-insensitive because that is how both the Graph user dictionary and SQL
        /// Server's default collation behave.
        /// </remarks>
        public static List<string> CollectManagerUpnsToPrefetch(
            IEnumerable<GraphUser> batch,
            IDictionary<string, GraphUser> graphUsersByAadId)
        {
            var upns = new List<string>();
            if (batch == null || graphUsersByAadId == null)
            {
                return upns;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var graphUser in batch)
            {
                var managerAadId = UserMetadataMappingRules.BuildPlan(graphUser).ManagerAadId;
                if (managerAadId == null)
                {
                    continue;
                }

                if (graphUsersByAadId.TryGetValue(managerAadId, out var managerGraphUser) &&
                    !string.IsNullOrEmpty(managerGraphUser.UserPrincipalName) &&
                    seen.Add(managerGraphUser.UserPrincipalName))
                {
                    upns.Add(managerGraphUser.UserPrincipalName);
                }
            }

            return upns;
        }

        /// <summary>
        /// Indexes loaded users by UPN for the prefetch cache.
        /// </summary>
        /// <remarks>
        /// <c>dbo.users</c> has no unique constraint on <c>user_name</c> and real databases do contain
        /// duplicates, which is why <c>UserCache.Load</c> orders by id and takes the first rather than
        /// using <c>SingleOrDefaultAsync</c>. This keeps the lowest id for the same reason, so the
        /// prefetch resolves a duplicated UPN to the same row the rest of the pipeline does. Users
        /// with no UPN are skipped.
        /// </remarks>
        public static Dictionary<string, Common.Entities.User> IndexByUpn(IEnumerable<Common.Entities.User> users)
        {
            var byUpn = new Dictionary<string, Common.Entities.User>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in users)
            {
                if (string.IsNullOrEmpty(user.UserPrincipalName))
                {
                    continue;
                }

                if (!byUpn.TryGetValue(user.UserPrincipalName, out var existing) || user.ID < existing.ID)
                {
                    byUpn[user.UserPrincipalName] = user;
                }
            }

            return byUpn;
        }
    }
}
