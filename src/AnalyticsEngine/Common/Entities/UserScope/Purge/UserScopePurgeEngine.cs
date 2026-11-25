using Common.Entities.State;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserScope.Purge
{
    /// <summary>How a call to <see cref="UserScopePurgeEngine.RunAsync"/> ended.</summary>
    public enum UserScopePurgeRunOutcome
    {
        Completed,
        Failed,
        Cancelled,

        /// <summary>
        /// The host is stopping or the coverage fence still needs recovery. The job remains active so startup
        /// or a later status request can try again under the purge lock.
        /// </summary>
        Paused,

        /// <summary>Another session holds the purge lock - a purge is running - or this purge is no longer active.</summary>
        NotClaimed,

        /// <summary>The run could not prove it still owned the active job after losing its SQL session or its state fence.</summary>
        LockLost,
    }

    /// <summary>
    /// Runs one purge: works out who is outside the scope, then applies <see cref="UserScopePurgePlan.Steps"/> in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A run that can safely resume starts from the beginning with a fresh snapshot. Each
    /// step deletes or anonymises whatever still matches and user rows go last, so starting again finishes the job - and
    /// it asks Graph who is outside the scope now, rather than trusting a list chosen before the restart. So the list of
    /// users being removed is never stored anywhere: it exists only in the purge's session, as <c>#purge_candidates</c>.
    /// </para>
    /// <para>
    /// What the administrator confirmed binds every run: before anything is deleted, a run stops if more people would
    /// have been removed in all than the number they confirmed (<see cref="UserScopePurgeJob.ConfirmedOutsideScopeCount"/>),
    /// and one that had to read the groups again stops if they no longer resolve to the people it was confirmed against
    /// (<see cref="UserScopePurgeJob.ScopeMembershipFingerprint"/>).
    /// A takeover with an unaccounted user-delete window stops before taking another snapshot. Its count is a
    /// lower bound, not evidence that the remaining confirmation can be spent.
    /// </para>
    /// <para>
    /// Only one purge runs at a time, on any web app instance: the run holds the purge lock on its session (see
    /// <see cref="UserScopePurgeDatabase"/>). Its record - progress, counts, who started it - is kept in
    /// <see cref="UserScopePurgeStateStore"/>, never in the analytics database. Logs carry job ids, table names and counts
    /// only, never a person's identity.
    /// </para>
    /// </remarks>
    public sealed class UserScopePurgeEngine
    {
        private static readonly int[] TransientSqlErrors =
        {
            -2, 64, 121, 233, 1205, 4060, 10053, 10054, 10060, 10928, 10929, 11001, 40143, 40197, 40501, 40613, 49918, 49919, 49920,
        };

        /// <summary>
        /// Errors after which the session's connection can't be trusted, even when it still says it is open (a killed or
        /// dropped session reports -1, "physical connection is not usable"). The session is opened again.
        /// </summary>
        private static readonly int[] ConnectionLostSqlErrors = { -1, 64, 121, 233, 10053, 10054, 10060, 11001, 40143, 40197, 40613 };

        /// <summary>
        /// Whether a statement's failure means the purge's session has gone, so its connection must be dropped and the
        /// session opened again - taking the purge lock again with it. Decided by the error as well as the connection's
        /// state, because a killed session's next command fails with -1 while the connection still says <c>Open</c>.
        /// </summary>
        internal static bool IsConnectionLost(int sqlErrorNumber, ConnectionState connectionState)
            => ConnectionLostSqlErrors.Contains(sqlErrorNumber) || connectionState != ConnectionState.Open;

        /// <summary>Whether a statement that failed this way is tried again: a transient error, or a lost session.</summary>
        internal static bool IsRetryable(int sqlErrorNumber, ConnectionState connectionState)
            => TransientSqlErrors.Contains(sqlErrorNumber) || IsConnectionLost(sqlErrorNumber, connectionState);

        private readonly UserScopePurgeDatabase _database;
        private readonly UserScopePurgeStateStore _state;
        private readonly UsageReportCoverageStore _coverage;
        private readonly ILogger _logger;
        private DateTime _lastStateCheckUtc = DateTime.MinValue;

        public UserScopePurgeEngine(UserScopePurgeDatabase database, UserScopePurgeStateStore state, ILogger logger)
            : this(database, state, logger, UsageReportCoverageStore.Open(new Config.AppConfig(), logger)) { }

        internal UserScopePurgeEngine(UserScopePurgeDatabase database, UserScopePurgeStateStore state, ILogger logger,
            UsageReportCoverageStore coverage)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>Test hook: runs after every window, with the step index and the window's 1-based number within the step.</summary>
        internal Func<int, int, Task> AfterWindow { get; set; }

        /// <summary>Test hook: overrides every step's window size.</summary>
        internal int? WindowOverride { get; set; }

        /// <summary>Test hook: overrides <see cref="UserScopePurgePlan.BatchSize"/>.</summary>
        internal int? BatchSizeOverride { get; set; }

        /// <summary>
        /// Test hook: runs before each user the one-at-a-time fallback deletes, after it chose them - where an importer
        /// can still add data about them.
        /// </summary>
        internal Func<int, Task> BeforeOneByOneDelete { get; set; }

        /// <summary>Test hook: overrides the pause between retries of a transient database error.</summary>
        internal TimeSpan? RetryDelayOverride { get; set; }

        internal TimeSpan CoverageWriteTimeout { get; set; } = TimeSpan.FromSeconds(5);
        internal Action<SqlTransaction> CommitUsageTransaction { get; set; } = transaction => transaction.Commit();

        /// <summary>How often, at most, progress is saved and a stop request looked for inside a step. Always between steps.</summary>
        internal TimeSpan StateCheckInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Test hook: the users to purge, instead of working them out from the scope - so a test never lets a real scope
        /// loose on a shared database. A scope provider given alongside is still read and checked, but never searched.
        /// </summary>
        internal IReadOnlyCollection<int> CandidatesOverride { get; set; }

        private int BatchSize => BatchSizeOverride ?? UserScopePurgePlan.BatchSize;

        /// <summary>Runs purge <paramref name="jobId"/> to the end, or until it is stopped or the host shuts down.</summary>
        /// <param name="scopeProvider">
        /// Where the scope comes from. Only a fresh, complete resolution with at least one member is ever used.
        /// </param>
        /// <param name="preResolved">
        /// The fresh resolution the purge was confirmed against, when it is still in hand - so starting a purge does not
        /// read the groups from Graph twice. Ignored unless it is complete.
        /// </param>
        /// <param name="lockedSession">
        /// A session already holding the purge lock - the request that started the purge took it - which this run takes
        /// over and closes. Without one the run takes the lock itself, and does nothing while another purge holds it.
        /// </param>
        public async Task<UserScopePurgeRunOutcome> RunAsync(int jobId, IUserImportScopeProvider scopeProvider,
            UserImportScopeResolution preResolved = null, CancellationToken stopToken = default, UserScopePurgeSession lockedSession = null)
        {
            var session = lockedSession ?? await _database.TryOpenPurgeSessionAsync(TimeSpan.Zero, stopToken).ConfigureAwait(false);
            if (session == null)
            {
                return UserScopePurgeRunOutcome.NotClaimed;
            }

            using (session)
            {
                try
                {
                    var job = await _state.TryClaimAsync(jobId, () => session.CheckLockAsync(stopToken), stopToken).ConfigureAwait(false);
                    if (job == null) return UserScopePurgeRunOutcome.NotClaimed;
                    Job = job;
                    session.CheckReconnectedOwnerAsync = token => _state.RenewOwnerAsync(job.Id, job.RunToken, async () =>
                    {
                        if (session.CoverageFence != null && !await _coverage.IsActivePurgeAsync(session.CoverageFence, token).ConfigureAwait(false))
                            throw new UserScopePurgeLockLostException();
                        await session.CheckLockAsync(token).ConfigureAwait(false);
                    }, token);
                    return await RunClaimedAsync(job, session, scopeProvider, preResolved, stopToken).ConfigureAwait(false);
                }
                catch (UserScopePurgeLockLostException)
                {
                    _logger.LogWarning($"User scope purge {jobId}: this worker can no longer prove ownership; stopping without updating the purge.");
                    return UserScopePurgeRunOutcome.LockLost;
                }
            }
        }

        /// <summary>
        /// The purge's record as this run left it - including anything it could not save - or null when the run didn't
        /// claim one. For the audit event at the end, which must not depend on reading the record back from the store.
        /// </summary>
        public UserScopePurgeJob Job { get; private set; }

        private async Task<UserScopePurgeRunOutcome> RunClaimedAsync(UserScopePurgeJob job, UserScopePurgeSession session,
            IUserImportScopeProvider scopeProvider, UserImportScopeResolution preResolved, CancellationToken stopToken)
        {
            var openingFence = _coverage.IsDurable;
            var mayHaveFence = job.RunNumber > 1 || job.StartedUtc.HasValue;
            try
            {
                if (_coverage.IsDurable)
                {
                    // A resumed, cancelled job must also close an abandoned fence. Bind its replacement
                    // to this run, not the job id that another host may have taken over.
                    await CoverageWriteAsync(() => _coverage.StartScopePurgeAsync(job.RunToken, async () =>
                    {
                        await CheckSessionForStateWriteAsync(session, stopToken).ConfigureAwait(false);
                        mayHaveFence = true; // Even if Table never acknowledges the pending write.
                    }, stopToken, job.OwnsEarlierFence), stopToken).ConfigureAwait(false);
                    session.CoverageFence = job.RunToken;
                    openingFence = false;
                }
                else if (_state.IsDurable && mayHaveFence)
                {
                    _logger.LogWarning($"User scope purge {job.Id}: durable report coverage is missing on takeover. Leaving the job active until its fence can be checked.");
                    return UserScopePurgeRunOutcome.Paused;
                }
                if (job.CancelRequested || session.StopRequestedOnReconnect)
                {
                    return await EndAsync(job, session, UserScopePurgeStates.Cancelled,
                        job.HasUnaccountedUserDeletes ? UserScopePurgeErrorCodes.UserDeleteOutcomeUnknown : null, null).ConfigureAwait(false);
                }
                if (job.HasUnaccountedUserDeletes)
                    return await EndAsync(job, session, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.UserDeleteOutcomeUnknown, null).ConfigureAwait(false);

                var snapshot = await SnapshotAsync(job, session, scopeProvider, preResolved, stopToken).ConfigureAwait(false);
                if (snapshot != null)
                {
                    return snapshot.Value;
                }

                var steps = UserScopePurgePlan.Steps;
                int? unknownUserId = null;
                while (job.StepIndex >= 1 && job.StepIndex <= steps.Count)
                {
                    var step = steps[job.StepIndex - 1];
                    if (step.Kind == UserScopePurgeStepKind.Calls && unknownUserId == null)
                    {
                        unknownUserId = await WithRetriesAsync(session, () => GetOrCreateUnknownUserAsync(session.Connection, stopToken), stopToken).ConfigureAwait(false);
                    }

                    var outcome = await RunStepAsync(job, session, step, unknownUserId, stopToken).ConfigureAwait(false);
                    if (outcome != null)
                    {
                        return outcome.Value;
                    }

                    job.StepIndex++;
                    job.StepAfter = null;
                    job.Phase = UserScopePurgePlan.PhaseOf(job.StepIndex);
                    if (await SaveAndCheckForStopAsync(job, session, true, stopToken).ConfigureAwait(false))
                    {
                        _logger.LogInformation($"User scope purge {job.Id}: stopped on request after step {job.StepIndex - 1}.");
                        return await EndAsync(job, session, UserScopePurgeStates.Cancelled, null, null).ConfigureAwait(false);
                    }
                }

                job.UsersSkipped = await WithRetriesAsync(session, () => session.CountRemainingCandidatesAsync(), stopToken).ConfigureAwait(false);
                job.Phase = UserScopePurgePhases.Done;
                _logger.LogInformation($"User scope purge {job.Id}: finished. {job.UsersDeleted:N0} of {job.CandidateCount:N0} user(s) outside the scope " +
                    $"were removed, {job.RowsAffected.Values.Sum():N0} row(s) deleted or anonymised" +
                    (job.UsersSkipped > 0
                        ? $"; {job.UsersSkipped:N0} user(s) were kept because new data about them arrived while it ran - run the purge again to remove them."
                        : "."));
                return await EndAsync(job, session, UserScopePurgeStates.Completed, null, null).ConfigureAwait(false);
            }
            catch (PurgeStopRequestedException)
            {
                return await EndAsync(job, session, UserScopePurgeStates.Cancelled, null, null).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is UserScopePurgeLockLostException) && !(ex is OperationCanceledException && stopToken.IsCancellationRequested))
            {
                if (openingFence && mayHaveFence)
                {
                    _logger.LogWarning(ex, $"User scope purge {job.Id}: coverage fence ownership could not be confirmed. Leaving the job active for recovery.");
                    return UserScopePurgeRunOutcome.Paused;
                }
                var sql = ex as SqlException ?? ex.InnerException as SqlException;
                var stateLost = ex is UserScopePurgeStateUnavailableException;
                var code = job.HasUnaccountedUserDeletes ? UserScopePurgeErrorCodes.UserDeleteOutcomeUnknown
                    : sql != null ? UserScopePurgeErrorCodes.DatabaseError
                    : stateLost ? UserScopePurgeErrorCodes.StateUnavailable
                    : UserScopePurgeErrorCodes.Unexpected;
                var detail = sql != null ? $"{ex.GetType().Name}: SQL error {sql.Number}"
                    : stateLost ? $"{ex.GetType().Name}: {ex.InnerException?.GetType().Name}"
                    : ex.GetType().Name;
                _logger.LogError(ex, $"User scope purge {job.Id}: failed at step {job.StepIndex} ({job.Phase}) - {detail}. " +
                    "What was removed before this point stays removed; start the purge again to finish it.");
                try
                {
                    return await EndAsync(job, session, UserScopePurgeStates.Failed, code, detail).ConfigureAwait(false);
                }
                catch (Exception finishError) when (!(finishError is UserScopePurgeLockLostException))
                {
                    _logger.LogError(finishError, $"User scope purge {job.Id}: could not record the failure ({finishError.GetType().Name}).");
                    return session.CoverageFence == null ? UserScopePurgeRunOutcome.Failed : UserScopePurgeRunOutcome.Paused;
                }
            }
            catch (OperationCanceledException)
            {
                await PauseQuietlyAsync(job, session).ConfigureAwait(false);
                return UserScopePurgeRunOutcome.Paused;
            }
        }

        /// <summary>
        /// Saves the purge's progress and says whether a stop has been asked for: at most every
        /// <see cref="StateCheckInterval"/> unless <paramref name="force"/>, because the store may be a network call away.
        /// </summary>
        private async Task<bool> SaveAndCheckForStopAsync(UserScopePurgeJob job, UserScopePurgeSession session, bool force, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _lastStateCheckUtc < StateCheckInterval)
            {
                return false;
            }

            _lastStateCheckUtc = now;
            job.UpdatedUtc = now;
            await CheckSessionForStateWriteAsync(session, token).ConfigureAwait(false);
            await _state.SaveOwnedAsync(job, token).ConfigureAwait(false);
            return session.StopRequestedOnReconnect || await _state.IsCancelRequestedAsync(job.Id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Works out who is outside the scope and loads them into the session's <c>#purge_candidates</c>. Null when the
        /// purge should carry on; otherwise how it ended.
        /// </summary>
        private async Task<UserScopePurgeRunOutcome?> SnapshotAsync(UserScopePurgeJob job, UserScopePurgeSession session,
            IUserImportScopeProvider scopeProvider, UserImportScopeResolution preResolved, CancellationToken stopToken)
        {
            // Set the first time a purge gets past here, so a purge that has it is starting again after a restart. Its
            // position is kept until it is past the checks below, so a restart they refuse still says where it got to.
            var restarted = job.StartedUtc != null;

            if (FilterChangedSince(job, scopeProvider))
            {
                if (restarted)
                {
                    // Changing the setting restarted the web app. The people being removed were chosen under the old
                    // filter and some may be inside the new one, so stop rather than carry on.
                    _logger.LogWarning($"User scope purge {job.Id}: UserGroupsFilter changed while the purge was running, so it stopped. " +
                        "What it removed before the change stays removed; start the purge again to check who is outside the new filter.");
                    return await EndAsync(job, session, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.FilterChangedWhileRunning, null).ConfigureAwait(false);
                }

                _logger.LogWarning($"User scope purge {job.Id}: UserGroupsFilter changed after the purge was confirmed, so nobody was removed.");
                return await EndAsync(job, session, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.FilterChanged, null).ConfigureAwait(false);
            }

            List<int> candidates;
            UserImportScope scope = null;
            var scopeDescription = string.Empty;
            if (CandidatesOverride == null || scopeProvider != null)
            {
                var confirmed = preResolved != null && preResolved.Status == UserImportScopeStatus.Resolved;
                var resolution = confirmed
                    ? preResolved
                    : scopeProvider == null ? null : await scopeProvider.RefreshAsync().ConfigureAwait(false);

                if (resolution == null || resolution.Status != UserImportScopeStatus.Resolved)
                {
                    _logger.LogWarning($"User scope purge {job.Id}: the UserGroupsFilter groups could not be read completely, so nobody " +
                        (restarted ? "more " : "") + "was removed.");
                    return await EndAsync(job, session, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.ScopeUnavailable, resolution?.FailureKind?.ToString()).ConfigureAwait(false);
                }

                if (resolution.Members == null || resolution.Members.Count == 0)
                {
                    _logger.LogWarning($"User scope purge {job.Id}: UserGroupsFilter resolved to nobody, so the purge was refused rather than remove every user.");
                    return await EndAsync(job, session, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.ScopeEmpty, null).ConfigureAwait(false);
                }

                // The groups were read again - the purge is starting again after a restart, or the instance that started it
                // has gone - rather than taken from the confirmation. If they no longer resolve to the same people, the
                // people this run would remove are not the ones the administrator was shown, so it stops.
                if (!confirmed && MembershipChangedSince(job, resolution))
                {
                    _logger.LogWarning($"User scope purge {job.Id}: the members of the UserGroupsFilter groups changed after the purge was confirmed, " +
                        "so it stopped and removed nobody " + (restarted ? "more. What it removed before the change stays removed; " : "else. ") +
                        "start the purge again to confirm who is outside the scope now.");
                    return await EndAsync(job, session, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.ScopeMembershipChanged, null).ConfigureAwait(false);
                }

                scope = UserImportScope.ForMembers(resolution.Members, "purge");
                scopeDescription = $" of {resolution.Members.Count:N0} member(s)";
            }

            if (CandidatesOverride != null)
            {
                candidates = CandidatesOverride.ToList();
            }
            else
            {
                candidates = await FindUsersOutsideScopeAsync(scope).ConfigureAwait(false);
            }

            // Never more people in all than the administrator agreed to remove. Checked against the people actually found,
            // before anything is deleted: the importers keep adding users between the confirmation and here, and a purge
            // starting again after a restart has read the groups again.
            var ceiling = job.ConfirmedOutsideScopeCount ?? job.CandidateCount;
            var total = (long)job.UsersDeleted + candidates.Count;
            if (total > ceiling)
            {
                _logger.LogWarning($"User scope purge {job.Id}: {total:N0} user(s) are outside the scope{scopeDescription}" +
                    (job.UsersDeleted > 0 ? $" counting the {job.UsersDeleted:N0} already removed" : string.Empty) +
                    $", more than the {ceiling:N0} the purge was confirmed for, so it stopped and removed nobody " +
                    (restarted ? "more. What it removed before stays removed; " : "else. ") +
                    "check the groups, then start the purge again to confirm the new number.");
                return await EndAsync(job, session, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.PurgeExceedsConfirmed,
                    $"{total} found, {ceiling} confirmed").ConfigureAwait(false);
            }

            var otherObjects = CandidatesOverride == null ? await FindUnlinkedCostObjectsOutsideScopeAsync(scope).ConfigureAwait(false) : null;

            job.StepIndex = 0;
            job.StepAfter = null;
            job.Phase = UserScopePurgePhases.Snapshot;
            await WithRetriesAsync(session, async () =>
            {
                await session.LoadCandidatesAsync(candidates, otherObjects, stopToken).ConfigureAwait(false);
                return true;
            }, stopToken).ConfigureAwait(false);

            if (!_coverage.IsDurable && candidates.Count != 0 && await WithRetriesAsync(session,
                () => HasCoveredFactsAsync(session.Connection, stopToken), stopToken).ConfigureAwait(false))
            {
                _logger.LogWarning($"User scope purge {job.Id}: candidates have daily usage-report history and Storage is not configured. " +
                    "Configure the web app's Storage connection string for the importer's state account, then start the purge again.");
                return await EndAsync(job, session, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.CoverageStorageRequired, null).ConfigureAwait(false);
            }

            job.CandidateCount = job.UsersDeleted + candidates.Count;
            job.State = UserScopePurgeStates.Running;
            job.UserDeletePending = false;
            job.StepIndex = 1;
            job.Phase = UserScopePurgePlan.PhaseOf(1);
            job.StartedUtc = job.StartedUtc ?? DateTime.UtcNow;
            _logger.LogInformation($"User scope purge {job.Id}: " + (restarted ? "starting again after a restart. " : string.Empty) +
                $"{candidates.Count:N0} user(s) are outside the scope{scopeDescription}; removing everything stored about them.");

            if (await SaveAndCheckForStopAsync(job, session, true, stopToken).ConfigureAwait(false))
            {
                return await EndAsync(job, session, UserScopePurgeStates.Cancelled, null, null).ConfigureAwait(false);
            }
            if (CandidatesOverride == null)
            {
                // No destructive work, including credits keyed only by Entra object id, before the coverage preflight.
                var unlinked = await WithRetriesAsync(session,
                    () => DeleteUnlinkedCreditsOutsideScopeAsync(session.Connection, scope, stopToken), stopToken).ConfigureAwait(false);
                if (unlinked > 0) Add(job, "copilot_studio_credit_user_daily", unlinked);
            }
            return null;
        }

        private static async Task<bool> HasCoveredFactsAsync(SqlConnection connection, CancellationToken token)
        {
            foreach (var source in UsageReportCoverageStore.WeeklySources)
            {
                var table = source + "_user_activity_log";
                if (!await TableExistsAsync(connection, table).ConfigureAwait(false)) continue;
                using (var command = new SqlCommand($@"SELECT TOP (1) 1 FROM #purge_candidates c
                    JOIN dbo.[{table}] t ON t.user_id = c.user_id;", connection) { CommandTimeout = 600 })
                    if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) != null) return true;
            }
            return false;
        }

        /// <summary>Whether <c>UserGroupsFilter</c> is no longer the one the purge was confirmed against.</summary>
        private static bool FilterChangedSince(UserScopePurgeJob job, IUserImportScopeProvider scopeProvider)
        {
            var current = scopeProvider?.Filter?.Fingerprint ?? string.Empty;
            return !string.IsNullOrEmpty(job.FilterFingerprint) && !string.Equals(job.FilterFingerprint, current, StringComparison.Ordinal);
        }

        /// <summary>Whether the groups now resolve to other people than the ones the purge was confirmed against.</summary>
        private static bool MembershipChangedSince(UserScopePurgeJob job, UserImportScopeResolution resolution)
            => !string.IsNullOrEmpty(job.ScopeMembershipFingerprint)
               && !string.Equals(job.ScopeMembershipFingerprint, resolution.Members.MembershipFingerprint, StringComparison.Ordinal);

        /// <summary>Users that the scope does not contain by object id, UPN or mail - never the anonymous Unknown User.</summary>
        internal async Task<List<int>> FindUsersOutsideScopeAsync(UserImportScope scope)
        {
            var outside = new List<int>();
            using (var connection = await _database.OpenAsync().ConfigureAwait(false))
            using (var command = new SqlCommand("SELECT id, user_name, mail, azure_ad_id FROM dbo.users WHERE user_name IS NULL OR user_name <> @unknown;", connection))
            {
                command.CommandTimeout = 600;
                command.Parameters.Add("@unknown", SqlDbType.VarChar, 250).Value = UserScopePurgePlan.UnknownUserName;
                using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var id = reader.GetInt32(0);
                        var upn = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var mail = reader.IsDBNull(2) ? null : reader.GetString(2);
                        var objectId = reader.IsDBNull(3) ? null : reader.GetString(3);
                        if (!scope.IsAnyInScope(objectId, upn, mail))
                        {
                            outside.Add(id);
                        }
                    }
                }
            }
            return outside;
        }

        /// <summary>
        /// Object ids in the Copilot credit history preview that no user row has and the scope leaves out. The preview
        /// reads every directory user, so it holds readings about people the imports never added a user row for.
        /// </summary>
        internal async Task<List<Guid>> FindUnlinkedCostObjectsOutsideScopeAsync(UserImportScope scope)
        {
            var outside = new List<Guid>();
            using (var connection = await _database.OpenAsync().ConfigureAwait(false))
            {
                var sources = new List<string>();
                foreach (var table in new[] { "copilot_cost_observations", "copilot_cost_history", "copilot_cost_work" })
                {
                    if (await TableExistsAsync(connection, table).ConfigureAwait(false))
                    {
                        sources.Add($"SELECT object_id FROM dbo.{table}");
                    }
                }
                if (sources.Count == 0)
                {
                    return outside;
                }

                using (var command = new SqlCommand($@"
SELECT x.object_id FROM ({string.Join(" UNION ", sources)}) x
WHERE NOT EXISTS (SELECT 1 FROM dbo.users u WHERE u.azure_ad_id = CONVERT(nvarchar(36), x.object_id));", connection))
                {
                    command.CommandTimeout = 600;
                    using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            var objectId = reader.GetGuid(0);
                            if (!scope.IsInScope(objectId))
                            {
                                outside.Add(objectId);
                            }
                        }
                    }
                }
            }
            return outside;
        }

        private static async Task<int> DeleteUnlinkedCreditsOutsideScopeAsync(SqlConnection connection, UserImportScope scope, CancellationToken token)
        {
            var outside = new List<string>();
            if (!await TableExistsAsync(connection, "copilot_studio_credit_user_daily").ConfigureAwait(false))
            {
                return 0;
            }

            using (var command = new SqlCommand(
                "SELECT DISTINCT entra_object_id FROM dbo.copilot_studio_credit_user_daily WHERE user_id IS NULL AND entra_object_id IS NOT NULL;", connection))
            {
                command.CommandTimeout = 600;
                using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        var objectId = reader.GetString(0);
                        if (!scope.IsInScope(objectId))
                        {
                            outside.Add(objectId);
                        }
                    }
                }
            }

            var deleted = 0;
            for (var i = 0; i < outside.Count; i += 500)
            {
                var chunk = outside.GetRange(i, Math.Min(500, outside.Count - i));
                var names = chunk.Select((_, n) => "@p" + n.ToString(CultureInfo.InvariantCulture)).ToList();
                using (var delete = new SqlCommand(
                    $"DELETE FROM dbo.copilot_studio_credit_user_daily WHERE user_id IS NULL AND entra_object_id IN ({string.Join(",", names)});", connection))
                {
                    delete.CommandTimeout = 600;
                    for (var n = 0; n < chunk.Count; n++)
                    {
                        delete.Parameters.Add(names[n], SqlDbType.NVarChar, 400).Value = chunk[n];
                    }
                    deleted += await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }
            return deleted;
        }

        /// <summary>
        /// Runs one step to its end. Null when it finished; otherwise how the purge ended (stopped on request).
        /// </summary>
        private async Task<UserScopePurgeRunOutcome?> RunStepAsync(UserScopePurgeJob job, UserScopePurgeSession session, UserScopePurgeStep step,
            int? unknownUserId, CancellationToken stopToken)
        {
            string keyType;
            var lateUsageGuard = "";
            var coverageSource = step.Schema == "dbo" && step.Kind == UserScopePurgeStepKind.Delete
                ? UsageReportCoverageStore.WeeklySources.SingleOrDefault(source => step.Table == source + "_user_activity_log") : null;
            // No matching facts at preflight. Never delete a later arrival under an in-memory ledger;
            // the final user step keeps the person and their new data for a future purge.
            if (coverageSource != null && !_coverage.IsDurable) return null;
            using (var connection = await _database.OpenAsync().ConfigureAwait(false))
            {
                keyType = IsWalkOfTheCandidates(step)
                    ? (await TableExistsAsync(connection, step.Table, step.Schema).ConfigureAwait(false) ? WalkOf(step).KeyType : null)
                    : await KeyTypeAsync(connection, step.Table, step.KeyColumn, step.Schema).ConfigureAwait(false);
                foreach (var required in step.Requires)
                {
                    if (keyType != null && !await TableExistsAsync(connection, required).ConfigureAwait(false))
                    {
                        _logger.LogInformation($"User scope purge {job.Id}: dbo.{required} is not in this database, skipping the {step.Schema}.{step.Table} step.");
                        return null;
                    }
                }
                if (step.Kind == UserScopePurgeStepKind.Users)
                {
                    // Some of these FKs cascade. Keep late rows from all five sources instead of
                    // silently erasing an unstaged day or relying on an FK error to keep it.
                    foreach (var table in UsageReportCoverageStore.WeeklySources.Select(source => source + "_user_activity_log"))
                        if (await TableExistsAsync(connection, table).ConfigureAwait(false))
                            lateUsageGuard += $" AND NOT EXISTS (SELECT 1 FROM dbo.[{table}] u WITH (HOLDLOCK, FORCESEEK) WHERE u.user_id = t.id)";
                }
            }

            if (keyType == null)
            {
                _logger.LogInformation($"User scope purge {job.Id}: {step.Schema}.{step.Table} is not in this database, skipping that step.");
                return null;
            }

            var window = WindowOverride ?? step.Window;
            var usageBatch = coverageSource == null ? null : new UsageBatch();
            var windowNumber = 0;
            long stepRows = 0;
            while (true)
            {
                stopToken.ThrowIfCancellationRequested();
                windowNumber++;

                WindowResult result;
                if (step.Kind == UserScopePurgeStepKind.Users)
                {
                    // The owner-checked intent must survive a disconnect between the SQL commit and
                    // the next progress write. Never replay an unacknowledged delete (including a
                    // partially executed one-at-a-time fallback) as though it removed zero people.
                    job.UserDeletePending = true;
                    if (await SaveAndCheckForStopAsync(job, session, true, stopToken).ConfigureAwait(false))
                    {
                        job.UserDeletePending = false;
                        throw new PurgeStopRequestedException();
                    }
                    result = await RunWindowAsync(session.Connection, step, keyType, job.StepAfter, window, unknownUserId, lateUsageGuard).ConfigureAwait(false);
                }
                else result = await WithRetriesAsync(session, () => coverageSource == null
                        ? RunWindowAsync(session.Connection, step, keyType, job.StepAfter, window, unknownUserId, lateUsageGuard)
                        : DeleteUsageWindowAsync(session, step, job.StepAfter, window, coverageSource, usageBatch, stopToken),
                    stopToken).ConfigureAwait(false);
                if (result.Done)
                {
                    if (step.Kind == UserScopePurgeStepKind.Users) job.UserDeletePending = false;
                    break;
                }

                stepRows += result.Affected;
                if (result.Counts != null)
                {
                    foreach (var count in result.Counts.Where(c => c.Value > 0))
                    {
                        Add(job, count.Key, count.Value);
                    }
                }
                else if (result.Affected > 0)
                {
                    Add(job, step.CountKey, result.Affected);
                    if (step.Kind == UserScopePurgeStepKind.Users)
                    {
                        job.UsersDeleted += result.Affected;
                    }
                }
                job.StepAfter = result.NextAfter;
                if (step.Kind == UserScopePurgeStepKind.Users) job.UserDeletePending = false;

                if (await SaveAndCheckForStopAsync(job, session, false, stopToken).ConfigureAwait(false))
                {
                    _logger.LogInformation($"User scope purge {job.Id}: stopped on request during {step.Table}.");
                    return await EndAsync(job, session, UserScopePurgeStates.Cancelled, null, null).ConfigureAwait(false);
                }

                if (AfterWindow != null)
                {
                    await AfterWindow(job.StepIndex, windowNumber).ConfigureAwait(false);
                }
            }

            if (stepRows > 0)
            {
                var outcome = step.Kind == UserScopePurgeStepKind.Calls ? "call(s) deleted or anonymised"
                    : step.Kind == UserScopePurgeStepKind.Update ? "row(s) changed in place"
                    : "row(s) deleted";
                _logger.LogInformation($"User scope purge {job.Id}: {(step.Schema == "dbo" ? "dbo." : string.Empty)}{step.CountKey} - {stepRows:N0} {outcome}.");
            }
            return null;
        }

        private sealed class WindowResult
        {
            public int Affected { get; set; }
            public string NextAfter { get; set; }
            public bool Done { get; set; }

            /// <summary>
            /// Rows per count key, for a window that deletes from some tables and anonymises others (the calls); null
            /// when every row the window touched counts under the step's own key.
            /// </summary>
            public Dictionary<string, long> Counts { get; set; }
        }

        // Keep the staged SQL IDs on their original session. A wide batch is consumed one Table transaction
        // per SQL delete, not rescanned for every 99 dates. Only confirmed deletions contribute to job progress.
        private sealed class UsageBatch
        {
            public SqlConnection Connection;
            public readonly List<DateTime> Days = new List<DateTime>();
            public readonly HashSet<DateTime> Revoked = new HashSet<DateTime>();
            public int End, Selected, Deleted, NextDay;
        }

        private async Task<WindowResult> DeleteUsageWindowAsync(UserScopePurgeSession session, UserScopePurgeStep step,
            string after, int window, string source, UsageBatch batch, CancellationToken token)
        {
            var connection = session.Connection;
            if (batch.Connection != connection &&
                !await StageUsageWindowAsync(connection, step, after, window, batch, token).ConfigureAwait(false))
                return new WindowResult { Done = true };

            var take = Math.Min(UsageReportCoverageStore.MaxPurgeDaysPerTransaction, batch.Days.Count - batch.NextDay);
            var next = batch.NextDay + take;
            var affected = 0;
            if (take > 0)
            {
                var range = batch.Days.Count == take ? "" : " AND b.report_day BETWEEN @from AND @through";
                var days = new List<DateTime>(take);
                // The delete is provisional until its exact dates have been revoked. A rejected
                // statement (or a staged row that no longer matches) never invalidates intact history.
                using (var transaction = connection.BeginTransaction())
                using (var delete = new SqlCommand($@"
DECLARE @removed TABLE (report_day date NOT NULL);
DELETE t OUTPUT CONVERT(date, deleted.[date]) INTO @removed
FROM #purge_usage_batch b INNER LOOP JOIN {step.QualifiedTable} t ON t.id = b.id
WHERE CONVERT(date, t.[date]) = b.report_day AND ({step.Match}){range};
SELECT report_day, COUNT(*) FROM @removed GROUP BY report_day;", connection, transaction) { CommandTimeout = 600 })
                {
                    if (range.Length != 0)
                    {
                        delete.Parameters.Add("@from", SqlDbType.Date).Value = batch.Days[batch.NextDay];
                        delete.Parameters.Add("@through", SqlDbType.Date).Value = batch.Days[next - 1];
                    }
                    using (var reader = await delete.ExecuteReaderAsync(token).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            var day = reader.GetDateTime(0);
                            affected += reader.GetInt32(1);
                            if (!batch.Revoked.Contains(day)) days.Add(day);
                        }
                    }
                    if (days.Count != 0)
                        await CoverageWriteAsync(() => RevokeBeforeCommitAsync(session.CoverageFence, source, days, token), token).ConfigureAwait(false);
                    // No retry or certificate restoration on a lost COMMIT response. Previously
                    // acknowledged units retain their counts; this unit's dates stay unverified.
                    try { CommitUsageTransaction(transaction); }
                    catch (Exception ex) { throw new InvalidOperationException("The usage purge commit was not acknowledged.", ex); }
                }
                if (batch.Revoked.Count + days.Count > UsageReportCoverageStore.MaximumDays) batch.Revoked.Clear();
                batch.Revoked.UnionWith(days);
            }
            batch.Deleted += affected;
            batch.NextDay = next;
            var done = next == batch.Days.Count;
            if (done) batch.Connection = null;
            return new WindowResult
            {
                Affected = affected,
                NextAfter = done && batch.Selected < BatchSize && batch.Deleted == batch.Selected ? KeyToString(batch.End) : after,
            };
        }

        private async Task RevokeBeforeCommitAsync(string fence, string source, IReadOnlyList<DateTime> days, CancellationToken token)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(CoverageWriteTimeout);
                var expired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (deadline.Token.Register(() => expired.TrySetResult(true)))
                {
                    var write = _coverage.RevokeScopeDaysAsync(fence, source, days, deadline.Token);
                    if (await Task.WhenAny(write, expired.Task).ConfigureAwait(false) != write)
                    {
                        // Do not hold SQL locks for a transport which ignored cancellation. A request
                        // already sent still carries the old fence ETag; it cannot change a later run.
                        _ = write.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("Usage coverage revocation exceeded the SQL transaction's wait budget.");
                    }
                    await write.ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                }
            }
        }

        private async Task<bool> StageUsageWindowAsync(SqlConnection connection, UserScopePurgeStep step, string after,
            int window, UsageBatch batch, CancellationToken token)
        {
            var afterWhere = after == null ? "" : "WHERE t.id > @after ";
            var afterAnd = after == null ? "" : " AND t.id > @after";
            var sql = $@"
SET NOCOUNT ON;
TRUNCATE TABLE #purge_usage_batch;
DECLARE @end int = (SELECT MAX(w.id) FROM (SELECT TOP (@window) t.id FROM {step.QualifiedTable} t {afterWhere}ORDER BY t.id) w);
INSERT #purge_usage_batch (id, report_day)
SELECT TOP (@batch) t.id, CONVERT(date, t.[date]) FROM {step.QualifiedTable} t WITH (INDEX(1))
WHERE t.id <= @end{afterAnd} AND ({step.Match});
SELECT @end, @@ROWCOUNT;
SELECT DISTINCT report_day FROM #purge_usage_batch ORDER BY report_day;";
            using (var command = new SqlCommand(sql, connection) { CommandTimeout = 600 })
            {
                if (after != null) AddKeyParameter(command, "@after", "int", after);
                command.Parameters.Add("@window", SqlDbType.Int).Value = window;
                command.Parameters.Add("@batch", SqlDbType.Int).Value = BatchSize;
                using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    await reader.ReadAsync(token).ConfigureAwait(false);
                    if (reader.IsDBNull(0)) return false;
                    batch.End = reader.GetInt32(0);
                    batch.Selected = reader.GetInt32(1);
                    batch.Deleted = batch.NextDay = 0;
                    batch.Days.Clear();
                    await reader.NextResultAsync(token).ConfigureAwait(false);
                    while (await reader.ReadAsync(token).ConfigureAwait(false)) batch.Days.Add(reader.GetDateTime(0));
                }
            }
            batch.Connection = connection;
            return true;
        }

        private static async Task CoverageWriteAsync(Func<Task> write, CancellationToken token)
        {
            try { await write().ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is UserScopePurgeLockLostException) && !(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                throw new UserScopePurgeStateUnavailableException("Could not update usage-report coverage for the purge.", ex);
            }
        }

        private async Task<WindowResult> RunWindowAsync(SqlConnection connection, UserScopePurgeStep step, string keyType, string after, int window, int? unknownUserId, string lateUsageGuard)
        {
            var sql = step.Kind == UserScopePurgeStepKind.Calls ? BuildCallsWindowSql(keyType, after != null)
                : IsWalkOfTheCandidates(step) ? BuildCandidateWindowSql(step, after != null)
                : BuildWindowSql(step, keyType, after != null, lateUsageGuard);

            try
            {
                return await ExecuteWindowAsync(connection, sql, keyType, after, window, unknownUserId).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == 547 && step.Kind == UserScopePurgeStepKind.Users)
            {
                // Something new points at one of these users - the importers added data about them while the purge
                // ran. Remove the ones that are free, one at a time, and leave the rest for the next purge.
                return await DeleteUsersOneByOneAsync(connection, keyType, after, window, lateUsageGuard).ConfigureAwait(false);
            }
        }

        private async Task<WindowResult> ExecuteWindowAsync(SqlConnection connection, string sql, string keyType, string after,
            int window, int? unknownUserId)
        {
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 600;
                command.Parameters.Add("@window", SqlDbType.Int).Value = window;
                command.Parameters.Add("@batch", SqlDbType.Int).Value = BatchSize;
                command.Parameters.Add("@unknownUserId", SqlDbType.Int).Value = (object)unknownUserId ?? DBNull.Value;
                if (after != null)
                {
                    AddKeyParameter(command, "@after", keyType, after);
                }

                using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync().ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("A purge window returned no result.");
                    }
                    var result = new WindowResult
                    {
                        Affected = reader.GetInt32(0),
                        NextAfter = reader.IsDBNull(1) ? null : KeyToString(reader.GetValue(1)),
                        Done = reader.GetBoolean(2),
                    };

                    // Any further column is a count, named by its key.
                    for (var i = 3; i < reader.FieldCount; i++)
                    {
                        if (result.Counts == null)
                        {
                            result.Counts = new Dictionary<string, long>(StringComparer.Ordinal);
                        }
                        result.Counts[reader.GetName(i)] = reader.IsDBNull(i) ? 0 : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
                    }
                    return result;
                }
            }
        }

        private async Task<WindowResult> DeleteUsersOneByOneAsync(SqlConnection connection, string keyType, string after, int window, string lateUsageGuard)
        {
            var afterFilter = after == null ? "" : "WHERE t.[id] > @after ";
            var select = $@"
SET NOCOUNT ON;
DECLARE @end {keyType} = (SELECT TOP (1) w.k FROM (SELECT TOP (@window) t.[id] AS k FROM dbo.[users] t {afterFilter}ORDER BY t.[id]) w ORDER BY w.k DESC);
SELECT t.[id], @end FROM dbo.[users] t WITH (INDEX(1))
WHERE t.[id] <= @end{(after == null ? "" : " AND t.[id] > @after")}
  AND {UserScopePurgePlan.Steps.Last().Match}
  AND NOT EXISTS (SELECT 1 FROM dbo.audit_events e WHERE e.user_id = t.[id]);";

            var ids = new List<int>();
            object end = null;
            using (var command = new SqlCommand(select, connection))
            {
                command.CommandTimeout = 600;
                command.Parameters.Add("@window", SqlDbType.Int).Value = window;
                if (after != null) AddKeyParameter(command, "@after", keyType, after);
                using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        ids.Add(reader.GetInt32(0));
                        end = reader.GetValue(1);
                    }
                }
            }

            var deleted = 0;
            foreach (var id in ids)
            {
                if (BeforeOneByOneDelete != null)
                {
                    await BeforeOneByOneDelete(id).ConfigureAwait(false);
                }

                try
                {
                    // The audit-event check again, in the delete itself: audit_events.user_id has no foreign key, so an event
                    // that arrived since the select above would otherwise be orphaned rather than keep its user.
                    using (var delete = new SqlCommand(
                        $"DELETE t FROM dbo.users t WHERE t.id = @id AND NOT EXISTS (SELECT 1 FROM dbo.audit_events e WHERE e.user_id = @id){lateUsageGuard}; SELECT @@ROWCOUNT;", connection))
                    {
                        delete.Parameters.Add("@id", SqlDbType.Int).Value = id;
                        deleted += (int)await delete.ExecuteScalarAsync().ConfigureAwait(false);
                    }
                }
                catch (SqlException ex) when (ex.Number == 547)
                {
                    // Kept: counted as skipped when the purge finishes.
                    NoteUncoveredReference(ex);
                }
            }

            if (end == null)
            {
                // No candidate left in the window - find the window's end on its own to move past it.
                using (var command = new SqlCommand(
                    $"SELECT TOP (1) w.k FROM (SELECT TOP (@window) t.[id] AS k FROM dbo.[users] t {afterFilter}ORDER BY t.[id]) w ORDER BY w.k DESC;", connection))
                {
                    command.Parameters.Add("@window", SqlDbType.Int).Value = window;
                    if (after != null) AddKeyParameter(command, "@after", keyType, after);
                    end = await command.ExecuteScalarAsync().ConfigureAwait(false);
                }
            }

            return end == null || end == DBNull.Value
                ? new WindowResult { Done = true }
                : new WindowResult { Affected = deleted, NextAfter = KeyToString(end) };
        }

        private static readonly System.Text.RegularExpressions.Regex ConflictLocation = new System.Text.RegularExpressions.Regex(
            @"table ""(?:[^"".]+\.)?(?<table>[^""]+)"", column '(?<column>[^']+)'", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private readonly HashSet<string> _uncoveredReferencesLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A user the fallback could not delete is kept, and usually that is because data about them arrived while the
        /// purge ran, which the next purge removes. But if what still refers to them is a column no step takes care of -
        /// a table added to the database by hand, say - running the purge again keeps them every time, so that is logged
        /// once, by table and column only.
        /// </summary>
        private void NoteUncoveredReference(SqlException conflict)
        {
            var match = ConflictLocation.Match(conflict.Message ?? string.Empty);
            if (!match.Success)
            {
                return;
            }

            var reference = $"{match.Groups["table"].Value}.{match.Groups["column"].Value}";
            if (UserScopePurgePlan.Steps.Any(s => s.Handles.Contains(reference, StringComparer.OrdinalIgnoreCase))
                || !_uncoveredReferencesLogged.Add(reference))
            {
                return;
            }

            _logger.LogWarning($"User scope purge: dbo.{reference} refers to users and no step of the purge removes it, so the people " +
                "it refers to are kept however often the purge runs. Remove those rows by hand, then run the purge again.");
        }

        /// <summary>
        /// One window of a Delete, Update or Users step: find the last key of the next <c>@window</c> keys, then change
        /// at most <c>@batch</c> matching rows inside that range. Returns (rows changed, where to resume, finished).
        /// When the batch cap was reached the same window is run again, because rows may be left in it.
        /// </summary>
        /// <remarks>
        /// Every window reads its table through the clustered index (<c>WITH (INDEX(1))</c>), which the window walks: a range
        /// seek over just the window's keys. Left to itself, with <c>@end</c> a local variable it can't estimate, the
        /// optimiser read <c>audit_events</c> through <c>IX_user_id</c> instead - merging it with the candidates in user order
        /// and filtering on the window's range - which walks the whole index for every window: work that grows with the table
        /// on each of its windows, so the step grows with the square of the table. Measured on 2M audit events: the median
        /// window went from 238 ms to 112 ms with 20% of people outside the scope, and from 255 ms to 157 ms with 90%; on 5M
        /// events (20%) from 446 ms to 131 ms. <c>OPTION (RECOMPILE)</c> reached the same plan in most windows, but only most,
        /// and compiles on every one.
        /// </remarks>
        internal static string BuildWindowSql(UserScopePurgeStep step, string keyType, bool hasAfter, string lateUsageGuard = "")
        {
            var key = $"t.[{step.KeyColumn}]";
            var afterWhere = hasAfter ? $"WHERE {key} > @after " : "";
            var afterAnd = hasAfter ? $" AND {key} > @after" : "";
            var resumeAt = hasAfter ? "@after" : $"CAST(NULL AS {keyType})";
            var target = $"{step.QualifiedTable} t WITH (INDEX(1))";

            string action;
            switch (step.Kind)
            {
                case UserScopePurgeStepKind.Delete:
                    action = $"DELETE TOP (@batch) t FROM {target} WHERE {key} <= @end{afterAnd} AND ({step.Match});";
                    break;
                case UserScopePurgeStepKind.Update:
                    action = $"UPDATE TOP (@batch) t SET {step.Set} FROM {target} WHERE {key} <= @end{afterAnd} AND ({step.Match});";
                    break;
                case UserScopePurgeStepKind.Users:
                    // A user whose audit events are still there is kept: audit_events.user_id has no foreign key, so
                    // deleting the user would silently orphan them. Any other reference fails the statement (error 547),
                    // which RunWindowAsync handles one user at a time.
                    action = $"DELETE TOP (@batch) t FROM {target} WHERE {key} <= @end{afterAnd} AND ({step.Match}) " +
                             $"AND NOT EXISTS (SELECT 1 FROM dbo.audit_events e WHERE e.user_id = t.[id]){lateUsageGuard};";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step));
            }

            if (step.Lookup != null)
            {
                if (step.Kind != UserScopePurgeStepKind.Delete)
                {
                    throw new ArgumentOutOfRangeException(nameof(step), "Only a delete step can release a lookup.");
                }
                return BuildReleasingWindowSql(step, keyType, key, target, afterWhere, afterAnd, resumeAt);
            }

            return $@"
