using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.User
{
    /// <summary>
    /// Abstract cache for Entra ID group memberships for users by UPN.
    /// </summary>
    public abstract class UserGroupsCache
    {
        protected readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, List<string>> _userGroupsCache = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        protected UserGroupsCache(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Loads the group display names for a user (by UPN) from cache or underlying loader.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetGroupsForUserAsync(string upn)
        {
            if (string.IsNullOrWhiteSpace(upn))
                throw new ArgumentNullException(nameof(upn));

            if (_userGroupsCache.TryGetValue(upn, out var cachedGroups))
                return cachedGroups;

            var groups = await LoadGroupsFromGraphAsync(upn);
            _userGroupsCache[upn] = groups;
            return groups;
        }

        /// <summary>
        /// Implement this to load group display names for a user from the desired source.
        /// </summary>
        protected abstract Task<List<string>> LoadGroupsFromGraphAsync(string upn);

        /// <summary>
        /// Returns true if any of the user's groups match the filter.
        /// </summary>
        public async Task<bool> IsInGroupsFilter(string upn, UserGroupsFilterModel filter)
        {
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));
            var groups = await GetGroupsForUserAsync(upn);
            return groups.Any(g => filter.Matches(g));
        }
    }
}
