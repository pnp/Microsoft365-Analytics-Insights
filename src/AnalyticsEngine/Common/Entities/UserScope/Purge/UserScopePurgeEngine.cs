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
        /// The host is shutting down. The purge lock went with the session; a durable record lets the purge start again
        /// when the web app next starts.
        /// </summary>
        Paused,

        /// <summary>Another session holds the purge lock - a purge is running - or this purge is no longer active.</summary>
        NotClaimed,

        /// <summary>The purge's session broke, and another session took the purge lock before this one could reconnect.</summary>
        LockLost,
    }

    /// <summary>
    /// Runs one purge: works out who is outside the scope, then applies <see cref="UserScopePurgePlan.Steps"/> in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every run starts from the beginning with a fresh snapshot, including one that carries on after a restart. Each
    /// step deletes or anonymises whatever still matches and user rows go last, so starting again finishes the job - and
    /// it asks Graph who is outside the scope now, rather than trusting a list chosen before the restart. So the list of
    /// users being removed is never stored anywhere: it exists only in the purge's session, as <c>#purge_candidates</c>.
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
        private readonly ILogger _logger;
        private DateTime _lastStateCheckUtc = DateTime.MinValue;

        public UserScopePurgeEngine(UserScopePurgeDatabase database, UserScopePurgeStateStore state, ILogger logger)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>Test hook: runs after every window, with the step index and the window's 1-based number within the step.</summary>
        internal Func<int, int, Task> AfterWindow { get; set; }

        /// <summary>Test hook: overrides every step's window size.</summary>
        internal int? WindowOverride { get; set; }

        /// <summary>Test hook: overrides <see cref="UserScopePurgePlan.BatchSize"/>.</summary>
        internal int? BatchSizeOverride { get; set; }

        /// <summary>Test hook: overrides the pause between retries of a transient database error.</summary>
        internal TimeSpan? RetryDelayOverride { get; set; }

        /// <summary>How often, at most, progress is saved and a stop request looked for inside a step. Always between steps.</summary>
        internal TimeSpan StateCheckInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Test hook: the users to purge, instead of working them out from the scope - so a test never lets a real scope
        /// loose on a shared database.
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
                var job = await _state.GetAsync(jobId).ConfigureAwait(false);
                if (job == null || !job.IsActive)
                {
                    return UserScopePurgeRunOutcome.NotClaimed;
                }
                Job = job;
                return await RunClaimedAsync(job, session, scopeProvider, preResolved, stopToken).ConfigureAwait(false);
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
            try
            {
                if (job.CancelRequested)
                {
                    return await EndAsync(job, UserScopePurgeStates.Cancelled, null, null).ConfigureAwait(false);
                }

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
                        unknownUserId = await GetOrCreateUnknownUserAsync().ConfigureAwait(false);
                    }

                    var outcome = await RunStepAsync(job, session, step, unknownUserId, stopToken).ConfigureAwait(false);
                    if (outcome != null)
                    {
                        return outcome.Value;
                    }

                    job.StepIndex++;
                    job.StepAfter = null;
                    job.Phase = UserScopePurgePlan.PhaseOf(job.StepIndex);
                    if (await SaveAndCheckForStopAsync(job, force: true).ConfigureAwait(false))
                    {
                        _logger.LogInformation($"User scope purge {job.Id}: stopped on request after step {job.StepIndex - 1}.");
                        return await EndAsync(job, UserScopePurgeStates.Cancelled, null, null).ConfigureAwait(false);
                    }
                }

                job.UsersSkipped = await WithRetriesAsync(session, () => session.CountRemainingCandidatesAsync(), stopToken).ConfigureAwait(false);
                job.Phase = UserScopePurgePhases.Done;
                _logger.LogInformation($"User scope purge {job.Id}: finished. {job.UsersDeleted:N0} of {job.CandidateCount:N0} user(s) outside the scope " +
                    $"were removed, {job.RowsAffected.Values.Sum():N0} row(s) deleted or anonymised" +
                    (job.UsersSkipped > 0
                        ? $"; {job.UsersSkipped:N0} user(s) were kept because new data about them arrived while it ran - run the purge again to remove them."
                        : "."));
                return await EndAsync(job, UserScopePurgeStates.Completed, null, null).ConfigureAwait(false);
            }
            catch (UserScopePurgeLockLostException)
            {
                // Another session runs the purge now and writes its record, so leave the record alone.
                _logger.LogWarning($"User scope purge {job.Id}: its database session broke and another session took the purge over; this one is stopping.");
                return UserScopePurgeRunOutcome.LockLost;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && stopToken.IsCancellationRequested))
            {
                var sql = ex as SqlException ?? ex.InnerException as SqlException;
                var stateLost = ex is UserScopePurgeStateUnavailableException;
                var code = sql != null ? UserScopePurgeErrorCodes.DatabaseError
                    : stateLost ? UserScopePurgeErrorCodes.StateUnavailable
                    : UserScopePurgeErrorCodes.Unexpected;
                var detail = sql != null ? $"{ex.GetType().Name}: SQL error {sql.Number}"
                    : stateLost ? $"{ex.GetType().Name}: {ex.InnerException?.GetType().Name}"
                    : ex.GetType().Name;
                _logger.LogError(ex, $"User scope purge {job.Id}: failed at step {job.StepIndex} ({job.Phase}) - {detail}. " +
                    "What was removed before this point stays removed; start the purge again to finish it.");
                try
                {
                    return await EndAsync(job, UserScopePurgeStates.Failed, code, detail).ConfigureAwait(false);
                }
                catch (Exception finishError)
                {
                    _logger.LogError(finishError, $"User scope purge {job.Id}: could not record the failure ({finishError.GetType().Name}).");
                    return UserScopePurgeRunOutcome.Failed;
                }
            }
            catch (OperationCanceledException)
            {
                await PauseQuietlyAsync(job).ConfigureAwait(false);
                return UserScopePurgeRunOutcome.Paused;
            }
        }

        /// <summary>
        /// Saves the purge's progress and says whether a stop has been asked for: at most every
        /// <see cref="StateCheckInterval"/> unless <paramref name="force"/>, because the store may be a network call away.
        /// </summary>
        private async Task<bool> SaveAndCheckForStopAsync(UserScopePurgeJob job, bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _lastStateCheckUtc < StateCheckInterval)
            {
                return false;
            }

            _lastStateCheckUtc = now;
            job.UpdatedUtc = now;
            await _state.SaveAsync(job).ConfigureAwait(false);
            return await _state.IsCancelRequestedAsync(job.Id).ConfigureAwait(false);
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
                    return await EndAsync(job, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.FilterChangedWhileRunning, null).ConfigureAwait(false);
                }

                _logger.LogWarning($"User scope purge {job.Id}: UserGroupsFilter changed after the purge was confirmed, so nobody was removed.");
                return await EndAsync(job, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.FilterChanged, null).ConfigureAwait(false);
            }

            List<int> candidates;
            var scopeDescription = string.Empty;
            if (CandidatesOverride != null)
            {
                candidates = CandidatesOverride.ToList();
            }
            else
            {
                var resolution = preResolved != null && preResolved.Status == UserImportScopeStatus.Resolved
                    ? preResolved
                    : scopeProvider == null ? null : await scopeProvider.RefreshAsync().ConfigureAwait(false);

                if (resolution == null || resolution.Status != UserImportScopeStatus.Resolved)
                {
                    _logger.LogWarning($"User scope purge {job.Id}: the UserGroupsFilter groups could not be read completely, so nobody " +
                        (restarted ? "more " : "") + "was removed.");
                    return await EndAsync(job, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.ScopeUnavailable, resolution?.FailureKind?.ToString()).ConfigureAwait(false);
                }

                if (resolution.Members == null || resolution.Members.Count == 0)
                {
                    _logger.LogWarning($"User scope purge {job.Id}: UserGroupsFilter resolved to nobody, so the purge was refused rather than remove every user.");
                    return await EndAsync(job, UserScopePurgeStates.Failed, UserScopePurgeErrorCodes.ScopeEmpty, null).ConfigureAwait(false);
                }

                var scope = UserImportScope.ForMembers(resolution.Members, "purge");
                var unknownUserId = await GetOrCreateUnknownUserAsync().ConfigureAwait(false);
                candidates = await FindUsersOutsideScopeAsync(scope, unknownUserId).ConfigureAwait(false);

                // Copilot Studio credit rows that were never linked to a user row carry only an Entra object id. They are
                // settled here, while the scope is in hand; the later steps only see linked rows.
                var unlinked = await DeleteUnlinkedCreditsOutsideScopeAsync(scope).ConfigureAwait(false);
                if (unlinked > 0)
                {
                    Add(job, "copilot_studio_credit_user_daily", unlinked);
                }
                scopeDescription = $" of {resolution.Members.Count:N0} member(s)";
            }

            job.StepIndex = 0;
            job.StepAfter = null;
            job.Phase = UserScopePurgePhases.Snapshot;
            await WithRetriesAsync(session, async () =>
            {
                await session.LoadCandidatesAsync(candidates, stopToken).ConfigureAwait(false);
                return true;
            }, stopToken).ConfigureAwait(false);

            job.CandidateCount = job.UsersDeleted + candidates.Count;
            job.State = UserScopePurgeStates.Running;
            job.StepIndex = 1;
            job.Phase = UserScopePurgePlan.PhaseOf(1);
            job.StartedUtc = job.StartedUtc ?? DateTime.UtcNow;
            _logger.LogInformation($"User scope purge {job.Id}: " + (restarted ? "starting again after a restart. " : string.Empty) +
                $"{candidates.Count:N0} user(s) are outside the scope{scopeDescription}; removing everything stored about them.");

            if (await SaveAndCheckForStopAsync(job, force: true).ConfigureAwait(false))
            {
                return await EndAsync(job, UserScopePurgeStates.Cancelled, null, null).ConfigureAwait(false);
            }
            return null;
        }

        /// <summary>Whether <c>UserGroupsFilter</c> is no longer the one the purge was confirmed against.</summary>
        private static bool FilterChangedSince(UserScopePurgeJob job, IUserImportScopeProvider scopeProvider)
        {
            var current = scopeProvider?.Filter?.Fingerprint ?? string.Empty;
            return !string.IsNullOrEmpty(job.FilterFingerprint) && !string.Equals(job.FilterFingerprint, current, StringComparison.Ordinal);
        }

        /// <summary>Users that the scope does not contain by object id, UPN or mail - never the anonymous Unknown User.</summary>
        internal async Task<List<int>> FindUsersOutsideScopeAsync(UserImportScope scope, int unknownUserId)
        {
            var outside = new List<int>();
            using (var connection = await _database.OpenAsync().ConfigureAwait(false))
            using (var command = new SqlCommand("SELECT id, user_name, mail, azure_ad_id FROM dbo.users;", connection))
            {
                command.CommandTimeout = 600;
                using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        var id = reader.GetInt32(0);
                        var upn = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var mail = reader.IsDBNull(2) ? null : reader.GetString(2);
                        var objectId = reader.IsDBNull(3) ? null : reader.GetString(3);
                        if (id != unknownUserId
                            && !string.Equals(upn, UserScopePurgePlan.UnknownUserName, StringComparison.Ordinal)
                            && !scope.IsAnyInScope(objectId, upn, mail))
                        {
                            outside.Add(id);
                        }
                    }
                }
            }
            return outside;
        }

        private async Task<int> DeleteUnlinkedCreditsOutsideScopeAsync(UserImportScope scope)
        {
            var outside = new List<string>();
            using (var connection = await _database.OpenAsync().ConfigureAwait(false))
            {
                if (!await TableExistsAsync(connection, "copilot_studio_credit_user_daily").ConfigureAwait(false))
                {
                    return 0;
                }

                using (var command = new SqlCommand(
                    "SELECT DISTINCT entra_object_id FROM dbo.copilot_studio_credit_user_daily WHERE user_id IS NULL AND entra_object_id IS NOT NULL;", connection))
                {
                    command.CommandTimeout = 600;
                    using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
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
                        deleted += await delete.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
                return deleted;
            }
        }

        /// <summary>
        /// Runs one step to its end. Null when it finished; otherwise how the purge ended (stopped on request).
        /// </summary>
        private async Task<UserScopePurgeRunOutcome?> RunStepAsync(UserScopePurgeJob job, UserScopePurgeSession session, UserScopePurgeStep step,
            int? unknownUserId, CancellationToken stopToken)
        {
            string keyType;
            using (var connection = await _database.OpenAsync().ConfigureAwait(false))
            {
                keyType = await KeyTypeAsync(connection, step.Table, step.KeyColumn).ConfigureAwait(false);
            }

            if (keyType == null)
            {
                _logger.LogInformation($"User scope purge {job.Id}: dbo.{step.Table} is not in this database, skipping that step.");
                return null;
            }

            var window = WindowOverride ?? step.Window;
            var windowNumber = 0;
            long stepRows = 0;
            while (true)
            {
                stopToken.ThrowIfCancellationRequested();
                windowNumber++;

                var result = await WithRetriesAsync(session,
                    () => RunWindowAsync(session.Connection, step, keyType, job.StepAfter, window, unknownUserId), stopToken).ConfigureAwait(false);
                if (result.Done)
                {
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

                if (await SaveAndCheckForStopAsync(job, force: false).ConfigureAwait(false))
                {
                    _logger.LogInformation($"User scope purge {job.Id}: stopped on request during {step.Table}.");
                    return await EndAsync(job, UserScopePurgeStates.Cancelled, null, null).ConfigureAwait(false);
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
                _logger.LogInformation($"User scope purge {job.Id}: dbo.{step.CountKey} - {stepRows:N0} {outcome}.");
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

        private async Task<WindowResult> RunWindowAsync(SqlConnection connection, UserScopePurgeStep step, string keyType, string after, int window, int? unknownUserId)
        {
            var sql = step.Kind == UserScopePurgeStepKind.Calls
                ? BuildCallsWindowSql(keyType, after != null)
                : BuildWindowSql(step, keyType, after != null);

            try
            {
                return await ExecuteWindowAsync(connection, sql, keyType, after, window, unknownUserId).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == 547 && step.Kind == UserScopePurgeStepKind.Users)
            {
                // Something new points at one of these users - the importers added data about them while the purge
                // ran. Remove the ones that are free, one at a time, and leave the rest for the next purge.
                return await DeleteUsersOneByOneAsync(connection, keyType, after, window).ConfigureAwait(false);
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

        private async Task<WindowResult> DeleteUsersOneByOneAsync(SqlConnection connection, string keyType, string after, int window)
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
                try
                {
                    using (var delete = new SqlCommand("DELETE FROM dbo.users WHERE id = @id;", connection))
                    {
                        delete.Parameters.Add("@id", SqlDbType.Int).Value = id;
                        deleted += await delete.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }
                }
                catch (SqlException ex) when (ex.Number == 547)
                {
                    // Kept: counted as skipped when the purge finishes.
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
        internal static string BuildWindowSql(UserScopePurgeStep step, string keyType, bool hasAfter)
        {
            var key = $"t.[{step.KeyColumn}]";
            var afterWhere = hasAfter ? $"WHERE {key} > @after " : "";
            var afterAnd = hasAfter ? $" AND {key} > @after" : "";
            var resumeAt = hasAfter ? "@after" : $"CAST(NULL AS {keyType})";
            var target = $"dbo.[{step.Table}] t WITH (INDEX(1))";

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
                             "AND NOT EXISTS (SELECT 1 FROM dbo.audit_events e WHERE e.user_id = t.[id]);";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(step));
            }

            return $@"
SET NOCOUNT ON;
DECLARE @end {keyType} = (SELECT TOP (1) w.k FROM (SELECT TOP (@window) {key} AS k FROM dbo.[{step.Table}] t {afterWhere}ORDER BY {key}) w ORDER BY w.k DESC);
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
        internal async Task<int> GetOrCreateUnknownUserAsync()
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
            using (var connection = await _database.OpenAsync().ConfigureAwait(false))
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.Add("@name", SqlDbType.VarChar, 250).Value = UserScopePurgePlan.UnknownUserName;
                return (int)await command.ExecuteScalarAsync().ConfigureAwait(false);
            }
        }

        private async Task<UserScopePurgeRunOutcome> EndAsync(UserScopePurgeJob job, string state, string errorCode, string errorDetail)
        {
            var now = DateTime.UtcNow;
            job.State = state;
            job.ErrorCode = errorCode;
            job.ErrorDetail = errorDetail;
            job.CompletedUtc = now;
            job.UpdatedUtc = now;
            if (state == UserScopePurgeStates.Completed)
            {
                job.Phase = UserScopePurgePhases.Done;
            }

            await _state.SaveAsync(job).ConfigureAwait(false);

            switch (state)
            {
                case UserScopePurgeStates.Completed: return UserScopePurgeRunOutcome.Completed;
                case UserScopePurgeStates.Cancelled: return UserScopePurgeRunOutcome.Cancelled;
                default: return UserScopePurgeRunOutcome.Failed;
            }
        }

        private async Task PauseQuietlyAsync(UserScopePurgeJob job)
        {
            try
            {
                job.UpdatedUtc = DateTime.UtcNow;
                await _state.SaveAsync(job).ConfigureAwait(false);
                _logger.LogInformation($"User scope purge {job.Id}: stopped because the web app is shutting down. " +
                    (_state.IsDurable
                        ? "It starts again when the web app next starts."
                        : "Its progress is kept in the web app's memory, so start it again from the portal to finish it."));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"User scope purge {job.Id}: could not save its progress while stopping ({ex.GetType().Name}).");
            }
        }

        /// <summary>
        /// Runs <paramref name="action"/> on the purge's session, retrying transient database errors. Before each attempt
        /// the session is re-opened if its connection broke - taking the purge lock again and reloading the candidates.
        /// </summary>
        private async Task<T> WithRetriesAsync<T>(UserScopePurgeSession session, Func<Task<T>> action, CancellationToken stopToken)
        {
            var delay = RetryDelayOverride ?? TimeSpan.FromSeconds(2);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await session.EnsureOpenAsync(stopToken).ConfigureAwait(false);
                    return await action().ConfigureAwait(false);
                }
                catch (SqlException ex) when (attempt < 5 && IsRetryable(ex.Number, session.Connection.State))
                {
                    // A deadlock or timeout leaves the connection usable, so keep it - and with it the purge lock. After a
                    // lost connection, drop it: the next attempt opens the session again first.
                    var lost = IsConnectionLost(ex.Number, session.Connection.State);
                    if (lost)
                    {
                        session.DiscardConnection();
                    }
                    _logger.LogWarning($"User scope purge: SQL error {ex.Number}{(lost ? " (connection lost)" : string.Empty)} (attempt {attempt} of 5); retrying.");
                    await Task.Delay(delay, stopToken).ConfigureAwait(false);
                    delay = TimeSpan.FromTicks(delay.Ticks * 2);
                }
            }
        }

        private static void Add(UserScopePurgeJob job, string table, long rows)
        {
            job.RowsAffected.TryGetValue(table, out var existing);
            job.RowsAffected[table] = existing + rows;
        }

        private static async Task<bool> TableExistsAsync(SqlConnection connection, string table)
        {
            using (var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", connection))
            {
                command.Parameters.Add("@name", SqlDbType.NVarChar, 300).Value = "dbo." + table;
                return (int)await command.ExecuteScalarAsync().ConfigureAwait(false) == 1;
            }
        }

        /// <summary>The key column's SQL type (int, bigint or uniqueidentifier), or null when the table is not there.</summary>
        internal static async Task<string> KeyTypeAsync(SqlConnection connection, string table, string column)
        {
            const string sql = @"
SELECT TYPE_NAME(c.system_type_id) FROM sys.columns c
WHERE c.object_id = OBJECT_ID(@table, N'U') AND c.name = @column;";
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.Add("@table", SqlDbType.NVarChar, 300).Value = "dbo." + table;
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