SET NOCOUNT ON;
DECLARE @end {keyType} = (SELECT TOP (1) w.k FROM (SELECT TOP (@window) {key} AS k FROM {step.QualifiedTable} t {afterWhere}ORDER BY {key}) w ORDER BY w.k DESC);
IF @end IS NULL
    SELECT CAST(0 AS int) AS affected, CAST(NULL AS {keyType}) AS next_after, CAST(1 AS bit) AS done;
ELSE
BEGIN
    {action}
    DECLARE @affected int = @@ROWCOUNT;
    SELECT @affected AS affected, CASE WHEN @affected >= @batch THEN {resumeAt} ELSE @end END AS next_after, CAST(0 AS bit) AS done;
END";
        }

        /// <summary>
        /// One window of a delete step whose rows point at a lookup (<see cref="UserScopePurgeStep.Lookup"/>): the rows'
        /// lookup ids are kept as they are deleted, and, in the same transaction, the lookup rows among them that nothing
        /// refers to any more are deleted too. Doing both in one transaction means a purge that stops between windows - or
        /// whose session drops - never leaves a lookup row behind that only the deleted rows used.
        /// </summary>
        /// <remarks>
        /// The importers keep running. A lookup row is only deleted when no column refers to it, checked in the delete
        /// itself, the same guard <c>Clean Old Data Data.sql</c> uses - with locking reads, so that under read-committed
        /// snapshot (Azure SQL's default) a reference an importer has written but not yet committed is waited for rather
        /// than missed: <c>copilot_interaction_keywords</c> cascades, so a missed one would be deleted with the phrase. If an
        /// importer adds a reference between that check and the delete, the foreign key refuses the delete (error 547): the
        /// statement alone is rolled back and tried again, when the new reference is seen and its lookup row kept. Counts
        /// come back per table, like the calls window's.
        /// </remarks>
        private static string BuildReleasingWindowSql(UserScopePurgeStep step, string keyType, string key, string target,
            string afterWhere, string afterAnd, string resumeAt)
        {
            var lookup = step.Lookup;
            var unreferenced = string.Concat(lookup.References.Select(reference =>
            {
                var dot = reference.IndexOf('.');
                return $"{Environment.NewLine}                      AND NOT EXISTS (SELECT 1 FROM dbo.[{reference.Substring(0, dot)}] x WITH (READCOMMITTEDLOCK) WHERE x.[{reference.Substring(dot + 1)}] = l.[id])";
            }));

            return $@"
SET NOCOUNT ON;
DECLARE @end {keyType} = (SELECT TOP (1) w.k FROM (SELECT TOP (@window) {key} AS k FROM {step.QualifiedTable} t {afterWhere}ORDER BY {key}) w ORDER BY w.k DESC);
IF @end IS NULL
    SELECT CAST(0 AS int) AS affected, CAST(NULL AS {keyType}) AS next_after, CAST(1 AS bit) AS done, CAST(0 AS int) AS [{step.Table}], CAST(0 AS int) AS [{lookup.Table}];
ELSE
BEGIN
    -- Statement errors leave the transaction open, so a refused lookup delete can be tried again within it.
    SET XACT_ABORT OFF;
    -- Through sp_executesql (it has parameters), so this temporary table belongs to this call and goes when it ends.
    CREATE TABLE #released (id int NULL);
    DECLARE @affected int = 0, @pruned int = 0, @attempt int = 0;
    BEGIN TRY
        BEGIN TRANSACTION;
        DELETE TOP (@batch) t OUTPUT deleted.[{lookup.Column}] INTO #released (id)
        FROM {target} WHERE {key} <= @end{afterAnd} AND ({step.Match});
        SET @affected = @@ROWCOUNT;
        WHILE @attempt < 3
        BEGIN
            BEGIN TRY
                DELETE l FROM dbo.[{lookup.Table}] l
                WHERE l.[id] IN (SELECT r.id FROM #released r WHERE r.id IS NOT NULL){unreferenced};
                SET @pruned = @@ROWCOUNT;
                BREAK;
            END TRY
            BEGIN CATCH
                -- An importer referred to one of them since the check: it is in use again, and stays.
                IF ERROR_NUMBER() <> 547 OR XACT_STATE() <> 1
                BEGIN
                    ;THROW;
                END
                SET @attempt += 1;
            END CATCH
        END
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
    SELECT @affected AS affected, CASE WHEN @affected >= @batch THEN {resumeAt} ELSE @end END AS next_after, CAST(0 AS bit) AS done,
           @affected AS [{step.Table}], @pruned AS [{lookup.Table}];
    DROP TABLE #released;
END";
        }

        /// <summary>Whether <paramref name="step"/> walks the purge's own list of people or object ids rather than its table.</summary>
        internal static bool IsWalkOfTheCandidates(UserScopePurgeStep step)
            => step.Kind == UserScopePurgeStepKind.DeleteByObject || step.Kind == UserScopePurgeStepKind.DeleteByCandidate;

        /// <summary>The temporary table, alias, key and key type a <see cref="IsWalkOfTheCandidates"/> step walks.</summary>
        private static (string Table, string Alias, string Key, string KeyType) WalkOf(UserScopePurgeStep step)
            => step.Kind == UserScopePurgeStepKind.DeleteByObject
                ? (UserScopePurgePlan.ObjectsTable, "o", "object_id", "uniqueidentifier")
                : (UserScopePurgePlan.CandidatesTable, "c", "user_id", "int");

        /// <summary>
        /// One window of a <see cref="UserScopePurgeStepKind.DeleteByObject"/> or
        /// <see cref="UserScopePurgeStepKind.DeleteByCandidate"/> step: the next <c>@window</c> object ids or people being
        /// purged, and at most <c>@batch</c> of their rows. The step's joins carry hints that seek each table from the
        /// window's own keys, so a window reads its own people's rows and nothing else, however many other people's rows
        /// share their days or runs - and however little the optimiser can estimate about a window bounded by a variable.
        /// </summary>
        /// <remarks>
        /// These steps only delete rows nothing refers to (their joins check), but the Copilot credit collector and the
        /// sent-email import keep writing. If one of them refers to a row between the check and the delete, the foreign key
        /// refuses the statement (error 547); it is tried again, when the check sees the new reference and leaves that row.
        /// </remarks>
        internal static string BuildCandidateWindowSql(UserScopePurgeStep step, bool hasAfter)
        {
            var walk = WalkOf(step);
            var key = $"{walk.Alias}.{walk.Key}";
            var afterWhere = hasAfter ? $"WHERE {key} > @after " : "";
            var afterAnd = hasAfter ? $" AND {key} > @after" : "";
            var resumeAt = hasAfter ? "@after" : $"CAST(NULL AS {walk.KeyType})";

            return $@"
SET NOCOUNT ON;
DECLARE @end {walk.KeyType} = (SELECT TOP (1) w.k FROM (SELECT TOP (@window) {key} AS k FROM {walk.Table} {walk.Alias} {afterWhere}ORDER BY {key}) w ORDER BY w.k DESC);
IF @end IS NULL
    SELECT CAST(0 AS int) AS affected, CAST(NULL AS {walk.KeyType}) AS next_after, CAST(1 AS bit) AS done;
ELSE
BEGIN
    DECLARE @affected int = 0, @attempt int = 0;
    WHILE @attempt < 3
    BEGIN
        BEGIN TRY
            DELETE TOP (@batch) t
            FROM {walk.Table} {walk.Alias} {step.Match}
            WHERE {key} <= @end{afterAnd};
            SET @affected = @@ROWCOUNT;
            BREAK;
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() <> 547 OR XACT_STATE() = -1
            BEGIN
                ;THROW;
            END
            SET @attempt += 1;
        END CATCH
    END
    SELECT @affected AS affected, CASE WHEN @affected >= @batch THEN {resumeAt} ELSE @end END AS next_after, CAST(0 AS bit) AS done;
END";
        }

        /// <summary>
        /// One window of Teams calls, in a transaction. A call on which everyone is being purged or is already the
        /// anonymous Unknown User is deleted with everything under it - exactly what the import stores for such a call,
        /// which is nothing. On the others the people being purged become Unknown User and their feedback goes.
        /// </summary>
        internal static string BuildCallsWindowSql(string keyType, bool hasAfter)
        {
            var afterWhere = hasAfter ? "WHERE t.[id] > @after " : "";
            var afterAnd = hasAfter ? " AND r.[id] > @after" : "";
            const string candidate = "EXISTS (SELECT 1 FROM " + UserScopePurgePlan.CandidatesTable + " c WHERE c.user_id = {0})";
            string IsCandidate(string expression) => string.Format(CultureInfo.InvariantCulture, candidate, expression);

            return $@"
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @end {keyType} = (SELECT TOP (1) w.k FROM (SELECT TOP (@window) t.[id] AS k FROM dbo.[call_records] t {afterWhere}ORDER BY t.[id]) w ORDER BY w.k DESC);
IF @end IS NULL
    SELECT CAST(0 AS int) AS affected, CAST(NULL AS {keyType}) AS next_after, CAST(1 AS bit) AS done;
ELSE
BEGIN
    -- This runs through sp_executesql (it has parameters), so these temporary tables belong to this call and go when it
    -- ends, even if it fails part-way. Only #purge_candidates lives on the purge session itself.
    CREATE TABLE #calls (id int NOT NULL PRIMARY KEY);
    CREATE TABLE #doomed (id int NOT NULL PRIMARY KEY);

    BEGIN TRANSACTION;

    INSERT INTO #calls (id)
    SELECT r.[id] FROM dbo.call_records r WITH (INDEX(1))
    WHERE r.[id] <= @end{afterAnd}
      AND ({IsCandidate("r.organizer_id")}
           OR EXISTS (SELECT 1 FROM dbo.call_sessions s WHERE s.call_record_id = r.[id] AND {IsCandidate("s.attendee_user_id")})
           OR EXISTS (SELECT 1 FROM dbo.call_feedback f WHERE f.call_id = r.[id] AND {IsCandidate("f.user_id")}));

    INSERT INTO #doomed (id)
    SELECT k.id FROM #calls k JOIN dbo.call_records r ON r.[id] = k.id
    WHERE (r.organizer_id = @unknownUserId OR {IsCandidate("r.organizer_id")})
      AND NOT EXISTS (SELECT 1 FROM dbo.call_sessions s
                      WHERE s.call_record_id = r.[id] AND s.attendee_user_id <> @unknownUserId AND NOT {IsCandidate("s.attendee_user_id")});

    DELETE m FROM dbo.call_session_call_modalities m JOIN dbo.call_sessions s ON s.[id] = m.call_session_id JOIN #doomed d ON d.id = s.call_record_id;
    DECLARE @modalities int = @@ROWCOUNT;
    DELETE s FROM dbo.call_sessions s JOIN #doomed d ON d.id = s.call_record_id;
    DECLARE @sessions int = @@ROWCOUNT;
    DELETE f FROM dbo.call_failures f JOIN #doomed d ON d.id = f.call_id;
    DECLARE @failures int = @@ROWCOUNT;
    DELETE f FROM dbo.call_feedback f JOIN #doomed d ON d.id = f.call_id;
    DECLARE @feedback int = @@ROWCOUNT;
    DELETE r FROM dbo.call_records r JOIN #doomed d ON d.id = r.[id];
    DECLARE @calls int = @@ROWCOUNT;

    DELETE f FROM dbo.call_feedback f JOIN #calls k ON k.id = f.call_id WHERE {IsCandidate("f.user_id")};
    SET @feedback += @@ROWCOUNT;
    UPDATE r SET organizer_id = @unknownUserId FROM dbo.call_records r JOIN #calls k ON k.id = r.[id] WHERE {IsCandidate("r.organizer_id")};
    DECLARE @organisers int = @@ROWCOUNT;
    UPDATE s SET attendee_user_id = @unknownUserId FROM dbo.call_sessions s JOIN #calls k ON k.id = s.call_record_id WHERE {IsCandidate("s.attendee_user_id")};
    DECLARE @attendees int = @@ROWCOUNT;

    COMMIT TRANSACTION;

    -- Deleted rows count under their table, anonymised ones under table.column, as every other step's do.
    SELECT (SELECT COUNT(*) FROM #calls) AS affected, @end AS next_after, CAST(0 AS bit) AS done,
           @calls AS [call_records], @sessions AS [call_sessions], @modalities AS [call_session_call_modalities],
           @failures AS [call_failures], @feedback AS [call_feedback],
           @organisers AS [call_records.organizer_id], @attendees AS [call_sessions.attendee_user_id];
    DROP TABLE #calls;
    DROP TABLE #doomed;
END";
        }

        /// <summary>The anonymous "Unknown User" the call import uses, created if the database has none yet.</summary>
        private static async Task<int> GetOrCreateUnknownUserAsync(SqlConnection connection, CancellationToken token)
        {
            const string sql = @"
SET NOCOUNT ON;
DECLARE @id int = (SELECT TOP (1) id FROM dbo.users WHERE user_name = @name ORDER BY id);
IF @id IS NULL
BEGIN
    INSERT INTO dbo.users (user_name) VALUES (@name);
    SET @id = SCOPE_IDENTITY();
END
SELECT @id;";
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.Add("@name", SqlDbType.VarChar, 250).Value = UserScopePurgePlan.UnknownUserName;
                return (int)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            }
        }

        private async Task<UserScopePurgeRunOutcome> EndAsync(UserScopePurgeJob job, UserScopePurgeSession session, string state, string errorCode, string errorDetail)
        {
            await CheckSessionForStateWriteAsync(session, CancellationToken.None).ConfigureAwait(false);
            if (state == UserScopePurgeStates.Completed && session.StopRequestedOnReconnect) state = UserScopePurgeStates.Cancelled;
            var now = DateTime.UtcNow;
            if (session.CoverageFence != null)
            {
                await CoverageWriteAsync(() => _coverage.EndScopePurgeAsync(session.CoverageFence, now), CancellationToken.None).ConfigureAwait(false);
                session.CoverageFence = null;
            }
            job.State = state;
            job.ErrorCode = errorCode;
            job.ErrorDetail = errorDetail;
            job.CompletedUtc = now;
            job.UpdatedUtc = now;
            if (state == UserScopePurgeStates.Completed)
            {
                job.Phase = UserScopePurgePhases.Done;
            }

            await _state.SaveOwnedAsync(job, CancellationToken.None).ConfigureAwait(false);

            switch (state)
            {
                case UserScopePurgeStates.Completed: return UserScopePurgeRunOutcome.Completed;
                case UserScopePurgeStates.Cancelled: return UserScopePurgeRunOutcome.Cancelled;
                default: return UserScopePurgeRunOutcome.Failed;
            }
        }

        private async Task PauseQuietlyAsync(UserScopePurgeJob job, UserScopePurgeSession session)
        {
            try
            {
                // Best effort on host shutdown: never wait out a full lock timeout for a progress update.
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    await CheckSessionForStateWriteAsync(session, deadline.Token).ConfigureAwait(false);
                    job.UpdatedUtc = DateTime.UtcNow;
                    await _state.SaveOwnedAsync(job, deadline.Token).ConfigureAwait(false);
                }
                _logger.LogInformation($"User scope purge {job.Id}: stopped because the web app is shutting down. " +
                    (_state.IsDurable
                        ? "It starts again when the web app next starts."
                        : "Its progress is kept in the web app's memory, so start it again from the portal to finish it."));
            }
            catch (Exception ex) when (!(ex is UserScopePurgeLockLostException))
            {
                _logger.LogWarning($"User scope purge {job.Id}: could not save its progress while stopping ({ex.GetType().Name}).");
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the purge's session, retrying transient database errors. Before each attempt
        /// the session is re-opened if its connection broke. Only the same active owner may reload its candidates.
        /// </summary>
        private async Task<T> WithRetriesAsync<T>(UserScopePurgeSession session, Func<Task<T>> action, CancellationToken stopToken, bool stateWrite = false)
        {
            var delay = RetryDelayOverride ?? TimeSpan.FromSeconds(2);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    stopToken.ThrowIfCancellationRequested();
                    await session.EnsureOpenAsync(stopToken).ConfigureAwait(false);
                    if (!stateWrite && session.StopRequestedOnReconnect) throw new PurgeStopRequestedException();
                    return await action().ConfigureAwait(false);
                }
                catch (SqlException ex) when (IsRetryable(ex.Number, session.Connection.State))
                {
                    // A deadlock or timeout leaves the connection usable, so keep it - and with it the purge lock. After a
                    // lost connection, drop it: the next attempt opens the session again first.
                    var lost = IsConnectionLost(ex.Number, session.Connection.State);
                    if (lost)
                    {
                        session.DiscardConnection();
                    }
                    if (attempt >= 5) throw;
                    _logger.LogWarning($"User scope purge: SQL error {ex.Number}{(lost ? " (connection lost)" : string.Empty)} (attempt {attempt} of 5); retrying.");
                    await Task.Delay(delay, stopToken).ConfigureAwait(false);
                    delay = TimeSpan.FromTicks(delay.Ticks * 2);
                }
            }
        }

        private Task CheckSessionForStateWriteAsync(UserScopePurgeSession session, CancellationToken token) =>
            WithRetriesAsync(session, () => session.CheckLockAsync(token), token, stateWrite: true);

        private sealed class PurgeStopRequestedException : Exception { }

        private static void Add(UserScopePurgeJob job, string table, long rows)
        {
            job.RowsAffected.TryGetValue(table, out var existing);
            job.RowsAffected[table] = existing + rows;
        }

        private static async Task<bool> TableExistsAsync(SqlConnection connection, string table, string schema = "dbo")
        {
            using (var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", connection))
            {
                command.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = schema + "." + table;
                return (int)await command.ExecuteScalarAsync().ConfigureAwait(false) == 1;
            }
        }

        /// <summary>The key column's SQL type (int, bigint or uniqueidentifier), or null when the table is not there.</summary>
        internal static async Task<string> KeyTypeAsync(SqlConnection connection, string table, string column, string schema = "dbo")
        {
            const string sql = @"
SELECT TYPE_NAME(c.system_type_id) FROM sys.columns c
WHERE c.object_id = OBJECT_ID(@table, N'U') AND c.name = @column;";
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.Add("@table", SqlDbType.NVarChar, 300).Value = schema + "." + table;
                command.Parameters.Add("@column", SqlDbType.NVarChar, 128).Value = column;
                var type = await command.ExecuteScalarAsync().ConfigureAwait(false) as string;
                switch (type)
                {
                    case null: return null;
                    case "int":
                    case "bigint":
                    case "uniqueidentifier":
                        return type;
                    default:
                        throw new NotSupportedException($"dbo.{table}.{column} is {type}; a purge step can only walk an int, bigint or uniqueidentifier key.");
                }
            }
        }

        private static void AddKeyParameter(SqlCommand command, string name, string keyType, string value)
        {
            switch (keyType)
            {
                case "int":
                    command.Parameters.Add(name, SqlDbType.Int).Value = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "bigint":
                    command.Parameters.Add(name, SqlDbType.BigInt).Value = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                default:
                    command.Parameters.Add(name, SqlDbType.UniqueIdentifier).Value = Guid.Parse(value);
                    break;
            }
        }

        private static string KeyToString(object value)
        {
            switch (value)
            {
                case Guid guid: return guid.ToString("D");
                case IFormattable formattable: return formattable.ToString(null, CultureInfo.InvariantCulture);
                default: return value?.ToString();
            }
        }
    }
}
