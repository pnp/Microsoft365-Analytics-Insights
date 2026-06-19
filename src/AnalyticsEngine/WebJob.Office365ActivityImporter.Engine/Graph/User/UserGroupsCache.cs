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
    /// Entries are evicted in bulk after <see cref="CacheTtl"/> elapses to bound memory growth
    /// in long-running WebJob processes (group memberships rarely change inside a single import run).
    /// </summary>
    public abstract class UserGroupsCache
    {
        protected readonly ILogger _logger;
        private ConcurrentDictionary<string, List<string>> _userGroupsCache = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// How long cached group memberships are kept before the cache is bulk-cleared.
        /// One hour matches the typical import-cycle cadence and prevents unbounded growth
        /// in tenants with hundreds of thousands of unique UPNs. Virtual so tests can shorten it.
        /// </summary>
        protected internal virtual TimeSpan CacheTtl => TimeSpan.FromHours(1);

        private DateTime _lastClearedUtc = DateTime.UtcNow;
        private readonly object _evictionLock = new object();

        protected UserGroupsCache(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Test hook: count of entries currently in the cache. Internal so tests can
        /// verify eviction behaviour without exposing the underlying dictionary.
        /// </summary>
        internal int CachedEntryCount => _userGroupsCache.Count;

        private void EvictIfStale()
        {
            if (DateTime.UtcNow - _lastClearedUtc < CacheTtl) return;
            lock (_evictionLock)
            {
                if (DateTime.UtcNow - _lastClearedUtc < CacheTtl) return;
                var oldCount = _userGroupsCache.Count;
                _userGroupsCache = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                _lastClearedUtc = DateTime.UtcNow;
                if (oldCount > 0)
                {
                    _logger?.LogInformation($"UserGroupsCache TTL elapsed: cleared {oldCount} cached UPN entries.");
                }
            }
        }

        /// <summary>
        /// Loads the group display names for a user (by UPN) from cache or underlying loader.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetGroupsForUserAsync(string upn)
        {
            if (string.IsNullOrWhiteSpace(upn))
                throw new ArgumentNullException(nameof(upn));

            EvictIfStale();

            if (_userGroupsCache.TryGetValue(upn, out var cachedGroups))
                return cachedGroups;

            List<string> groups;
            try
            {
                groups = await LoadGroupsFromExternalAsync(upn);
            }
            catch (Exception ex)
            {
                // Do NOT cache a failed load. Caching an empty list here would persist for the whole
                // TTL and - because IsInGroupsFilter treats "no groups" as "matches the filter" - would
                // silently include this user for up to an hour on a single transient Graph error.
                // Returning an uncached empty list lets the next call retry the load.
                _logger?.LogError(ex, $"Failed to load groups for user {upn}. Returning an empty (uncached) list so the next call retries.");
                return new List<string>();
            }
            _userGroupsCache[upn] = groups;
            return groups;
        }

        /// <summary>
        /// Implement this to load group display names for a user from the desired source.
        /// </summary>
        protected abstract Task<List<string>> LoadGroupsFromExternalAsync(string upn);

        /// <summary>
        /// Returns true if any of the user's groups match the filter.
        /// </summary>
        public async Task<bool> IsInGroupsFilter(string upn, UserGroupsFilterModel filter)
        {
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));
            if (filter.Patterns.Count == 0)
            {
                return true; // No filter patterns - all groups match
            }

            var groupsForUser = await GetGroupsForUserAsync(upn);

            if (groupsForUser.Count == 0)
            {
                return true; // user has no groups - all groups match
            }
            return groupsForUser.Any(g => filter.Matches(g));
        }
    }
}
