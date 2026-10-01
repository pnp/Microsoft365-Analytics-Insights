using Common.Entities.Config;
using DataUtils;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserScope
{
    /// <summary>
    /// Supplies the <c>UserGroupsFilter</c> scope to every import, resolving it at most once per refresh interval.
    /// </summary>
    public interface IUserImportScopeProvider
    {
        /// <summary>The configured filter.</summary>
        UserGroupsFilterModel Filter { get; }

        /// <summary>
        /// The scope an import should apply, <b>failing open</b>: the current resolution when it succeeded; the
        /// last successful one when a refresh failed; and everyone when nothing has resolved yet. Never throws.
        /// </summary>
        Task<UserImportScope> GetScopeAsync();

        /// <summary>
        /// The current resolution as it is - no last-known-good substitution and no widening - for an import that
        /// must <b>fail closed</b> (Copilot interaction history). Never throws.
        /// </summary>
        Task<UserImportScopeResolution> GetResolutionAsync();

        /// <summary>
        /// Resolves the filter now, ignoring any cached result, and makes that the current resolution. For an
        /// administrator asking to see the scope as it is, and for anything destructive that must never act on a
        /// stale answer. Never throws.
        /// </summary>
        Task<UserImportScopeResolution> RefreshAsync();
    }

    /// <summary>
    /// Process-lifetime <see cref="IUserImportScopeProvider"/>. Create one per web job and share it with every import
    /// in that process, so they all apply the same scope and the directory is read once, not once per import.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A successful resolution is reused for <see cref="DefaultRefreshInterval"/>, like the per-user group cache it
    /// replaces. A failed one is retried after <see cref="DefaultRetryInterval"/>, so a transient Graph error is not
    /// held for an hour but a missing permission is not retried on every call either. Refreshes are single-flight:
    /// concurrent callers wait for one resolution rather than each starting their own.
    /// </para>
    /// <para>
    /// The last successful resolution is kept for the life of the process, so a refresh that fails reuses the
    /// scope that was in force rather than widening to everyone. Only when nothing has resolved since the web job
    /// started does the fail-open view let everyone through - logged as an error on every refresh.
    /// </para>
    /// </remarks>
    public sealed class UserImportScopeProvider : IUserImportScopeProvider
    {
        public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromHours(1);
        public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMinutes(5);

        private readonly IGroupMembershipResolver _resolver;
        private readonly ILogger _logger;
        private readonly IClock _clock;
        private readonly TimeSpan _refreshInterval;
        private readonly TimeSpan _retryInterval;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private readonly string _filterText;

        private UserImportScopeResolution _current;
        private UserImportScopeResolution _lastKnownGood;

        public UserImportScopeProvider(UserGroupsFilterModel filter, IGroupMembershipResolver resolver, ILogger logger,
            IClock clock = null, TimeSpan? refreshInterval = null, TimeSpan? retryInterval = null)
        {
            Filter = filter ?? new UserGroupsFilterModel();
            _resolver = resolver;
            _logger = logger ?? NullLogger.Instance;
            _clock = clock ?? SystemClock.Instance;
            _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
            _retryInterval = retryInterval ?? DefaultRetryInterval;
            _filterText = string.Join(";", Filter.Patterns);

            if (Filter.IsNarrowing && _resolver == null)
            {
                throw new ArgumentNullException(nameof(resolver), "A narrowing UserGroupsFilter needs a resolver.");
            }
        }

        public UserGroupsFilterModel Filter { get; }

        /// <summary>A provider for which everyone is always in scope.</summary>
        public static UserImportScopeProvider Unfiltered() => new UserImportScopeProvider(new UserGroupsFilterModel(), null, null);

        /// <summary>
        /// The production provider: resolves <see cref="AppConfig.UserGroupsFilter"/> through Microsoft Graph with the
        /// runtime app registration. Builds no Graph client at all when the filter narrows nothing.
        /// </summary>
        public static UserImportScopeProvider CreateForGraph(AppConfig config, ILogger logger)
        {
            var filter = new UserGroupsFilterModel(config?.UserGroupsFilter);
            if (!filter.IsNarrowing)
            {
                return new UserImportScopeProvider(filter, null, logger);
            }

            IGroupMembershipResolver resolver;
            try
            {
                var auth = new GraphDirectoryOAuthContext(logger ?? NullLogger.Instance, config.ClientID, config.TenantGUID.ToString(),
                    config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);
                var httpClient = new ConfidentialClientApplicationThrottledHttpClient(auth, false, logger ?? NullLogger.Instance);
                resolver = new GroupMembershipResolver(new GraphGroupDirectoryReader(httpClient, logger), logger);
            }
            catch (Exception ex)
            {
                // Bad credentials configuration is reported through the normal failure path, so the imports fail
                // open (with an error each refresh) instead of the web job crashing at start-up.
                resolver = new FailedGroupMembershipResolver($"a Graph client could not be created ({ex.GetType().Name}: {ex.Message})");
            }

            return new UserImportScopeProvider(filter, resolver, logger);
        }

        public Task<UserImportScopeResolution> GetResolutionAsync() => ResolveAsync(forceRefresh: false);

        public Task<UserImportScopeResolution> RefreshAsync() => ResolveAsync(forceRefresh: true);

        private async Task<UserImportScopeResolution> ResolveAsync(bool forceRefresh)
        {
            if (!Filter.IsNarrowing)
            {
                return UserImportScopeResolution.Unfiltered(_clock.UtcNow);
            }

            var current = Volatile.Read(ref _current);
            if (!forceRefresh && IsFresh(current))
            {
                return current;
            }

            var requestedUtc = _clock.UtcNow;
            await _refreshLock.WaitAsync();
            try
            {
                current = Volatile.Read(ref _current);

                // A forced refresh is satisfied by one that completed while this caller waited for the lock, so
                // concurrent refresh requests still resolve the directory only once.
                if (forceRefresh ? current != null && current.ResolvedUtc >= requestedUtc : IsFresh(current))
                {
                    return current;
                }

                UserImportScopeResolution resolved;
                try
                {
                    resolved = await _resolver.ResolveAsync(Filter);
                }
                catch (Exception ex)
                {
                    resolved = UserImportScopeResolution.Unavailable(null, null, $"{ex.GetType().Name}: {ex.Message}", _clock.UtcNow);
                }

                if (resolved == null)
                {
                    resolved = UserImportScopeResolution.Unavailable(null, null, "the resolver returned nothing", _clock.UtcNow);
                }

                if (resolved.Status == UserImportScopeStatus.Resolved)
                {
                    Volatile.Write(ref _lastKnownGood, resolved);
                }
                Volatile.Write(ref _current, resolved);

                LogRefresh(resolved);
                return resolved;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        public async Task<UserImportScope> GetScopeAsync()
        {
            var resolution = await GetResolutionAsync();
            return ToFailOpenScope(resolution);
        }

        /// <summary>
        /// <see cref="GetScopeAsync"/>, plus one log line saying which users this cycle's imports cover. Call once at
        /// the start of each import cycle, so the scope - and in particular a filter that matches no group, or one
        /// that is failing open - is stated every cycle and not only when it is refreshed.
        /// </summary>
        public async Task<UserImportScope> GetScopeForCycleAsync()
        {
            var resolution = await GetResolutionAsync();
            var scope = ToFailOpenScope(resolution);

            var message = $"User import scope for this cycle: {scope.Description}.";
            if (resolution.Status == UserImportScopeStatus.Unavailable)
            {
                _logger.LogError(message);
            }
            else if (resolution.Status == UserImportScopeStatus.Resolved && resolution.Members.Count == 0)
            {
                _logger.LogWarning(message);
            }
            else
            {
                _logger.LogInformation(message);
            }

            return scope;
        }

        private UserImportScope ToFailOpenScope(UserImportScopeResolution resolution)
        {
            switch (resolution.Status)
            {
                case UserImportScopeStatus.Unfiltered:
                    return UserImportScope.Unfiltered;

                case UserImportScopeStatus.Resolved:
                    return UserImportScope.ForMembers(resolution.Members, DescribeResolved(resolution));

                default:
                    var lastKnownGood = Volatile.Read(ref _lastKnownGood);
                    if (lastKnownGood != null)
                    {
                        return UserImportScope.ForMembers(lastKnownGood.Members,
                            $"UserGroupsFilter '{_filterText}' could not be refreshed ({resolution.Reason}), so the " +
                            $"{lastKnownGood.Members.Count:N0} user(s) it last resolved to at {lastKnownGood.ResolvedUtc:u} are still in scope");
                    }

                    return UserImportScope.Everyone(
                        $"UserGroupsFilter '{_filterText}' could not be resolved ({resolution.Reason}), so it is failing open: " +
                        "EVERY user is in scope until it can be resolved. Copilot interaction history stays closed and imports nobody");
            }
        }

        private string DescribeResolved(UserImportScopeResolution resolution)
        {
            if (resolution.MatchedNoGroup)
            {
                return $"UserGroupsFilter '{_filterText}' matches no Entra ID group, so NOBODY is in scope and no user-level " +
                       "data is imported. Check the group name(s), or use group object ids";
            }

            if (resolution.Members.Count == 0)
            {
                return $"UserGroupsFilter '{_filterText}' matched {resolution.MatchedGroupCount:N0} group(s) with no user " +
                       "members, so NOBODY is in scope and no user-level data is imported. Only direct members count";
            }

            return $"UserGroupsFilter '{_filterText}' resolved to {resolution.Members.Count:N0} user(s), the direct members of " +
                   $"{resolution.MatchedGroupCount:N0} group(s), at {resolution.ResolvedUtc:u}; data about anyone else is not imported";
        }

        private bool IsFresh(UserImportScopeResolution resolution)
        {
            if (resolution == null)
            {
                return false;
            }

            var maxAge = resolution.Status == UserImportScopeStatus.Resolved ? _refreshInterval : _retryInterval;
            return _clock.UtcNow - resolution.ResolvedUtc < maxAge;
        }

        private void LogRefresh(UserImportScopeResolution resolution)
        {
            var scope = ToFailOpenScope(resolution);
            if (resolution.Status == UserImportScopeStatus.Unavailable)
            {
                _logger.LogError($"User import scope refresh failed: {scope.Description}. Retrying in {_retryInterval.TotalMinutes:N0} minute(s).");
            }
            else if (resolution.Members.Count == 0)
            {
                _logger.LogWarning($"User import scope refreshed: {scope.Description}.");
            }
            else
            {
                _logger.LogInformation($"User import scope refreshed: {scope.Description}.");
            }
        }

        /// <summary>Reports every resolution as unavailable - used when no Graph client could be built.</summary>
        private sealed class FailedGroupMembershipResolver : IGroupMembershipResolver
        {
            private readonly string _reason;

            public FailedGroupMembershipResolver(string reason)
            {
                _reason = reason;
            }

            public Task<UserImportScopeResolution> ResolveAsync(UserGroupsFilterModel filter)
                => Task.FromResult(UserImportScopeResolution.Unavailable(null, null, _reason, DateTime.UtcNow,
                    UserImportScopeFailureKind.ClientUnavailable));
        }
    }

    /// <summary>App-only Graph token context for resolving groups, available to every web job.</summary>
    public class GraphDirectoryOAuthContext : ImportAppIndentityOAuthContext
    {
        public GraphDirectoryOAuthContext(ILogger logger, string clientId, string tenantId, string clientSecret, string keyVaultUrl, bool useClientCertificate)
            : base(logger, clientId, tenantId, clientSecret, keyVaultUrl, useClientCertificate)
        {
        }

        public override string ResourceURL => "https://graph.microsoft.com/.default";
    }
}
