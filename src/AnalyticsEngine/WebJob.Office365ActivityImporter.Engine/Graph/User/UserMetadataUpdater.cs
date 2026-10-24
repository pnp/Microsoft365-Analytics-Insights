using Azure.Core;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.LookupCaches;
using Common.Entities.State;
using Common.Entities.UserDiagnostics;
using Common.Entities.UserOrgs;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Ensures user table info is upto-date from Graph
    /// </summary>
    public class UserMetadataUpdater : AbstractApiLoader
    {
        #region Constructor & Privates

        private UserMetadataCache _userMetaCache;
        private readonly IUserMetadataLoader _userLoader;
        private readonly IAnalyticsDbContextFactory _contextFactory;
        private readonly IClock _clock;
        private UserBatchProcessor _batchProcessor;
        private UserInsertProcessor _insertProcessor;
        private UserLicenseProcessor _licenseProcessor;
        private UserDataMapper _dataMapper;
        private IUserImportScopeProvider _userScopeProvider;
        private IImportLastRunStore _lastRunStore;
        private IUserImportScopeMarkerStore _scopeMarkerStore;

        /// <summary>
        /// Cadence key for the full re-read that picks up people who joined the <c>UserGroupsFilter</c> groups after
        /// the directory was last read in full. Stored in the process-lifetime last-run store, like the section gates.
        /// </summary>
        public const string ScopeCatchUpLastRunKey = "UserImportScopeCatchUpLastRun";

        /// <summary>At most one scope catch-up re-read per this many hours.</summary>
        public const int ScopeCatchUpIntervalHours = 24;

        private enum ScopeCatchUpDecision
        {
            None,
            FullReadAlreadyPending,
            MembershipCatchUpRequested,
        }

        /// <summary>
        /// Reads the admin-configured org types. Injectable so the org step can be exercised without a
        /// database; built from the configured SQL connection string otherwise.
        /// </summary>
        private readonly IUserOrgTypeStore _orgTypeStore;

        /// <summary>Writes the resolved org values. Injectable for the same reason.</summary>
        private readonly IUserOrgAssignmentStore _orgAssignmentStore;

        /// <param name="userScopeProvider">
        /// The process-lifetime <c>UserGroupsFilter</c> scope: only people in it are written to the users table.
        /// Null means unfiltered.
        /// </param>
        /// <param name="lastRunStore">
        /// Process-lifetime store that rate-limits the scope catch-up re-read. Without one no catch-up is attempted.
        /// </param>
        public UserMetadataUpdater(AnalyticsLogger logger, AppConfig settings, TokenCredential creds, ManualGraphCallClient manualGraphCallClient, IClock clock = null,
            IUserImportScopeProvider userScopeProvider = null, IImportLastRunStore lastRunStore = null)
            : base(logger, settings)
        {
            _clock = clock ?? SystemClock.Instance;
            _userScopeProvider = userScopeProvider;
            _lastRunStore = lastRunStore;
            IDeltaValueProvider deltaProvider = null;
            var deltaTokenStore = StateStore.TryOpen(settings, StatePartitions.UserImport, logger);
            IdentityQuarantineStore = new UserIdentityQuarantineStore(
                StateStore.TryOpen(settings, StatePartitions.UserIdentityQuarantine, logger) ?? ProcessIdentityQuarantineFallback,
                settings.TenantGUID);
            if (deltaTokenStore != null)
            {
                deltaProvider = new PersistedDeltaValueProvider(settings, logger, deltaTokenStore);
                _scopeMarkerStore = new PersistedUserImportScopeMarkerStore(deltaTokenStore, settings.TenantGUID);
                logger.LogInformation($"User import - persisting the delta token in {deltaTokenStore.Description}.");
            }
            else
            {
                logger.LogInformation($"User import - no Storage connection string configured, using in-process cache for delta token.");
                deltaProvider = new InProcessDeltaValueProvider(logger);
            }

            // v4 used graphClient.HttpProvider.OverallTimeout = 1h directly. HttpProvider is gone
            // in v5+, so we build a HttpClient that keeps Kiota's service-directed Retry-After
            // handling but caps any single stalled HTTP request. /users/delta over a 200k-user
            // tenant can legitimately take longer than the framework default 100s across all pages;
            // the budget is therefore per request, not per full tenant traversal.
            var graphServiceClient = GraphServiceClientFactory.CreateForUserImport(creds, _logger);

            _userLoader = new GraphUserLoader(manualGraphCallClient, deltaProvider, _logger, graphServiceClient);
            _contextFactory = DefaultAnalyticsDbContextFactory.Instance;
            _orgTypeStore = CreateOrgTypeStore(settings, logger);
            _orgAssignmentStore = CreateOrgAssignmentStore(settings, logger);
            InitializeHelpers();
        }

        /// <summary>
        /// Constructor with injectable user loader for testing and alternate implementations
        /// </summary>
        public UserMetadataUpdater(AnalyticsLogger logger, AppConfig settings, IUserMetadataLoader userLoader, IClock clock = null)
            : this(logger, settings, userLoader, DefaultAnalyticsDbContextFactory.Instance, clock)
        {
        }

        /// <summary>
        /// Constructor with an injectable user loader and database context factory (#372).
        /// </summary>
        public UserMetadataUpdater(
            AnalyticsLogger logger,
            AppConfig settings,
            IUserMetadataLoader userLoader,
            IAnalyticsDbContextFactory contextFactory,
            IClock clock = null,
            IUserOrgTypeStore orgTypeStore = null,
            IUserOrgAssignmentStore orgAssignmentStore = null)
            : base(logger, settings)
        {
            _userLoader = userLoader;
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            _clock = clock ?? SystemClock.Instance;
            _orgTypeStore = orgTypeStore ?? CreateOrgTypeStore(settings, logger);
            _orgAssignmentStore = orgAssignmentStore ?? CreateOrgAssignmentStore(settings, logger);
            IdentityQuarantineStore = new UserIdentityQuarantineStore(new InMemoryKeyValueStore(), Guid.Empty);
            InitializeHelpers();
        }

        /// <summary>
        /// The quarantine when no Storage connection string is configured: once per process, so quarantined users are
        /// still carried from one cycle to the next while the process lives. The updater itself is rebuilt every cycle.
        /// </summary>
        private static readonly InMemoryKeyValueStore ProcessIdentityQuarantineFallback = new InMemoryKeyValueStore();

        /// <summary>
        /// Where users quarantined for an identity conflict are kept between cycles. The production constructor opens
        /// <see cref="StatePartitions.UserIdentityQuarantine"/>; the others start with an empty store of their own, so tests
        /// share one only when they set it.
        /// </summary>
        internal IUserIdentityQuarantineStore IdentityQuarantineStore { get; set; }

        /// <summary>
        /// Builds the org type store, or returns <c>null</c> when no SQL connection string is
        /// configured. Null means "user orgs are not available", which the import treats as "no org
        /// types configured" rather than as an error - the feature is optional and must never be able
        /// to stop the user import.
        /// </summary>
        private static IUserOrgTypeStore CreateOrgTypeStore(AppConfig settings, AnalyticsLogger logger)
        {
            var connectionString = settings?.ConnectionStrings?.SQL;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            try
            {
                return UserOrgStores.CreateTypeStore(connectionString);
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"User import - could not prepare the user organisation store: {ex.Message}. Organisation values will not be imported.");
                return null;
            }
        }

        private static IUserOrgAssignmentStore CreateOrgAssignmentStore(AppConfig settings, AnalyticsLogger logger)
        {
            var connectionString = settings?.ConnectionStrings?.SQL;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            try
            {
                return UserOrgStores.CreateAssignmentStore(connectionString);
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"User import - could not prepare the user organisation store: {ex.Message}. Organisation values will not be imported.");
                return null;
            }
        }

        private void InitializeHelpers()
        {
            _batchProcessor = new UserBatchProcessor(_logger, _clock);
            _insertProcessor = new UserInsertProcessor(_logger, _batchProcessor);
        }

        public IUserMetadataLoader UserLoader => _userLoader;

        /// <summary>
        /// Applies <c>UserGroupsFilter</c> to this updater: only people in the scope are written, and people who
        /// joined its groups after the directory was last read in full are caught up by an occasional full re-read.
        /// </summary>
        /// <param name="scopeMarkerStore">
        /// Where the filter the stored delta token was taken under is recorded, so a changed filter triggers a full
        /// read. Null when the token does not outlive the import (see <see cref="PersistedUserImportScopeMarkerStore"/>).
        /// </param>
        public UserMetadataUpdater WithUserScope(IUserImportScopeProvider userScopeProvider, IImportLastRunStore lastRunStore,
            IUserImportScopeMarkerStore scopeMarkerStore = null)
        {
            _userScopeProvider = userScopeProvider;
            _lastRunStore = lastRunStore;
            _scopeMarkerStore = scopeMarkerStore;
            return this;
        }

        #endregion

        /// <summary>
        /// Main method
        /// </summary>
        /// <returns>
        /// True when the cycle completed and the new Graph delta token was committed. False when it did not -
        /// above all when the <c>/users/delta</c> read stopped before its <c>@odata.deltaLink</c> (issue #664) -
        /// so the import section is not recorded as done and is retried on the next cycle. A failure in any
        /// database phase still throws, exactly as before.
        /// </returns>
        public async Task<bool> InsertAndUpdateDatabaseFromExternalUsers()
        {
            // The users-and-licences writer lock: held from the first Graph read to the last write, so a one-person
            // refresh from the portal can never land between this run's reading and its writing and then be
            // overwritten by an older answer. See DirectoryWriterLock.
            using (var writerLock = await AcquireWriterLockAsync())
            {
                if (writerLock == NotAcquired)
                {
                    _logger.LogWarning("User import - another writer of users and licences (a one-person refresh from the portal, or this import " +
                        $"on another instance) held the users-and-licences lock for {WriterLockWait.TotalMinutes:N0} minute(s). Skipping this cycle's user import; " +
                        "it is retried on the next cycle.");
                    return false;
                }

                _refreshStore = UserRefreshStore.TryOpen(_settings, _logger);
                _writeFence = writerLock?.Lock == null
                    ? (Func<Task<bool>>)(() => Task.FromResult(true))
                    : () => writerLock.Lock.IsStillHeldAsync();

                var mayHaveMarked = false;
                if (writerLock.Lock != null)
                {
                    // One-person refreshes also wait on this marker, so that even if the lock's connection drops part-way,
                    // none runs until this run has stopped writing. Kept alive with the lock's own heartbeat, which also
                    // stops the hour-long Graph reads leaving the lock's connection idle long enough to be closed.
                    await TryMarkScheduledImportAsync();
                    mayHaveMarked = _refreshStore != null;
                    var store = _refreshStore;
                    writerLock.Lock.StartKeepAlive(DirectoryWriterLock.DefaultKeepAliveInterval,
                        store == null ? null : (Func<Task>)(() => store.MarkScheduledImportActiveAsync()));
                }

                try
                {
                    return await InsertAndUpdateDatabaseFromExternalUsersCore();
                }
                finally
                {
                    // Stop the heartbeat first - including a beat already in flight - or it could write the marker back
                    // just after it is cleared. Cleared even when the first mark failed: a later beat may have set it.
                    var heartbeatStopped = writerLock.Lock?.StopKeepAlive() ?? true;
                    if (mayHaveMarked)
                    {
                        if (!heartbeatStopped)
                        {
                            _logger.LogInformation($"User import - a 'user import running' heartbeat to Azure Table storage was still in progress after the import finished; if it completes it holds one-person refreshes back for up to {UserRefreshStore.ScheduledImportMarkerLifetime.TotalMinutes:N0} minutes.");
                        }

                        try
                        {
                            await _refreshStore.ClearScheduledImportActiveAsync();
                        }
                        catch (Exception ex)
                        {
                            // It expires by itself within UserRefreshStore.ScheduledImportMarkerLifetime.
                            _logger.LogInformation($"User import - couldn't clear the 'user import running' marker ({ex.GetType().Name}); one-person refreshes wait until it expires.");
                        }
                    }
                }
            }
        }

        private async Task TryMarkScheduledImportAsync()
        {
            if (_refreshStore == null) return;
            try
            {
                await _refreshStore.MarkScheduledImportActiveAsync();
            }
            catch (Exception ex)
            {
                // Without the marker the SQL lock still orders the writers; only the dropped-connection case is less
                // covered until the next heartbeat sets it.
                _logger.LogInformation($"User import - couldn't set the 'user import running' marker ({ex.GetType().Name}); continuing.");
            }
        }

        private UserRefreshStore _refreshStore;

        /// <summary>How long the scheduled import waits for a one-person refresh to finish before skipping the cycle.</summary>
        public static readonly TimeSpan WriterLockWait = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Takes the users-and-licences writer lock. Overridable by tests; null means "no lock" (no SQL connection string,
        /// as in some unit tests), which keeps the import's behaviour exactly as it was.
        /// </summary>
        public Func<Task<DirectoryWriterLock>> WriterLockFactory { get; set; }

        /// <summary>
        /// Builds the writer that marks rows disabled for accounts Graph reported disabled or deleted, from the import's
        /// connection string. Overridable by tests.
        /// </summary>
        internal Func<string, IUserAccountStatusWriter> AccountStatusWriterFactory { get; set; } =
            connectionString => new SqlUserAccountStatusWriter(connectionString);

        private sealed class HeldLock : IDisposable
        {
            public HeldLock(DirectoryWriterLock writerLock) { Lock = writerLock; }
            public DirectoryWriterLock Lock { get; }
            public void Dispose() => Lock?.Dispose();
        }

        private static readonly HeldLock NotAcquired = new HeldLock(null);

        private Func<Task<bool>> _writeFence = () => Task.FromResult(true);

        /// <summary>The lock check the licence processor of the last run used. For tests of its wiring.</summary>
        internal Func<Task<bool>> LicenceWriteFence => _licenseProcessor?.WriteFence;

        private async Task<HeldLock> AcquireWriterLockAsync()
        {
            var factory = WriterLockFactory;
            if (factory == null)
            {
                var connectionString = _settings?.ConnectionStrings?.DatabaseConnectionString;
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    return new HeldLock(null);
                }
                factory = () => DirectoryWriterLock.TryAcquireAsync(connectionString, WriterLockWait);
            }

            var acquired = await factory();
            return acquired == null ? NotAcquired : new HeldLock(acquired);
        }

        /// <summary>
        /// Throws when the writer lock was lost (its SQL connection dropped) since the run's reading began: someone else
        /// may have written since, and this run's older answer must not overwrite theirs.
        /// </summary>
        private async Task EnsureWriterLockHeldAsync(string phase)
        {
            if (!await _writeFence())
            {
                throw new InvalidOperationException(
                    $"User import - lost the users-and-licences writer lock before the {phase}; not writing an answer that may now be out of date. The run is retried on the next cycle.");
            }
        }

        /// <summary>
        /// Graph's count of the directory's users, or null when it could not be read. Never throws: it only decides how
        /// much a complete read is trusted, so a failure falls back to the safer check rather than failing the import.
        /// </summary>
        private async Task<int?> CountDirectoryUsersAsync()
        {
            try
            {
                return await _userLoader.CountDirectoryUsersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"User import - couldn't read Graph's count of the directory's users: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Marks rows disabled for the accounts Graph reported disabled or deleted, then - after a complete read of the
        /// directory that <see cref="DirectoryReadCompleteness"/> trusts - for the accounts that read did not list. A
        /// failed write throws, which withholds the delta token.
        /// </summary>
        /// <param name="usersInDirectory">Graph's count of the directory's users, or null when it could not be read.</param>
        private async Task MarkRowsDisabledAsync(AnalyticsEntitiesContext db, UserRowsToDeactivate rows, GraphUserDeactivations deactivations,
            int? usersInDirectory, DateTime lastUpdatedUtc)
        {
            IUserAccountStatusWriter writer = null;
            if (rows.Reported.Count > 0)
            {
                await EnsureWriterLockHeldAsync("account status update");
                writer = AccountStatusWriterFactory(db.Database.Connection.ConnectionString);
                var marked = await writer.MarkDisabledAsync(rows.Reported, lastUpdatedUtc);
                _logger.LogInformation($"User import - marked {marked:N0} users row(s) disabled for the {deactivations.DisabledObjectIds.Count:N0} disabled and " +
                    $"{deactivations.RemovedObjectIds.Count:N0} deleted account(s) Graph reported.");
            }

            if (rows.NoLongerInDirectory.Count == 0)
            {
                return;
            }

            var leftOut = rows.NoLongerInDirectory.Count;
            var read = deactivations.CompleteReadCount;
            var verdict = DirectoryReadCompleteness.Judge(read, usersInDirectory, leftOut, rows.ConfirmedByCompleteRead);
            if (!DirectoryReadCompleteness.IsTrusted(verdict))
            {
                string why;
                switch (verdict)
                {
                    case DirectoryReadVerdict.ShortOfDirectoryCount:
                        why = $"it listed {read:N0} users, but Graph counts {usersInDirectory:N0} in the directory, so it may have missed some. " +
                            "Graph's count can lag behind a large deletion for a while";
                        break;
                    case DirectoryReadVerdict.BeyondDirectoryCount:
                        why = $"it listed {read:N0} users, but Graph counts only {usersInDirectory:N0} in the directory - too far apart for either to be relied on";
                        break;
                    case DirectoryReadVerdict.LeftOutMoreThanConfirmed:
                        why = $"Graph's count of the directory's users could not be read to confirm the read was complete, and the read left out more of the " +
                            $"users table's accounts ({leftOut:N0}) than it listed ({rows.ConfirmedByCompleteRead:N0}), as a read that lost part of the directory would";
                        break;
                    default:
                        why = "it listed no users at all";
                        break;
                }

                _logger.LogWarning($"User import - NOT marking {leftOut:N0} account(s) disabled although this complete read of the directory did not list them: {why}. " +
                    "They are checked again on the next complete read (clearing the user checkpoint on the User import page forces one). " +
                    "Accounts Graph reports as disabled or deleted are still marked as usual.");
                return;
            }

            await EnsureWriterLockHeldAsync("account status update");
            writer = writer ?? AccountStatusWriterFactory(db.Database.Connection.ConnectionString);
            var gone = await writer.MarkDisabledAsync(rows.NoLongerInDirectory, lastUpdatedUtc);
            var evidence = verdict == DirectoryReadVerdict.MatchesDirectoryCount
                ? $"this complete read listed {read:N0} users, in line with Graph's count of {usersInDirectory:N0}, and not them"
                : $"this complete read listed {read:N0} users and not them; Graph's count of the directory's users could not be read, but the read listed " +
                    $"{rows.ConfirmedByCompleteRead:N0} of the users table's accounts, more than it left out";
            _logger.LogInformation($"User import - marked {gone:N0} users row(s) disabled whose account is no longer in Entra ID: {evidence}, so they have been deleted. " +
                "The first complete read after upgrading to this version also catches up every account deleted before it, so expect a one-off drop in the " +
                "number of enabled users of about this many.");
        }

        /// <summary>Checks the writer lock before each batch of the bulk metadata update, not just once for the phase.</summary>
        private sealed class FencedUserBulkUpdateWriter : IUserBulkUpdateWriter
        {
            private readonly IUserBulkUpdateWriter _inner;
            private readonly Func<Task> _fence;

            public FencedUserBulkUpdateWriter(IUserBulkUpdateWriter inner, Func<Task> fence)
            {
                _inner = inner;
                _fence = fence;
            }

            public async Task ExecuteAsync(System.Data.DataTable userUpdates)
            {
                await _fence();
                await _inner.ExecuteAsync(userUpdates);
            }
        }

        private async Task<bool> InsertAndUpdateDatabaseFromExternalUsersCore()
        {
            const int BATCH_SIZE = 500;
            var phaseResults = new UserImportPhaseResults();

            using (var db = _contextFactory.Create())
            {
                db.Configuration.AutoDetectChangesEnabled = false;

                _userMetaCache = new UserMetadataCache(db);
                _licenseProcessor = new UserLicenseProcessor(_logger, _userLoader, _userMetaCache)
                {
                    // Every licence write path - the tenant SKU refresh and the per-user fallback - checks the
                    // users-and-licences lock before each chunk. Set here, once, so no path can miss it.
                    WriteFence = () => _writeFence(),
                };
                var refreshStore = _refreshStore;
                if (refreshStore != null)
                {
                    // Reports key their caches on this generation, so a licence change reaches them without waiting out the cache.
                    _licenseProcessor.LicencesChanged = () => refreshStore.BumpLicenceDataGenerationAsync();
                }
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, new SqlUserLookupStore(db), _clock);
                var importCycleLastUpdatedUtc = _clock.UtcNow;

                // UserGroupsFilter: only people in the scope are written to the users table - their metadata, manager
                // link and licences. Everyone else is left out entirely.
                var userScope = _userScopeProvider == null ? UserImportScope.Unfiltered : await _userScopeProvider.GetScopeAsync();
                _dataMapper.UserScope = userScope;

                _logger.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - start");

                // Work out which user-org attributes to ask Graph for, BEFORE the load. The selection
                // also qualifies the delta-token cache key, so a change to the configured attributes
                // discards the stored token and the next cycle re-enumerates every user once - which is
                // the only thing that populates a newly selected property for users who have not
                // otherwise changed. See GraphUserOrgSelection.
                var entraOrgTypes = await LoadEnabledEntraOrgTypes();
                var orgSelection = GraphUserOrgSelection.FromTypes(entraOrgTypes);
                _userLoader.SetOrgSelection(orgSelection);

                // If we have no active users, assume new install so clear delta key - after the selection,
                // which decides the key, and every key this cycle might read: a cache kept from an earlier
                // database would otherwise feed this one only what changed since, and nobody else.
                var activeUserCount = await db.users.Where(u => u.AccountEnabled.HasValue && u.AccountEnabled.Value == true).CountAsync();
                var deltaTokenCleared = false;
                if (activeUserCount == 0)
                {
                    await _userLoader.ClearStoredDeltaTokensAsync();
                    deltaTokenCleared = true;
                }

                // A stored delta token only returns people who changed since it was taken, so one taken under a
                // different UserGroupsFilter would never return the people the new filter lets in.
                deltaTokenCleared |= await ClearDeltaTokenIfUserScopeChangedAsync(orgSelection.DeltaKeyQualifier, alreadyCleared: deltaTokenCleared);

                var scopeCatchUpDecision = ScopeCatchUpDecision.None;
                if (!deltaTokenCleared && userScope.IsFiltered)
                {
                    scopeCatchUpDecision = await ClearDeltaTokenForScopeCatchUpIfDueAsync(
                        db, userScope, orgSelection.DeltaKeyQualifier);
                    deltaTokenCleared |= scopeCatchUpDecision == ScopeCatchUpDecision.MembershipCatchUpRequested;
                }

                // The membership marker is advanced only after a successful FULL read. Advancing it after an
                // incremental read would forget a membership change while the newly in-scope person's user object
                // remained behind the stored delta checkpoint.
                var readingFullDirectory = deltaTokenCleared
                    || scopeCatchUpDecision == ScopeCatchUpDecision.FullReadAlreadyPending;
                var scopeCatchUpRequested =
                    scopeCatchUpDecision == ScopeCatchUpDecision.MembershipCatchUpRequested;

                // Load from Graph & update delta code once done
                var allActiveGraphUsers = await _userLoader.LoadAllActiveUsers();
                var deltaReadCompleted = _userLoader.LastLoadReachedDeltaLink;
                var deactivations = _userLoader.LastLoadDeactivations ?? GraphUserDeactivations.None;
                var disabledGraphUsers = new List<GraphUser>(deactivations.DisabledUsers);
                _logger.LogInformation($"User import - loaded {allActiveGraphUsers.Count.ToString("N0")} users from Graph");

                if (userScope.IsFiltered)
                {
                    var readFromGraph = allActiveGraphUsers.Count;
                    allActiveGraphUsers = SelectGraphUsersInScope(allActiveGraphUsers, userScope);
                    _logger.LogInformation($"User import - {allActiveGraphUsers.Count:N0} of the {readFromGraph:N0} users read are in UserGroupsFilter; only they are written.");

                    // Disabled members are still members, so their rows are still kept up to date.
                    disabledGraphUsers = SelectGraphUsersInScope(disabledGraphUsers, userScope);
                }

                // Users quarantined by an earlier cycle are checked again beside this read: their stored profile, or this
                // read's newer one. See UserIdentityQuarantineRules.
                var quarantineLoad = await LoadIdentityQuarantineAsync();
                var quarantineMerge = UserIdentityQuarantineRules.Merge(quarantineLoad.Entries, allActiveGraphUsers, disabledGraphUsers, deactivations,
                    u => !userScope.IsFiltered || userScope.IsAnyInScope(u.Id, u.UserPrincipalName, u.Mail));

                // Get SKUs from tenant
                var skus = await _userLoader.LoadTenantSkus();

                // Load DB user data without tracking.
                // Only include license lookups when we'll need per-user license processing
                // (i.e. tenant-level SKUs unavailable). Including them unconditionally loads
                // hundreds of thousands of extra entities that are never read in the common
                // path and can cause an out-of-memory crash before the existing-user metadata
                // update runs.
                var allDbUsers = skus == null
                    ? await db.users.AsNoTracking().Include(u => u.LicenseLookups).ToListAsync()
                    : await db.users.AsNoTracking().ToListAsync();
                _logger.LogInformation($"User import - loaded {allDbUsers.Count.ToString("N0")} users from database");

                // Renamed users keep their row. Users whose identity cannot be matched safely are quarantined: left out of
                // everything below that depends on who they are, and carried to the next cycle. Nobody else waits for them.
                var profilesByObjectId = new Dictionary<string, GraphUser>(StringComparer.OrdinalIgnoreCase);
                foreach (var graphUser in allActiveGraphUsers.Concat(disabledGraphUsers))
                {
                    if (!string.IsNullOrEmpty(graphUser?.Id))
                    {
                        profilesByObjectId[graphUser.Id] = graphUser;
                    }
                }
                var reconciliation = await UserRenameProcessor.ReconcileAsync(db.Database.Connection.ConnectionString,
                    allActiveGraphUsers.Concat(disabledGraphUsers), allDbUsers, _logger,
                    () => EnsureWriterLockHeldAsync("user rename"));
                var quarantinedRowIds = new HashSet<int>();
                if (reconciliation.Conflicts.Count > 0)
                {
                    allActiveGraphUsers = allActiveGraphUsers.Where(u => !IsQuarantined(u, reconciliation)).ToList();
                    disabledGraphUsers = disabledGraphUsers.Where(u => !IsQuarantined(u, reconciliation)).ToList();
                    quarantinedRowIds = RowsOfQuarantinedIdentities(allDbUsers, reconciliation, profilesByObjectId);
                }
                LogIdentityReconciliation(reconciliation, quarantineLoad, quarantineMerge);

                // Pre-build dictionary for O(1) graph user lookups by AAD ID (avoids O(n) scans per user in manager resolution).
                // After the quarantine, so a quarantined manager is never looked up under the name that is in conflict.
                _dataMapper.SetGraphUserLookup(allActiveGraphUsers);

                // Create lookup dictionaries for performance - pre-allocate capacity
                var dbUsersByUpn = new Dictionary<string, Common.Entities.User>(allDbUsers.Count, StringComparer.OrdinalIgnoreCase);
                var dbUsersByAadId = new Dictionary<string, Common.Entities.User>(allDbUsers.Count, StringComparer.OrdinalIgnoreCase);

                // A renamed user's old row keeps their object id, so an object id can be on more than one row. Managers
                // are resolved through this map, so it holds the row with Graph's current sign-in name, or else the
                // newest one - never whichever the unordered load returned first, which left reports linked to the row
                // a rename left behind.
                var disabledGraphUsersByAadId = new Dictionary<string, GraphUser>(StringComparer.OrdinalIgnoreCase);
                foreach (var disabledUser in disabledGraphUsers)
                {
                    if (!string.IsNullOrEmpty(disabledUser.Id))
                    {
                        disabledGraphUsersByAadId[disabledUser.Id] = disabledUser;
                    }
                }
                Func<string, string> currentUpnOf = aadId =>
                {
                    GraphUser current;
                    return _dataMapper.GraphUsersByAadId.TryGetValue(aadId, out current) || disabledGraphUsersByAadId.TryGetValue(aadId, out current)
                        ? current.UserPrincipalName
                        : null;
                };

                // Single pass to populate both dictionaries. Under UserGroupsFilter, people outside the scope are left
                // out: these maps are also where managers are resolved, and a manager outside the scope must resolve
                // to no manager rather than to their (still unpurged) row.
                var rowsSharingAnObjectId = 0;
                foreach (var user in allDbUsers)
                {
                    if (userScope.IsFiltered && !IsDbUserInScope(user, userScope))
                    {
                        continue;
                    }
                    if (!string.IsNullOrEmpty(user.UserPrincipalName))
                    {
                        dbUsersByUpn[user.UserPrincipalName] = user;
                    }
                    if (UserIdentityRules.AddByObjectId(dbUsersByAadId, user, currentUpnOf))
                    {
                        rowsSharingAnObjectId++;
                    }
                }
                if (rowsSharingAnObjectId > 0)
                {
                    _logger.LogInformation($"User import - {rowsSharingAnObjectId:N0} users row(s) carry the same Entra object id as another row, usually because the user's " +
                        "sign-in name changed. Manager links use the row with the user's current sign-in name, or else the newest row.");
                }

                // Accounts Graph reported disabled or deleted - and, after a complete read of the directory, accounts it
                // did not list. Nobody is inserted for them; a row the table has for a disabled user is updated with
                // everyone else's below, and every row carrying one of their object ids - a rename's left-behind row
                // too - is then marked disabled, whether or not they are still in UserGroupsFilter (see
                // SelectRowsToDeactivate). Resolved now, against the rows just loaded: a deleted account's entry carries
                // nothing but its object id, and azure_ad_id cannot be queried efficiently.
                var disabledGraphUsersWithRows = disabledGraphUsers.Where(u => dbUsersByUpn.ContainsKey(u.UserPrincipalName)).ToList();
                var rowsToDeactivate = UserIdentityRules.SelectRowsToDeactivate(allDbUsers, deactivations);

                // Absence only means deletion if the read really listed the whole directory. Graph's own count of the
                // directory's users is read now, as close to the read as possible, and only when absence would disable
                // someone: see DirectoryReadCompleteness.
                int? usersInDirectory = null;
                if (rowsToDeactivate.NoLongerInDirectory.Count > 0)
                {
                    usersInDirectory = await CountDirectoryUsersAsync();
                }

                var graphMentionedExistingDbUsers = _dataMapper.GetDbUsersFromGraphUsers(allActiveGraphUsers, allDbUsers);
                var fallbackLicenceUserIds = skus == null ? new HashSet<int>() : null;
                var fallbackDesiredLicences = skus == null ? new HashSet<UserLicenseAssignment>() : null;

                // Insert any user we've not seen so far. Their managers are resolved against every user already in the
                // database: in an incremental read, a newcomer's manager is usually someone Graph did not report as changed.
                var insertedDbUsers = await InsertMissingUsersFenced(db, allActiveGraphUsers, graphMentionedExistingDbUsers, skus == null, importCycleLastUpdatedUtc, fallbackLicenceUserIds, fallbackDesiredLicences, dbUsersByAadId);
                phaseResults.InsertPhaseSucceeded = true;
                _logger.LogInformation($"User import - Insert phase completed. {insertedDbUsers.Count.ToString("N0")} new users inserted.");

                // Reload newly inserted users WITH TRACKING and update dictionaries
                // This ensures they're properly tracked when used as managers in ProcessExistingUsersInBatches
                if (insertedDbUsers.Count > 0)
                {
                    _logger.LogInformation($"User import - Reloading {insertedDbUsers.Count.ToString("N0")} newly inserted users with tracking for manager relationships...");

                    // Keep Graph's spelling; the shared resolver matches under the column's collation.
                    var insertedUpns = new List<string>(insertedDbUsers.Count);
                    foreach (var user in insertedDbUsers)
                    {
                        if (!string.IsNullOrEmpty(user.UserPrincipalName))
                        {
                            insertedUpns.Add(user.UserPrincipalName);
                        }
                    }

                    // Load with tracking in reasonable batches to avoid memory issues
                    const int RELOAD_BATCH_SIZE = 1000;
                    var reloadedUsers = new List<Common.Entities.User>(insertedDbUsers.Count);

                    for (int i = 0; i < insertedUpns.Count; i += RELOAD_BATCH_SIZE)
                    {
                        var batchCount = Math.Min(RELOAD_BATCH_SIZE, insertedUpns.Count - i);
                        var batchUpns = insertedUpns.GetRange(i, batchCount);
                        var resolved = await ExistingUserIds.FindAsync(db, batchUpns);
                        var ids = resolved.Where(id => id.HasValue).Select(id => id.Value).Distinct().ToList();
                        var batchReloaded = await db.users
                            .Where(u => ids.Contains(u.ID))
                            .ToListAsync();
                        reloadedUsers.AddRange(batchReloaded);
                    }

                    // Update lookup dictionaries with TRACKED entities.
                    // dbUsersByUpn was built with StringComparer.OrdinalIgnoreCase so we can
                    // key by the original UPN without lowering it - the comparer handles case.
                    foreach (var insertedUser in reloadedUsers)
                    {
                        if (!string.IsNullOrEmpty(insertedUser.UserPrincipalName))
                        {
                            dbUsersByUpn[insertedUser.UserPrincipalName] = insertedUser;
                            await _userMetaCache.UserCache.GetOrCreateNewResource(insertedUser.UserPrincipalName, insertedUser);
                        }

                        if (!string.IsNullOrEmpty(insertedUser.AzureAdId))
                        {
                            dbUsersByAadId[insertedUser.AzureAdId] = insertedUser;
                        }
                    }

                    insertedDbUsers = reloadedUsers;
                }

                // Identify users that need updating - use HashSet for O(1) lookup instead of Any().
                // Both sets are OrdinalIgnoreCase so we keep the original UPN casing and let
                // the comparer handle case-insensitivity (cheaper than .ToLower() at 200k scale).
                var insertedUpnSet = new HashSet<string>(
                    insertedDbUsers.Where(u => !string.IsNullOrEmpty(u.UserPrincipalName)).Select(u => u.UserPrincipalName),
                    StringComparer.OrdinalIgnoreCase);

                var notInsertedUpns = new HashSet<string>(allActiveGraphUsers.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var graphUser in allActiveGraphUsers)
                {
                    if (!string.IsNullOrEmpty(graphUser.UserPrincipalName) && !insertedUpnSet.Contains(graphUser.UserPrincipalName))
                    {
                        notInsertedUpns.Add(graphUser.UserPrincipalName);
                    }
                }

                // Disabled users with a row are updated like everyone else, account_enabled included.
                var graphUsersToUpdate = allActiveGraphUsers;
                if (disabledGraphUsersWithRows.Count > 0)
                {
                    graphUsersToUpdate = new List<GraphUser>(allActiveGraphUsers.Count + disabledGraphUsersWithRows.Count);
                    graphUsersToUpdate.AddRange(allActiveGraphUsers);
                    graphUsersToUpdate.AddRange(disabledGraphUsersWithRows);
                    foreach (var disabledUser in disabledGraphUsersWithRows)
                    {
                        notInsertedUpns.Add(disabledUser.UserPrincipalName);
                    }
                }

                // NOTE: we deliberately do NOT clear allDbUsers here yet when
                // tenant-level SKUs are available. The licence refresh step below
                // (ProcessSKUsForAllUsers) MUST iterate over the entire DB user
                // population - not just the users returned by the current Graph
                // delta - otherwise users whose only change in Graph is a licence
                // assignment will never have their user_license_type_lookups
                // rows refreshed. With a persisted delta token (the state table) this
                // causes licence counts to drift downward run after run until
                // they no longer match the tenant's actual licence assignments.
                // When SKUs are not available the per-user path inside
                // UpdateDbUserWithGraphData handles licences as part of the
                // per-user Graph call, so we can free the list early in that
                // branch to save memory.
                if (skus == null)
                {
                    allDbUsers.Clear();
                    allDbUsers = null;
                }

                // Update existing users.
                // When tenant-level SKUs are available we can use the fast bulk-SQL
                // path (no per-user Graph calls needed for licenses).
                // When SKUs are NOT available we must fall back to the EF-per-entity
                // path because each user needs individual Graph license queries.
                _logger.LogInformation($"User import - Starting metadata update for {notInsertedUpns.Count.ToString("N0")} existing users...");
                int existingUsersUpdated = 0;
                try
                {
                    if (skus != null)
                    {
                        existingUsersUpdated = await _batchProcessor.BulkUpdateExistingUsers(
                            db,
                            graphUsersToUpdate,
                            notInsertedUpns,
                            dbUsersByUpn,
                            dbUsersByAadId,
                            _dataMapper.GraphUsersByAadId,
                            _userMetaCache,
                            new FencedUserBulkUpdateWriter(new SqlUserBulkUpdateWriter(db.Database.Connection.ConnectionString), () => EnsureWriterLockHeldAsync("metadata update")),
                            importCycleLastUpdatedUtc);
                    }
                    else
                    {
                        existingUsersUpdated = await _batchProcessor.ProcessExistingUsersInBatches(
                            db,
                            graphUsersToUpdate,
                            notInsertedUpns,
                            dbUsersByUpn,
                            dbUsersByAadId,
                            async (graphUser, dbUser) => await UpdateDbUserWithGraphData(db, graphUser, allActiveGraphUsers, new List<Common.Entities.User>(), dbUser, true, dbUsersByAadId, importCycleLastUpdatedUtc, fallbackLicenceUserIds, fallbackDesiredLicences),
                            // Resolve the whole batch's managers in bulk rather than one query per
                            // user - see UserDataMapper.PrefetchManagersForBatchAsync (#371).
                            batch => _dataMapper.PrefetchManagersForBatchAsync(batch),
                            BATCH_SIZE,
                            beforeSave: () => EnsureWriterLockHeldAsync("metadata update"));
                    }

                    // Last, so nothing written above can turn a row back on. Part of this phase, so a failure withholds
                    // the delta token exactly like any other write: the next cycle reads these accounts again.
                    await MarkRowsDisabledAsync(db, rowsToDeactivate, deactivations, usersInDirectory, importCycleLastUpdatedUtc);
                    phaseResults.UpdatePhaseSucceeded = true;
                    _logger.LogInformation($"User import - Completed metadata update for {existingUsersUpdated.ToString("N0")} existing users");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"User import - ERROR updating existing users: {ex.Message}");
                    throw;
                }

                // Build the user list passed to ProcessSKUsForAllUsers.
                //
                // IMPORTANT: this MUST cover every user in the database, not just
                // the users returned by the current Graph delta response. The
                // licence refresh step reconciles user_license_type_lookups rows
                // for the supplied users against the per-SKU Graph queries; any
                // user not in the supplied list keeps their stale rows forever and
                // any new licence assignment for them is never written. When the
                // delta token is persisted (the state table) the delta response shrinks to
                // only users with metadata changes, so scoping the licence refresh
                // to delta users causes the tenant-wide licence counts to drift
                // downward over time.
                //
                // The supplied list is also the ONLY set of users whose licence rows
                // the refresh is allowed to delete, so passing the whole population
                // is what makes removals correct as well as additions.
                //
                // We build the list from allDbUsers (every existing DB user
                // loaded at the start of this run) plus insertedDbUsers (users
                // freshly created in this run), de-duplicated by primary key
                // because the refresh turns it into a UPN-keyed dictionary.
                List<Common.Entities.User> allDbUsersForLicenseRefresh = null;
                if (skus != null)
                {
                    var combinedById = new Dictionary<int, Common.Entities.User>(
                        (allDbUsers?.Count ?? 0) + insertedDbUsers.Count);

                    if (allDbUsers != null)
                    {
                        foreach (var u in allDbUsers)
                        {
                            // Licences are directory metadata too: not refreshed for anyone outside UserGroupsFilter,
                            // nor for a quarantined identity - SKU holders are matched by sign-in name, which is what is in
                            // conflict, so their licence rows are left exactly as they are.
                            if (u.ID > 0 && (!userScope.IsFiltered || IsDbUserInScope(u, userScope)) && !quarantinedRowIds.Contains(u.ID))
                            {
                                combinedById[u.ID] = u;
                            }
                        }
                    }

                    foreach (var u in insertedDbUsers)
                    {
                        if (u.ID > 0)
                        {
                            combinedById[u.ID] = u;
                        }
                    }

                    allDbUsersForLicenseRefresh = new List<Common.Entities.User>(combinedById.Values);
                    _logger.LogInformation($"User import - licence refresh will cover {allDbUsersForLicenseRefresh.Count.ToString("N0")} DB users (entire population, not just delta).");
                }

                // Can we update SKUs for users on batch (ie Organization.Read.All granted)?
                if (skus != null)
                {
                    // No re-attach loop needed: the licence refresh works in FK IDs
                    // (UserId / LicenseTypeId) rather than navigation properties, so
                    // the entities do not need to be tracked by EF.
                    await EnsureWriterLockHeldAsync("licence refresh");
                    await _licenseProcessor.ProcessSKUsForAllUsers(skus, allDbUsersForLicenseRefresh, db);
                    _logger.LogInformation($"User import - updated user license information from {skus.Count.ToString("N0")} tenant SKUs");

                    db.ChangeTracker.DetectChanges();
                    await db.SaveChangesAsync();
                    phaseResults.LicenceRefreshSucceeded = true;
                }
                else
                {
                    await UserLicenseProcessor.MarkSubscribedSkuCapacityUnavailableAsync(db, _logger);
                    await EnsureWriterLockHeldAsync("per-user licence refresh");
                    await _licenseProcessor.ReconcileCollectedUserLicenses(db, fallbackLicenceUserIds, fallbackDesiredLicences);

                    // No separate licence phase runs at all when tenant SKUs are unavailable:
                    // ProcessUserLicenses already ran per user inside the update phase above, so
                    // there is no outstanding licence work that committing the delta could skip.
                    // Set inside the branch rather than after it so that a catch added around the
                    // work above would leave the flag false, exactly as it would for the other two
                    // phases.
                    phaseResults.LicenceRefreshSucceeded = true;
                }

                // Apply the org values last, once every Graph user is guaranteed to have a dbo.users row
                // (the insert phase above creates the missing ones) so their ids can be resolved. Only from a read
                // that reached its deltaLink: the organisation merge has only ever been given a complete read - any
                // other failure of an org-carrying read is retried without the attributes - and a page that came back
                // without its value collection is now a failed page that is not.
                if (deltaReadCompleted)
                {
                    await ApplyUserOrgValues(allActiveGraphUsers, entraOrgTypes, dbUsersByUpn, phaseResults, importCycleLastUpdatedUtc);
                }

                _logger.LogInformation($"{DateTime.Now.ToShortTimeString()} User import - complete. Inserted {insertedDbUsers.Count.ToString("N0")} new users, updated metadata for {existingUsersUpdated.ToString("N0")} existing users (from {allActiveGraphUsers.Count.ToString("N0")} Graph users)");

                // All insert/metadata/license work succeeded. Now and ONLY now is it
                // safe to persist the new Graph delta token. If any of the previous
                // steps had thrown, control would have left this method via the
                // exception and the previously-persisted delta would still be in
                // effect, so the failed users will be retried on the next import
                // cycle instead of being skipped because Graph already considers
                // them "seen".
                //
                // The check is explicit rather than implied by that control flow so the
                // guarantee survives someone adding a catch: see UserImportCommitPolicy (#372).
                bool cycleCompleted;
                var checkpointWouldMove = deltaReadCompleted && UserImportCommitPolicy.ShouldCommitDelta(phaseResults);
                phaseResults.IdentityQuarantineSaved = await SaveIdentityQuarantineAsync(quarantineLoad, quarantineMerge, reconciliation, profilesByObjectId,
                    checkpointWouldMove);
                if (!deltaReadCompleted)
                {
                    // The read never reached an @odata.deltaLink, so there is no new token and
                    // CommitDeltaTokenAsync would quietly do nothing. Say so, and report the run as
                    // incomplete so it is neither stamped as done nor announced as a finished section:
                    // otherwise a read that fails on every run looks exactly like a tenant in which
                    // nothing changes (#664).
                    _logger.LogWarning("User import - NOT moving the user checkpoint forward: the Graph /users/delta read did not reach its end (no @odata.deltaLink), " +
                        "so this run's user list is incomplete and there is no new delta token to save. The users that were read have been saved; " +
                        "the next cycle reads again from the stored checkpoint, or reads the full user list if there is none.");
                    cycleCompleted = false;
                }
                else if (UserImportCommitPolicy.ShouldCommitDelta(phaseResults))
                {
                    // False when the loader withheld the new token because the checkpoint was cleared while this
                    // run was in progress (an admin asking for a full re-read); it has logged why. Reporting the run
                    // as not done keeps the cadence gate open, so that re-read happens on the next cycle.
                    // Not fenced: every write is done, so losing the lock now protects nothing, and failing here would
                    // only throw away a finished run.
                    cycleCompleted = await _userLoader.CommitDeltaTokenAsync();
                }
                else
                {
                    _logger.LogWarning("User import - NOT committing the Graph delta token: at least one import phase did not complete. " +
                        "The same users will be reprocessed on the next cycle rather than being skipped.");
                    cycleCompleted = false;
                }

                if (cycleCompleted && readingFullDirectory)
                {
                    await RecordScopeMembershipAfterFullReadAsync(orgSelection.DeltaKeyQualifier, userScope, scopeCatchUpRequested);
                }

                // Final cleanup
                dbUsersByUpn.Clear();
                dbUsersByAadId.Clear();
                allActiveGraphUsers.Clear();
                allDbUsersForLicenseRefresh?.Clear();
                allDbUsers?.Clear();

                return cycleCompleted;
            }
        }

        /// <summary>The Graph users in the <c>UserGroupsFilter</c> scope, matched on object id, UPN or mail.</summary>
        internal static List<GraphUser> SelectGraphUsersInScope(List<GraphUser> graphUsers, UserImportScope userScope)
        {
            if (graphUsers == null || userScope == null || !userScope.IsFiltered)
            {
                return graphUsers;
            }
            return graphUsers.Where(u => u != null && userScope.IsAnyInScope(u.Id, u.UserPrincipalName, u.Mail)).ToList();
        }

        /// <summary>True when a users-table row belongs to someone in the <c>UserGroupsFilter</c> scope.</summary>
        internal static bool IsDbUserInScope(Common.Entities.User user, UserImportScope userScope)
            => user != null && userScope.IsAnyInScope(user.AzureAdId, user.UserPrincipalName, user.Mail);

        private sealed class IdentityQuarantineLoad
        {
            public List<QuarantinedUserIdentity> Entries { get; set; } = new List<QuarantinedUserIdentity>();

            /// <summary>False when the stored set could not be read: it is then never overwritten this cycle.</summary>
            public bool Available { get; set; }
        }

        private async Task<IdentityQuarantineLoad> LoadIdentityQuarantineAsync()
        {
            if (IdentityQuarantineStore == null)
            {
                return new IdentityQuarantineLoad { Available = false };
            }

            try
            {
                return new IdentityQuarantineLoad { Entries = await IdentityQuarantineStore.LoadAsync(), Available = true };
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"User import - identity quarantine: could not read the stored set from {IdentityQuarantineStore.Description} ({DescribeStateFailure(ex)}). " +
                    "Users quarantined by earlier cycles are not re-checked this cycle and stay stored. If anyone is quarantined this cycle, the user checkpoint does not move.");
                return new IdentityQuarantineLoad { Available = false };
            }
        }

        private static bool IsQuarantined(GraphUser user, UserIdentityReconciliation reconciliation)
            => !string.IsNullOrEmpty(user?.Id) && reconciliation.Conflicts.ContainsKey(user.Id);

        /// <summary>
        /// The rows a quarantined identity could touch: those carrying their object id, and the one holding the sign-in name
        /// Graph now reports for them.
        /// </summary>
        private static HashSet<int> RowsOfQuarantinedIdentities(List<Common.Entities.User> dbUsers, UserIdentityReconciliation reconciliation,
            Dictionary<string, GraphUser> profilesByObjectId)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var objectId in reconciliation.Conflicts.Keys)
            {
                if (profilesByObjectId.TryGetValue(objectId, out var profile) && !string.IsNullOrEmpty(profile.UserPrincipalName))
                {
                    names.Add(profile.UserPrincipalName);
                }
            }

            var rows = new HashSet<int>();
            foreach (var user in dbUsers)
            {
                if ((!string.IsNullOrEmpty(user.AzureAdId) && reconciliation.Conflicts.ContainsKey(user.AzureAdId))
                    || (!string.IsNullOrEmpty(user.UserPrincipalName) && names.Contains(user.UserPrincipalName)))
                {
                    rows.Add(user.ID);
                }
            }
            return rows;
        }

        private void LogIdentityReconciliation(UserIdentityReconciliation reconciliation, IdentityQuarantineLoad load, UserIdentityQuarantineMerge merge)
        {
            var resolved = merge.Considered.Keys.Count(id => !reconciliation.Conflicts.ContainsKey(id));
            var message = $"User import - identity reconciliation: {reconciliation.RenamesApplied:N0} renamed in place " +
                $"({reconciliation.RenamedFromSeveralRows:N0} from several rows, {reconciliation.ChainedRenames:N0} after another rename freed the name), " +
                $"{reconciliation.PlaceholdersAdopted:N0} placeholder row(s) adopted (path: object id stamped on the row another import created under the new sign-in name; earlier rows kept as history), " +
                $"{reconciliation.Conflicts.Count:N0} quarantined, {merge.Retried:N0} retried from earlier cycles, {resolved:N0} resolved, " +
                $"{merge.Dropped:N0} dropped (deleted or outside UserGroupsFilter), in {reconciliation.ElapsedMs:N0} ms.";
            if (reconciliation.Conflicts.Count > 0 || !load.Available)
            {
                _logger.LogWarning(message);
            }
            else
            {
                _logger.LogInformation(message);
            }
        }

        /// <summary>
        /// Stores who is quarantined after this cycle, and returns whether the user checkpoint may move: only when every
        /// quarantined user's latest profile is safely stored (see <see cref="UserIdentityQuarantineRules"/>).
        /// </summary>
        private async Task<bool> SaveIdentityQuarantineAsync(IdentityQuarantineLoad load, UserIdentityQuarantineMerge merge,
            UserIdentityReconciliation reconciliation, Dictionary<string, GraphUser> profilesByObjectId, bool checkpointWouldMove)
        {
            var next = UserIdentityQuarantineRules.Next(merge, reconciliation.Conflicts, profilesByObjectId, _clock.UtcNow, checkpointWouldMove);
            if (next.Count > 0 || load.Entries.Count > 0)
            {
                var resolved = merge.Considered.Keys.Count(id => !reconciliation.Conflicts.ContainsKey(id));
                var oldest = next.Count > 0 ? next.Min(e => e.SinceUtc).ToString("u") : "n/a";
                var message = $"User import - identity quarantine: {next.Count:N0} user(s) quarantined, oldest since {oldest}, {merge.Retried:N0} retried, {resolved:N0} resolved" +
                    (checkpointWouldMove ? "." : "; resolved users stay stored until a cycle whose checkpoint moves.");
                if (next.Count > 0)
                {
                    _logger.LogWarning(message);
                }
                else
                {
                    _logger.LogInformation(message);
                }
            }

            if (!load.Available)
            {
                if (reconciliation.Conflicts.Count == 0)
                {
                    return true;
                }
                _logger.LogError($"User import - identity quarantine: {reconciliation.Conflicts.Count:N0} user(s) were quarantined this cycle, but the stored set could not be read, " +
                    "so it is not overwritten. NOT moving the user checkpoint forward, so none of their changes are lost; they are read again next cycle.");
                return false;
            }

            var fits = next.Count <= UserIdentityQuarantineStore.MaxEntries;
            if (!fits)
            {
                _logger.LogError($"User import - identity quarantine: {next.Count:N0} user(s) are quarantined, more than the {UserIdentityQuarantineStore.MaxEntries:N0} the state store keeps. " +
                    "Storing the longest-quarantined and NOT moving the user checkpoint forward, so none of the others' changes are lost; they are read again next cycle.");
                next = next.OrderBy(e => e.SinceUtc).Take(UserIdentityQuarantineStore.MaxEntries).ToList();
            }

            if (!UserIdentityQuarantineRules.Differs(load.Entries, next))
            {
                return fits;
            }

            try
            {
                await IdentityQuarantineStore.SaveAsync(next);
                return fits;
            }
            catch (Exception ex)
            {
                _logger.LogError($"User import - identity quarantine: could not save {next.Count:N0} user(s) to {IdentityQuarantineStore.Description} ({DescribeStateFailure(ex)}). " +
                    "NOT moving the user checkpoint forward, so none of their changes are lost; they are read again next cycle.");
                return false;
            }
        }

        /// <summary>The exception type and, for a storage failure, its HTTP status and error code. Never a URL.</summary>
        private static string DescribeStateFailure(Exception ex)
        {
            var requestFailed = ex as Azure.RequestFailedException;
            return requestFailed == null
                ? ex.GetType().Name
                : $"{ex.GetType().Name}, HTTP {requestFailed.Status}, error code '{requestFailed.ErrorCode ?? "unknown"}'";
        }

        /// <summary>
        /// Discards the stored <c>/users/delta</c> tokens when the one this cycle would resume was taken under a
        /// different <c>UserGroupsFilter</c> from the one set now, so this cycle reads the whole directory (filtered to
        /// the new scope as always).
        /// </summary>
        /// <remarks>
        /// A token only returns people who changed after it was taken, and while a filter is set everyone outside it
        /// is left out of the users table. So once the filter is removed or changed, the people it newly lets in would
        /// never be imported unless they happened to change. Changing the setting restarts the web job, which is why
        /// the filter a token belongs to is stored beside it rather than remembered in memory - under the same
        /// organisation-attribute qualifier as the token, because a token kept for another selection is resumed when
        /// that selection comes back, and must be judged by the filter it was taken under.
        /// </remarks>
        /// <param name="orgAttributeQualifier">The qualifier of the token this cycle would resume, from the org selection.</param>
        /// <returns>True when the tokens were discarded (or already had been) because the filter changed.</returns>
        private async Task<bool> ClearDeltaTokenIfUserScopeChangedAsync(string orgAttributeQualifier, bool alreadyCleared)
        {
            if (_scopeMarkerStore == null)
            {
                return false;
            }

            var fingerprint = (_userScopeProvider?.Filter ?? new UserGroupsFilterModel()).Fingerprint;
            try
            {
                var stored = await _scopeMarkerStore.GetFingerprintAsync(orgAttributeQualifier) ?? string.Empty;
                if (string.Equals(stored, fingerprint, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!alreadyCleared)
                {
                    _logger.LogInformation("User import - UserGroupsFilter has changed since the stored /users/delta checkpoint was taken, and an " +
                        "incremental read would never return the people it now includes who have not changed since. Reading the full user list this cycle.");
                    await _userLoader.ClearStoredDeltaTokensAsync();
                }

                // Recorded now rather than once the read completes: with the tokens gone, every cycle reads the full
                // list until one completes, whatever this record says. Both keys this cycle can resume from were
                // cleared - the org selection's and the unqualified one its fallback uses - so both are recorded.
                await _scopeMarkerStore.SetFingerprintAsync(orgAttributeQualifier, fingerprint);
                var unqualified = GraphUserOrgSelection.None.DeltaKeyQualifier;
                if (!string.Equals(orgAttributeQualifier ?? string.Empty, unqualified ?? string.Empty, StringComparison.Ordinal))
                {
                    await _scopeMarkerStore.SetFingerprintAsync(unqualified, fingerprint);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"User import - could not check whether UserGroupsFilter has changed since the last full read of the directory ({ex.Message}). " +
                    "Will check again next cycle.");
                return false;
            }
        }

        /// <summary>
        /// People added to the <c>UserGroupsFilter</c> groups after the directory was last read in full are never
        /// returned by an incremental <c>/users/delta</c> read - their user object did not change, only the group did.
        /// Compare the current enabled membership with the membership recorded after that full read and discard the
        /// delta tokens when it changed. This deliberately does not use "does a users-table row exist?" as the marker:
        /// an existing row may carry metadata from before the person entered scope and needs the same catch-up.
        /// </summary>
        private async Task<ScopeCatchUpDecision> ClearDeltaTokenForScopeCatchUpIfDueAsync(
            AnalyticsEntitiesContext db, UserImportScope userScope, string orgAttributeQualifier)
        {
            if (_lastRunStore == null)
            {
                return ScopeCatchUpDecision.None;
            }

            try
            {
                // The token this cycle would resume: the provider is keyed by the org selection already.
                if (string.IsNullOrEmpty(await _userLoader.DeltaValueProvider.GetDeltaToken()))
                {
                    return ScopeCatchUpDecision.FullReadAlreadyPending;
                }

                if (_scopeMarkerStore != null)
                {
                    var current = userScope.EnabledMembershipFingerprint;
                    var stored = await _scopeMarkerStore.GetMembershipFingerprintAsync(orgAttributeQualifier);
                    if (string.Equals(stored, current, StringComparison.Ordinal))
                    {
                        return ScopeCatchUpDecision.None;
                    }

                    // No marker means this build has just introduced membership tracking. Read once immediately:
                    // an old dbo.users row cannot prove that the person's metadata was imported while in scope.
                    if (stored != null)
                    {
                        var lastMembershipCatchUp = await _lastRunStore.GetLastRunUtc(ScopeCatchUpLastRunKey);
                        if (!ImportCadenceGate.ShouldRun(lastMembershipCatchUp, ScopeCatchUpIntervalHours, force: false, nowUtc: _clock.UtcNow))
                        {
                            _logger.LogInformation("User import - enabled membership of the UserGroupsFilter group(s) has changed since the last full directory read. " +
                                $"The next catch-up read is due after {lastMembershipCatchUp?.AddHours(ScopeCatchUpIntervalHours):u} UTC.");
                            return ScopeCatchUpDecision.None;
                        }
                    }

                    _logger.LogInformation(stored == null
                        ? "User import - no membership marker exists for the stored /users/delta checkpoint. Reading the full user list once so every current UserGroupsFilter member has current metadata."
                        : "User import - enabled membership of the UserGroupsFilter group(s) changed after the stored /users/delta checkpoint was taken. Reading the full user list this cycle.");
                    await _userLoader.ClearStoredDeltaTokensAsync();
                    return ScopeCatchUpDecision.MembershipCatchUpRequested;
                }

                // Non-persisted/test loaders have no marker store. Retain the old missing-row check as a best-effort
                // fallback; production persisted checkpoints take the fingerprint path above.
                var knownObjectIds = new HashSet<Guid>();
                foreach (var id in await db.users.Where(u => u.AzureAdId != null && u.AzureAdId != "").Select(u => u.AzureAdId).ToListAsync())
                {
                    if (Guid.TryParse(id, out var objectId))
                    {
                        knownObjectIds.Add(objectId);
                    }
                }

                var missing = userScope.EnabledMemberObjectIds.Count(id => !knownObjectIds.Contains(id));
                if (missing == 0)
                {
                    return ScopeCatchUpDecision.None;
                }

                var lastCatchUp = await _lastRunStore.GetLastRunUtc(ScopeCatchUpLastRunKey);
                if (!ImportCadenceGate.ShouldRun(lastCatchUp, ScopeCatchUpIntervalHours, force: false, nowUtc: _clock.UtcNow))
                {
                    _logger.LogInformation($"User import - {missing:N0} enabled member(s) of the UserGroupsFilter group(s) are not in the users table yet. " +
                        $"The next full re-read to add them is due after {lastCatchUp?.AddHours(ScopeCatchUpIntervalHours):u} UTC.");
                    return ScopeCatchUpDecision.None;
                }

                _logger.LogInformation($"User import - {missing:N0} enabled member(s) of the UserGroupsFilter group(s) are not in the users table - usually " +
                    "people added to the group(s) since the directory was last read in full, whom an incremental read never returns. " +
                    "Reading the full user list this cycle to add them.");
                await _userLoader.ClearStoredDeltaTokensAsync();
                await _lastRunStore.SetLastRunUtc(ScopeCatchUpLastRunKey, _clock.UtcNow);
                return ScopeCatchUpDecision.MembershipCatchUpRequested;
            }
            catch (Exception ex)
            {
                // A missed catch-up only delays adding new members; it must not stop the import.
                _logger.LogWarning($"User import - could not check for UserGroupsFilter members missing from the users table ({ex.Message}). " +
                    "Will check again next cycle.");
                return ScopeCatchUpDecision.None;
            }
        }

        /// <summary>
        /// Records which enabled membership a successfully completed full directory read covered. Each organisation
        /// attribute selection has its own delta token, so it has its own marker too; the unqualified fallback token
        /// is recorded alongside the selected one for the same reason as the filter marker.
        /// </summary>
        private async Task RecordScopeMembershipAfterFullReadAsync(
            string orgAttributeQualifier, UserImportScope userScope, bool wasMembershipCatchUp)
        {
            if (_scopeMarkerStore == null)
            {
                return;
            }

            try
            {
                var fingerprint = userScope.IsFiltered ? userScope.EnabledMembershipFingerprint : null;
                await _scopeMarkerStore.SetMembershipFingerprintAsync(orgAttributeQualifier, fingerprint);
                var unqualified = GraphUserOrgSelection.None.DeltaKeyQualifier;
                if (!string.Equals(orgAttributeQualifier ?? string.Empty, unqualified ?? string.Empty, StringComparison.Ordinal))
                {
                    await _scopeMarkerStore.SetMembershipFingerprintAsync(unqualified, fingerprint);
                }

                if (wasMembershipCatchUp && _lastRunStore != null)
                {
                    await _lastRunStore.SetLastRunUtc(ScopeCatchUpLastRunKey, _clock.UtcNow);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"User import - could not record which UserGroupsFilter membership the completed full directory read covered ({ex.Message}). " +
                    "The next cycle may repeat the full read rather than risk leaving a newly in-scope user's metadata stale.");
            }
        }

        #region User organisations

        /// <summary>
        /// Reads the enabled, Entra-sourced org types.
        /// </summary>
        /// <remarks>
        /// Never throws. User organisations are an optional feature layered onto the user import, and a
        /// database that has not been migrated yet - the web-jobs deliberately do not run migrations -
        /// simply has no such tables. Letting that stop the whole user import would be a far worse
        /// outcome than importing without org values.
        /// </remarks>
        private async Task<IReadOnlyList<Common.Entities.UserOrgs.UserOrgType>> LoadEnabledEntraOrgTypes()
        {
            if (_orgTypeStore == null)
            {
                return new Common.Entities.UserOrgs.UserOrgType[0];
            }

            try
            {
                var types = await _orgTypeStore.GetEnabledEntraTypesAsync();
                if (types.Count > 0)
                {
                    _logger.LogInformation($"User import - {types.Count} user organisation type(s) will be read from Entra.");
                }
                return types;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"User import - could not read the configured user organisation types ({ex.Message}). "
                    + "Continuing without organisation values. If this persists, check the database is upgraded to this build.");
                return new Common.Entities.UserOrgs.UserOrgType[0];
            }
        }

        /// <summary>
        /// Resolves each Graph user's configured org attributes and merges the result.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Skipped entirely when Graph rejected the org properties earlier in the cycle. That response
        /// carries no org attributes at all, so applying it would read as "every user's value was
        /// cleared" and wipe the assignments the admin is trying to fix.
        /// </para>
        /// <para>
        /// Never throws, for the same reason as <see cref="LoadEnabledEntraOrgTypes"/>: the user import
        /// proper has already succeeded by this point, and failing here would roll the whole cycle back
        /// and stop the delta token being committed.
        /// </para>
        /// <para>
        /// Records each type's refresh time once its values are applied - or once the cycle found
        /// nothing to change, which is the usual delta outcome and just as much a confirmation. Not when
        /// Graph rejected the attributes or the merge failed: the time going stale is exactly what tells
        /// an administrator the values have stopped being refreshed.
        /// </para>
        /// </remarks>
        private async Task ApplyUserOrgValues(
            List<GraphUser> graphUsers,
            IReadOnlyList<Common.Entities.UserOrgs.UserOrgType> orgTypes,
            Dictionary<string, Common.Entities.User> dbUsersByUpn,
            UserImportPhaseResults phaseResults,
            DateTime cycleStartedUtc)
        {
            if (orgTypes == null || orgTypes.Count == 0 || _orgAssignmentStore == null)
            {
                return;
            }

            if (_userLoader.OrgSelectionWasRejected)
            {
                // Not a failure of this phase - there was simply nothing to apply, and withholding the
                // delta token would re-read the whole tenant every cycle for as long as the attribute
                // stayed broken without ever making progress.
                _logger.LogWarning(
                    "User import - skipping user organisation values this cycle: Graph rejected the configured "
                    + "attributes, so the response does not contain them. Existing organisation values are left "
                    + "untouched rather than being cleared.");
                return;
            }

            try
            {
                IReadOnlyList<string> skipped;
                var parsed = UserOrgMappingRules.ParseOrgTypes(orgTypes, out skipped);

                if (skipped.Count > 0)
                {
                    _logger.LogError(
                        $"User import - skipping {skipped.Count} organisation type(s) whose Entra attribute could "
                        + $"not be understood: {string.Join(", ", skipped)}. Re-save them on the User organisations page.");
                }

                // Skipped whole, every cycle, once an import has found the attribute holding lists at this
                // configuration - not only in the cycle that happened to see a list. A later delta is usually
                // people who changed something else, without a list, and merging it would read their absent
                // attribute as "no value". They stay in the $select and the token key, so the token does not
                // churn; saving the type moves its generation and clears the mark.
                var holdingLists = orgTypes
                    .Where(t => t != null && t.AttributeHoldsLists)
                    .ToList();
                if (holdingLists.Count > 0)
                {
                    var stillSkipped = new HashSet<int>(holdingLists.Select(t => t.Id));
                    parsed = parsed.Where(p => !stillSkipped.Contains(p.OrgTypeId)).ToList();
                    _logger.LogWarning(
                        $"User import - still skipping {holdingLists.Count} organisation type(s) whose Entra attribute "
                        + "was found holding a list of values: "
                        + string.Join(", ", holdingLists.Select(t => $"'{t.Name}' ({t.EntraAttributeName})"))
                        + ". Point them at a single-valued attribute on the User organisations page.");
                }

                if (parsed.Count == 0)
                {
                    return;
                }

                // The expected source kind and per-type generation are passed so the merge can drop
                // anything whose org type has been switched away from Entra, disabled, or repointed
                // at a different attribute since this cycle read its configuration. That read happened
                // before 200,000 users were loaded from Graph, which is minutes of window in which an
                // admin can change it - and undoing their change would leave values no later import
                // ever corrects. Source kind alone is not enough: a repoint leaves the type enabled
                // and Entra-sourced throughout, so only the generation catches it.
                var expectedGenerations = orgTypes
                    .Where(t => t != null)
                    .GroupBy(t => t.Id)
                    .ToDictionary(g => g.Key, g => g.First().SourceGeneration);

                // Reuse the dictionary the import already built rather than re-querying dbo.users: at
                // 200k users that would be a second full table read for no new information.
                var userIdsByUpn = new Dictionary<string, int>(dbUsersByUpn.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var pair in dbUsersByUpn)
                {
                    if (pair.Value != null && pair.Value.ID > 0)
                    {
                        userIdsByUpn[pair.Key] = pair.Value.ID;
                    }
                }

                var listValued = new HashSet<int>();
                var shortened = new Dictionary<int, int>();
                var updates = UserOrgMappingRules.BuildUpdates(graphUsers, parsed, userIdsByUpn, listValued, shortened);
                if (listValued.Count > 0)
                {
                    // Skipped whole, for everyone: a list is not "no value", so clearing the users who have
                    // one would be wrong, and clearing only the users without one would half-apply a
                    // configuration known to be wrong. Nor is the type recorded as refreshed - now or in a
                    // later cycle, which is why it is recorded as holding lists: the delta token moves on,
                    // and the next cycle, often one in which nobody with a list changed, would otherwise
                    // stamp a type whose values were never read. The admin page says what is wrong.
                    var names = orgTypes
                        .Where(t => t != null && listValued.Contains(t.Id))
                        .Select(t => $"'{t.Name}' ({t.EntraAttributeName})");
                    _logger.LogError(
                        $"User import - skipping {listValued.Count} organisation type(s) whose Entra attribute holds a "
                        + $"list of values rather than one: {string.Join(", ", names)}. Their values were left as they "
                        + "were. A user can be in only one organisation of each type, so these values cannot be "
                        + "imported. Point the type at a single-valued attribute on the User organisations page.");

                    await RecordOrgTypesListValued(
                        listValued
                            .Where(expectedGenerations.ContainsKey)
                            .ToDictionary(id => id, id => expectedGenerations[id]));
                }

                if (updates.Count > 0)
                {
                    var result = await _orgAssignmentStore.MergeAsync(
                        updates, UserOrgSourceKind.EntraAttribute, expectedGenerations);

                    _logger.LogInformation(
                        $"User import - user organisations: {result.Applied.ToString("N0")} assignment(s) set, "
                        + $"{result.Cleared.ToString("N0")} cleared, {result.ValuesCreated.ToString("N0")} new organisation value(s) "
                        + $"across {parsed.Count} organisation type(s).");

                    // Stored shortened rather than dropped, as a CSV's are, but said so: values that differ only
                    // past the limit are now one organisation, and the save-time test sees only the one user it
                    // was run against. Counts and configuration only - the values are tenant data. And only when
                    // the merge stored everything it was given: it reports how many updates it fenced out, not
                    // whose, and a fenced value was never stored. That cycle withholds its delta token (below),
                    // so the next one re-reads these users and reports what it actually stores.
                    if (shortened.Count > 0 && result.FencedOut == 0)
                    {
                        var shortenedTypes = orgTypes
                            .Where(t => t != null && shortened.ContainsKey(t.Id))
                            .GroupBy(t => t.Id)
                            .Select(g => $"'{g.First().Name}' ({g.First().EntraAttributeName}): {shortened[g.Key].ToString("N0")}");
                        _logger.LogWarning(
                            $"User import - {shortened.Values.Sum().ToString("N0")} organisation value(s) were longer than "
                            + $"{UserOrgRules.MaxOrgValueLength} characters, the most that can be stored, and were shortened to "
                            + $"fit: {string.Join(", ", shortenedTypes)}. Values that differ only after that point are stored as "
                            + "the same organisation. Shorten them in Entra ID, or point the type at a different attribute on "
                            + "the User organisations page.");
                    }

                    if (result.FencedOut > 0)
                    {
                        // An org type was reconfigured while this cycle was loading users from Graph, so
                        // its updates were dropped rather than applied over the top of the change. The
                        // token is withheld because those users will not appear in a delta again unless
                        // they change: committing it would strand them with stale values indefinitely.
                        _logger.LogWarning(
                            $"User import - {result.FencedOut.ToString("N0")} organisation update(s) were skipped because "
                            + "their organisation type was reconfigured while this cycle was running. The delta token "
                            + "will not be committed, so the next cycle re-reads them.");

                        phaseResults.UserOrgsSucceeded = false;
                    }
                }

                // Only the types this cycle actually read. One skipped as unparseable was not refreshed,
                // nor was one whose attribute turned out to hold lists, and a reconfigured one is filtered
                // out by the store's own fence.
                var refreshed = parsed
                    .Select(p => p.OrgTypeId)
                    .Distinct()
                    .Where(id => expectedGenerations.ContainsKey(id) && !listValued.Contains(id))
                    .ToDictionary(id => id, id => expectedGenerations[id]);

                await RecordOrgTypesRefreshed(refreshed, cycleStartedUtc);
            }
            catch (Exception ex)
            {
                // The rest of the import keeps its results - this must never fail the user import. But
                // the delta token is withheld, because committing it would throw away the very
                // enumeration these values needed. See UserImportPhaseResults.UserOrgsSucceeded.
                phaseResults.UserOrgsSucceeded = false;

                _logger.LogError(
                    $"User import - failed to apply user organisation values ({ex.Message}). The rest of the user "
                    + "import completed, but the Graph delta token will NOT be committed, so the next cycle re-reads "
                    + "the users needed to populate them.");
            }
        }

        /// <summary>
        /// Records that these org types' attributes hold lists, so no later cycle stamps them as refreshed
        /// until their configuration changes.
        /// </summary>
        /// <remarks>
        /// Never throws, and never withholds the delta token - re-reading the tenant cannot make a list into
        /// one value. A failure leaves the old behaviour for this configuration: the type is not stamped by
        /// this cycle, but a later quiet one may.
        /// </remarks>
        private async Task RecordOrgTypesListValued(IReadOnlyDictionary<int, int> expectedGenerations)
        {
            if (_orgTypeStore == null || expectedGenerations.Count == 0)
            {
                return;
            }

            try
            {
                await _orgTypeStore.RecordListValuedAsync(expectedGenerations);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"User import - could not record that {expectedGenerations.Count} organisation type(s) hold lists "
                    + $"({ex.Message}). The User organisations page may show them as refreshed after a later import.");
            }
        }

        /// <summary>
        /// Records when these org types were refreshed, for the "Last refreshed" column on the admin page.
        /// </summary>
        /// <remarks>
        /// Never throws, and never withholds the delta token. The values themselves are already applied;
        /// failing to record when is a stale label, not stale data, and re-reading the tenant to fix a
        /// label would cost far more than it is worth.
        /// </remarks>
        private async Task RecordOrgTypesRefreshed(IReadOnlyDictionary<int, int> expectedGenerations, DateTime cycleStartedUtc)
        {
            if (_orgTypeStore == null || expectedGenerations.Count == 0)
            {
                return;
            }

            try
            {
                await _orgTypeStore.RecordEntraRefreshAsync(expectedGenerations, cycleStartedUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    $"User import - organisation values were applied, but recording when they were refreshed failed "
                    + $"({ex.Message}). The User organisations page will show an older 'Last refreshed' time until "
                    + "the next cycle records it.");
            }
        }

        #endregion

        private async Task UpdateDbUserWithGraphData(AnalyticsEntitiesContext db, GraphUser graphUser, List<GraphUser> allGraphUsers, List<Common.Entities.User> allDbUsers, Common.Entities.User dbUser, bool readUserSkus, Dictionary<string, Common.Entities.User> dbUsersByAadId = null, DateTime? lastUpdatedUtc = null, HashSet<int> fallbackLicenceUserIds = null, HashSet<UserLicenseAssignment> fallbackDesiredLicences = null)
        {
            await _dataMapper.UpdateUserMetadata(db, graphUser, allGraphUsers, dbUser, dbUsersByAadId, allDbUsers, lastUpdatedUtc);

            // This is only done per user if can't be done at tenant level (due to extra permission)
            if (readUserSkus)
            {
                var desired = await _licenseProcessor.BuildDesiredAssignmentsForUser(db, graphUser, dbUser);
                if (desired == null)
                {
                    return;
                }
                if (fallbackLicenceUserIds == null || fallbackDesiredLicences == null)
                {
                    await _licenseProcessor.ReconcileCollectedUserLicenses(db, new[] { dbUser.ID }, desired);
                    return;
                }

                fallbackLicenceUserIds.Add(dbUser.ID);
                foreach (var assignment in desired)
                {
                    fallbackDesiredLicences.Add(assignment);
                }
            }
        }

        /// <summary>
        /// Inserts missing users into DB using two-phase approach: fast bulk insert, then metadata enrichment.
        /// Delegates to UserInsertProcessor for the heavy lifting.
        /// </summary>
        private async Task<List<Common.Entities.User>> InsertMissingUsersFenced(AnalyticsEntitiesContext db, List<GraphUser> allGraphUsers, List<Common.Entities.User> graphMentionedDbUsers, bool readUserSkus, DateTime? importCycleLastUpdatedUtc, HashSet<int> fallbackLicenceUserIds, HashSet<UserLicenseAssignment> fallbackDesiredLicences, Dictionary<string, Common.Entities.User> existingDbUsersByAadId)
        {
            await EnsureWriterLockHeldAsync("user insert");
            return await InsertMissingUsers(db, allGraphUsers, graphMentionedDbUsers, readUserSkus, importCycleLastUpdatedUtc, fallbackLicenceUserIds, fallbackDesiredLicences, existingDbUsersByAadId);
        }

        /// <param name="existingDbUsersByAadId">
        /// Every in-scope users row already in the database, by object id, for resolving new users' managers. Null
        /// resolves them against <paramref name="graphMentionedDbUsers"/> only, which misses a manager Graph did not
        /// report in this read.
        /// </param>
        public async Task<List<Common.Entities.User>> InsertMissingUsers(AnalyticsEntitiesContext db, List<GraphUser> allGraphUsers, List<Common.Entities.User> graphMentionedDbUsers, bool readUserSkus, DateTime? importCycleLastUpdatedUtc = null, HashSet<int> fallbackLicenceUserIds = null, HashSet<UserLicenseAssignment> fallbackDesiredLicences = null, Dictionary<string, Common.Entities.User> existingDbUsersByAadId = null)
        {
            // Ensure cache and helpers are initialized (for direct method calls from tests)
            if (_userMetaCache == null)
            {
                _userMetaCache = new UserMetadataCache(db);
            }
            if (_dataMapper == null)
            {
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, new SqlUserLookupStore(db), _clock);
            }
            if (_licenseProcessor == null)
            {
                _licenseProcessor = new UserLicenseProcessor(_logger, _userLoader, _userMetaCache) { WriteFence = () => _writeFence() };
            }

            return await _insertProcessor.InsertMissingUsers(
                db,
                allGraphUsers,
                graphMentionedDbUsers,
                readUserSkus,
                _userMetaCache,
                _dataMapper,
                _licenseProcessor,
                async (ctx, graphUser, allGraph, allDb, dbUser, readSkus, dbByAadId) =>
                    await UpdateDbUserWithGraphData(ctx, graphUser, allGraph, allDb, dbUser, readSkus, dbByAadId, importCycleLastUpdatedUtc, fallbackLicenceUserIds, fallbackDesiredLicences),
                existingDbUsersByAadId);
        }

        /// <summary>
        /// Get database users that match Graph users by UPN (public wrapper for testing)
        /// </summary>
        public List<Common.Entities.User> GetDbUsersFromGraphUsers(List<GraphUser> allGraphUsers, List<Common.Entities.User> allDbUsers)
        {
            // Ensure mapper is initialized
            if (_dataMapper == null && _userMetaCache != null)
            {
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, _clock);
            }

            if (_dataMapper != null)
            {
                return _dataMapper.GetDbUsersFromGraphUsers(allGraphUsers, allDbUsers);
            }
            else
            {
                // Fallback implementation
                var dbUsersByUpn = allDbUsers
                    .Where(u => !string.IsNullOrEmpty(u.UserPrincipalName))
                    .ToDictionary(u => u.UserPrincipalName.ToLower(), u => u, StringComparer.OrdinalIgnoreCase);

                var users = new List<Common.Entities.User>();
                foreach (var graphUser in allGraphUsers)
                {
                    var upn = graphUser.UserPrincipalName?.ToLower();
                    if (!string.IsNullOrEmpty(upn) && dbUsersByUpn.TryGetValue(upn, out var dbUser))
                    {
                        users.Add(dbUser);
                    }
                }
                return users;
            }
        }

        /// <summary>
        /// Update basic user properties from Graph user (internal method for backward compatibility)
        /// </summary>
        internal Common.Entities.User UpdateDbUserFromGraphUser(Common.Entities.User dbUser, GraphUser graphUser)
        {
            // Ensure mapper is initialized
            if (_dataMapper == null && _userMetaCache != null)
            {
                _dataMapper = new UserDataMapper(_logger, _userMetaCache, _clock);
            }

            // If mapper is available, use it; otherwise do direct mapping
            if (_dataMapper != null)
            {
                return _dataMapper.UpdateDbUserFromGraphUser(dbUser, graphUser);
            }
            else
            {
                // Fallback for edge cases where mapper isn't initialized. Uses the same extracted
                // mapping rule as UserDataMapper so the two copies cannot drift apart (#371).
                var plan = UserMetadataMappingRules.BuildPlan(graphUser);
                dbUser.AccountEnabled = plan.AccountEnabled;
                dbUser.PostalCode = plan.PostalCode;
                dbUser.AzureAdId = plan.AzureAdId;
                dbUser.Mail = plan.Mail;
                return dbUser;
            }
        }
    }
}
