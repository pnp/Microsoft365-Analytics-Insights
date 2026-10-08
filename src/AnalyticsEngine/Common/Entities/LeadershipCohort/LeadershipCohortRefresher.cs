using Common.Entities.Config;
using Common.Entities.UserScope;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.LeadershipCohort
{
    /// <summary>Maps Entra object ids to <c>dbo.users.id</c>.</summary>
    public interface ILeadershipCohortUserIdMapper
    {
        /// <summary>The SQL user id of every object id the database knows. Unknown ids are simply absent.</summary>
        Task<IReadOnlyList<int>> MapAsync(IReadOnlyList<string> objectIds);
    }

    /// <summary>
    /// Matches on <c>dbo.users.azure_ad_id</c>, which the Graph user import fills - the same join the agent-cost import
    /// uses (<c>SqlAgentCostUserLinkStore</c>), so it needs no schema change.
    /// </summary>
    /// <remarks>
    /// <c>azure_ad_id</c> is <c>nvarchar(max)</c> and so cannot be indexed: each chunk of <see cref="ChunkSize"/> ids is one
    /// scan of <c>dbo.users</c>. That is why this runs at refresh time (every <see cref="LeadershipCohortRefresher.RefreshAfterSuccess"/>)
    /// and never per request: a 50-person cohort costs one scan, the <see cref="LeadershipCohortStore.MaxMembers"/> ceiling
    /// ten - about two seconds against a 200,000-user directory.
    /// </remarks>
    public sealed class SqlLeadershipCohortUserIdMapper : ILeadershipCohortUserIdMapper
    {
        /// <summary>Ids per query. EF6 inlines a local list's values, so this is a statement-size bound, not SQL's 2,100-parameter one.</summary>
        public const int ChunkSize = 1000;

        private readonly Func<AnalyticsEntitiesContext> _createContext;

        public SqlLeadershipCohortUserIdMapper(Func<AnalyticsEntitiesContext> createContext = null)
        {
            _createContext = createContext ?? (() => new AnalyticsEntitiesContext());
        }

        public async Task<IReadOnlyList<int>> MapAsync(IReadOnlyList<string> objectIds)
        {
            var result = new HashSet<int>();
            var distinct = (objectIds ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct.Count == 0) return new List<int>();

            using (var db = _createContext())
            {
                // GetRange, not Skip/Take, and no ToLower(): the default collation is already case-insensitive.
                for (var i = 0; i < distinct.Count; i += ChunkSize)
                {
                    var chunk = distinct.GetRange(i, Math.Min(ChunkSize, distinct.Count - i));
                    var ids = await db.users
                        .Where(u => chunk.Contains(u.AzureAdId))
                        .Select(u => u.ID)
                        .ToListAsync()
                        .ConfigureAwait(false);
                    foreach (var id in ids) result.Add(id);
                }
            }
            return result.ToList();
        }
    }

    /// <summary>
    /// Reads the configured leadership group's direct user members from Microsoft Graph, matches them to the analytics
    /// database's users and stores them (#654). Runs from the importer's cycle when due, and when an administrator asks.
    /// </summary>
    /// <remarks>
    /// <para><b>Bounded.</b> Direct members only (<c>/groups/{id}/members/microsoft.graph.user</c>, 999 per page), no nested
    /// expansion and no manager hierarchy, and at most <see cref="LeadershipCohortStore.MaxMembers"/> of them: about eleven
    /// Graph calls, however large the tenant. A larger group is refused as <see cref="LeadershipCohortRefreshStatuses.TooLarge"/>
    /// rather than truncated.</para>
    /// <para><b>Honest.</b> Every outcome is recorded in the snapshot header. A failure replaces the previous header, so the
    /// report says the membership could not be refreshed instead of quietly comparing against the last good list.</para>
    /// <para><b>Single-flight</b> per process: a refresh requested while one is running returns null.</para>
    /// </remarks>
    public sealed class LeadershipCohortRefresher
    {
        /// <summary>A successful refresh is repeated after this long.</summary>
        public static readonly TimeSpan RefreshAfterSuccess = TimeSpan.FromHours(6);

        /// <summary>A failed refresh is retried after this long, so a missing permission is not hammered every cycle.</summary>
        public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

        private static readonly SemaphoreSlim ProcessGate = new SemaphoreSlim(1, 1);

        private readonly LeadershipCohortStore _store;
        private readonly Func<IGroupDirectoryReader> _directoryFactory;
        private readonly ILeadershipCohortUserIdMapper _mapper;
        private readonly ILogger _logger;
        private readonly Func<DateTime> _utcNow;
        private readonly int _maxMembers;
        private readonly SemaphoreSlim _gate;

        public LeadershipCohortRefresher(
            LeadershipCohortStore store,
            Func<IGroupDirectoryReader> directoryFactory,
            ILeadershipCohortUserIdMapper mapper,
            ILogger logger = null,
            Func<DateTime> utcNow = null,
            int maxMembers = LeadershipCohortStore.MaxMembers,
            SemaphoreSlim gate = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _directoryFactory = directoryFactory ?? throw new ArgumentNullException(nameof(directoryFactory));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
            _logger = logger ?? NullLogger.Instance;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _maxMembers = Math.Max(1, Math.Min(maxMembers, LeadershipCohortStore.MaxMembers));
            _gate = gate ?? ProcessGate;
        }

        /// <summary>A refresher that reads Graph as the runtime app (the same credentials the user import uses).</summary>
        public static LeadershipCohortRefresher ForGraph(LeadershipCohortStore store, AppConfig config, ILogger logger)
        {
            return new LeadershipCohortRefresher(
                store,
                () =>
                {
                    var auth = new GraphDirectoryOAuthContext(logger ?? NullLogger.Instance, config.ClientID, config.TenantGUID.ToString(),
                        config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);
                    var httpClient = new ConfidentialClientApplicationThrottledHttpClient(auth, false, logger ?? NullLogger.Instance);
                    return new GraphGroupDirectoryReader(httpClient, logger);
                },
                new SqlLeadershipCohortUserIdMapper(),
                logger);
        }

        /// <summary>Whether a refresh is due for these settings, given the last snapshot.</summary>
        public static bool IsDue(LeadershipCohortSettings settings, LeadershipCohortSnapshot snapshot, DateTime utcNow)
        {
            if (settings == null) return false;
            if (snapshot == null || !string.Equals(snapshot.SettingsRevision, settings.Revision, StringComparison.Ordinal)) return true;
            var after = snapshot.IsReady ? RefreshAfterSuccess : RetryAfterFailure;
            return utcNow - snapshot.AttemptedUtc >= after;
        }

        /// <summary>Refreshes when <see cref="IsDue"/>. Null when not configured, not due, or another refresh is running.</summary>
        public async Task<LeadershipCohortSnapshot> RefreshIfDueAsync()
        {
            var settings = await _store.GetSettingsAsync().ConfigureAwait(false);
            if (settings == null) return null;
            var snapshot = await _store.GetSnapshotAsync().ConfigureAwait(false);
            if (!IsDue(settings, snapshot, _utcNow())) return null;
            return await RefreshAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Refreshes now and returns the header it saved. Null when nothing is configured or another refresh is running.
        /// Throws <see cref="LeadershipCohortStateUnavailableException"/> only when the outcome itself cannot be stored.
        /// </summary>
        public async Task<LeadershipCohortSnapshot> RefreshAsync()
        {
            if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return null;
            try
            {
                var settings = await _store.GetSettingsAsync().ConfigureAwait(false);
                if (settings == null || !Guid.TryParse(settings.GroupId, out var groupId)) return null;

                var snapshot = new LeadershipCohortSnapshot
                {
                    Version = Guid.NewGuid().ToString("N"),
                    GroupId = groupId.ToString("D"),
                    SettingsRevision = settings.Revision,
                    AttemptedUtc = _utcNow(),
                };

                var userIds = await ReadMembersAsync(snapshot, groupId).ConfigureAwait(false);
                if (userIds == null)
                {
                    await _store.SaveFailureAsync(snapshot).ConfigureAwait(false);
                    _logger.LogWarning($"Leadership cohort refresh did not complete: {snapshot.Status}"
                        + (snapshot.FailureKind != null ? $" ({snapshot.FailureKind})" : string.Empty)
                        + (snapshot.HttpStatus.HasValue ? $", Graph HTTP {snapshot.HttpStatus}" : string.Empty)
                        + ". The leadership comparison is unavailable until a refresh succeeds.");
                    return snapshot;
                }

                await _store.SaveMembersAsync(snapshot, userIds).ConfigureAwait(false);
                _logger.LogInformation($"Leadership cohort refreshed: {snapshot.DirectMembers} direct member(s), "
                    + $"{snapshot.MatchedUsers} matched to imported users, {snapshot.GraphPages} Graph page(s).");
                return snapshot;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The members' SQL ids, or null with the snapshot's status and failure filled in.</summary>
        private async Task<IReadOnlyList<int>> ReadMembersAsync(LeadershipCohortSnapshot snapshot, Guid groupId)
        {
            IGroupDirectoryReader directory;
            try
            {
                directory = _directoryFactory();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Leadership cohort: a Graph client could not be created ({ex.GetType().Name}).");
                return Fail(snapshot, LeadershipCohortRefreshStatuses.Failed, LeadershipCohortFailureKinds.GraphClient, null);
            }

            var objectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var group = await directory.GetGroupByIdAsync(groupId).ConfigureAwait(false);
                if (group == null) return Fail(snapshot, LeadershipCohortRefreshStatuses.GroupNotFound, null, (int)HttpStatusCode.NotFound);
                snapshot.GroupDisplayName = group.DisplayName;

                // One page more than the cap can need, so a group exactly at the cap is accepted and anything beyond it
                // is detected without reading the rest of a huge group.
                var maxGraphPages = (_maxMembers + GraphGroupDirectoryReader.PageSize - 1) / GraphGroupDirectoryReader.PageSize + 1;
                string next = null;
                do
                {
                    var page = await directory.ListUserMembersAsync(snapshot.GroupId, next).ConfigureAwait(false);
                    snapshot.GraphPages++;
                    foreach (var user in page?.Items ?? new List<DirectoryUser>())
                    {
                        if (!string.IsNullOrWhiteSpace(user?.Id)) objectIds.Add(user.Id.Trim());
                    }
                    if (objectIds.Count > _maxMembers) return TooLarge(snapshot, objectIds.Count);
                    next = page?.NextLink;
                    if (!string.IsNullOrEmpty(next) && snapshot.GraphPages >= maxGraphPages) return TooLarge(snapshot, objectIds.Count);
                }
                while (!string.IsNullOrEmpty(next));
            }
            catch (DirectoryReadException ex)
            {
                var code = (int)ex.StatusCode;
                if (ex.StatusCode == HttpStatusCode.Forbidden || ex.StatusCode == HttpStatusCode.Unauthorized)
                    return Fail(snapshot, LeadershipCohortRefreshStatuses.PermissionMissing, null, code);
                if (ex.StatusCode == HttpStatusCode.NotFound)
                    return Fail(snapshot, LeadershipCohortRefreshStatuses.GroupNotFound, null, code);
                return Fail(snapshot, LeadershipCohortRefreshStatuses.Failed, LeadershipCohortFailureKinds.GraphError, code);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Leadership cohort: reading the group from Graph failed ({ex.GetType().Name}).");
                return Fail(snapshot, LeadershipCohortRefreshStatuses.Failed, LeadershipCohortFailureKinds.GraphClient, null);
            }

            snapshot.DirectMembers = objectIds.Count;
            IReadOnlyList<int> userIds;
            try
            {
                userIds = await _mapper.MapAsync(objectIds.ToList()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Leadership cohort: matching members to imported users failed ({ex.GetType().Name}).");
                return Fail(snapshot, LeadershipCohortRefreshStatuses.Failed, LeadershipCohortFailureKinds.SqlError, null);
            }

            snapshot.MatchedUsers = userIds.Count;
            snapshot.Status = LeadershipCohortRefreshStatuses.Ready;
            snapshot.RefreshedUtc = snapshot.AttemptedUtc;
            return userIds;
        }

        private static IReadOnlyList<int> TooLarge(LeadershipCohortSnapshot snapshot, int seen)
        {
            // "More than the cap" is all that is known; the count read so far is a floor, recorded as such.
            snapshot.DirectMembers = seen;
            return Fail(snapshot, LeadershipCohortRefreshStatuses.TooLarge, null, null);
        }

        private static IReadOnlyList<int> Fail(LeadershipCohortSnapshot snapshot, string status, string failureKind, int? httpStatus)
        {
            snapshot.Status = status;
            snapshot.FailureKind = failureKind;
            snapshot.HttpStatus = httpStatus;
            snapshot.RefreshedUtc = null;
            snapshot.MatchedUsers = 0;
            return null;
        }
    }
}
