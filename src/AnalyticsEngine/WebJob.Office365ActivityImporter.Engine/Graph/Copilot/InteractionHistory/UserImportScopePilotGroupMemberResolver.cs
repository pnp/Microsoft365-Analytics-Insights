using Common.Entities.Config;
using Common.Entities.UserScope;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// Supplies the member UPNs of the pilot group(s) named by <c>UserGroupsFilter</c>, plus whether that answer is
    /// complete. Comparison is case-insensitive so it lines up with SQL Server's default collation when the results
    /// are matched against the users table.
    /// </summary>
    public interface IPilotGroupMemberResolver
    {
        Task<PilotGroupResolution> GetMemberUpnsAsync(UserGroupsFilterModel filter);
    }

    /// <summary>
    /// Supplies the Copilot interaction-history pilot scope from the shared <c>UserGroupsFilter</c> scope, read
    /// <b>fail-closed</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other import applies the shared scope failing open: when the groups cannot be resolved they reuse the
    /// last successful resolution, and failing that import everyone. This one deliberately does not. Widening it
    /// would read every enabled user's prompt history and, where Cognitive Services is configured, send their prompt
    /// text to Azure AI Language - so it takes the current resolution exactly as it is: a partial member set with
    /// the reason recorded as an error when resolution stopped early, and nobody when it failed outright.
    /// </para>
    /// <para>
    /// Resolution itself (group-first, by object id / exact name / wildcard, under one call budget) is the shared
    /// <see cref="GroupMembershipResolver"/>; this adapter only chooses how to read it. The <c>filter</c> argument is
    /// the provider's own filter in production and is used only to recognise the unscoped and match-all cases.
    /// </para>
    /// </remarks>
    public class UserImportScopePilotGroupMemberResolver : IPilotGroupMemberResolver
    {
        internal const string MatchesEverythingReason =
            "UserGroupsFilter matches every group ('*'), which is not a pilot scope. Remove the filter to import " +
            "across the directory, or name the pilot group(s).";

        private readonly IUserImportScopeProvider _scopeProvider;

        public UserImportScopePilotGroupMemberResolver(IUserImportScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));
        }

        public async Task<PilotGroupResolution> GetMemberUpnsAsync(UserGroupsFilterModel filter)
        {
            var upns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (filter == null || filter.Patterns.Count == 0)
            {
                return new PilotGroupResolution(upns);
            }

            // A match-all filter is not a narrowing. The importer takes its unscoped path before asking; refusing
            // here as well means it can never become an expensive silent default (issue #297).
            if (filter.MatchesEverything)
            {
                return new PilotGroupResolution(upns, MatchesEverythingReason);
            }

            var resolution = await _scopeProvider.GetResolutionAsync();
            if (resolution.Members != null)
            {
                foreach (var upn in resolution.Members.UserPrincipalNames)
                {
                    upns.Add(upn);
                }
            }

            return resolution.Status == UserImportScopeStatus.Unavailable
                ? new PilotGroupResolution(upns, resolution.Reason)
                : new PilotGroupResolution(upns);
        }
    }
}
