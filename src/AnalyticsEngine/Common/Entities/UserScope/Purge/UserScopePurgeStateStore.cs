using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserScope.Purge
{
    /// <summary>
    /// Purge records - one per purge, which purge is the latest, and stop requests - kept in the solution's runtime state
    /// store (<see cref="StatePartitions.UserScopePurge"/>): Azure Table storage when it is configured, otherwise the web
    /// app's memory. Never the analytics database. A run claims and updates the active job using its ETag: SQL session
    /// loss can't let a late progress or terminal write overwrite the next owner. A stop request is a separate key.
    /// </summary>
    public sealed class UserScopePurgeStateStore
    {
        private const string LatestKey = "LatestJob";

        /// <summary>How long a finished purge's record is kept, for the page to show.</summary>
        public static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(90);

        private static readonly DateTime IdEpoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly IKeyValueStore _values;
        private readonly Func<DateTime> _utcNow;

        /// <param name="isDurable">
        /// True when <paramref name="values"/> survives a web app restart and every instance shares it - Azure Table storage,
        /// not memory. Then a purge the web app was running when it stopped is started again when it starts.
        /// </param>
        public UserScopePurgeStateStore(IKeyValueStore values, bool isDurable, Func<DateTime> utcNow = null)
        {
            _values = values ?? throw new ArgumentNullException(nameof(values));
            IsDurable = isDurable;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>True when purge records survive a restart and every web app instance sees them.</summary>
        public bool IsDurable { get; }

        public string Description => _values.Description;

        /// <summary>
        /// Records a new, queued purge and makes it the latest. Only call it while holding the purge lock, so that purges
        /// are created one at a time. Ids count up from a value seeded by the clock, so a restart that loses in-memory
        /// records never reuses an id that a browser may still be asking about.
        /// </summary>
        /// <param name="confirmedOutsideScopeCount">How many people outside the scope the administrator agreed to remove.</param>
        /// <param name="scopeMembershipFingerprint">The <see cref="UserScopeMembers.MembershipFingerprint"/> of the scope it was confirmed against.</param>
        public async Task<UserScopePurgeJob> CreateAsync(string requestedBy, string filterFingerprint, int scopeMemberCount,
            int confirmedOutsideScopeCount, string scopeMembershipFingerprint = null)
        {
            var now = _utcNow();
            var seed = (int)Math.Min(int.MaxValue - 1, Math.Max(1, (now - IdEpoch).TotalSeconds));
            var id = Math.Max(await GetLatestIdAsync().ConfigureAwait(false) + 1, seed);

            var job = new UserScopePurgeJob
            {
                Id = id,
                State = UserScopePurgeStates.Queued,
                Phase = UserScopePurgePhases.Snapshot,
                StepIndex = 0,
                StepCount = UserScopePurgePlan.StepCount,
                RequestedBy = requestedBy == null || requestedBy.Length <= 256 ? requestedBy : requestedBy.Substring(0, 256),
                FilterFingerprint = filterFingerprint,
                ScopeMemberCount = scopeMemberCount,
                ConfirmedOutsideScopeCount = Math.Max(0, confirmedOutsideScopeCount),
                ScopeMembershipFingerprint = scopeMembershipFingerprint,
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            await SaveAsync(job).ConfigureAwait(false);
            await WriteAsync(LatestKey, id.ToString(CultureInfo.InvariantCulture), null).ConfigureAwait(false);
            return job;
        }

        /// <summary>A purge's record, or null when there is none (or it has expired).</summary>
        public async Task<UserScopePurgeJob> GetAsync(int id)
        {
            var job = ReadJob(await ReadAsync(JobKey(id), CancellationToken.None).ConfigureAwait(false));
            if (job != null) job.CancelRequested = await IsCancelRequestedAsync(id).ConfigureAwait(false);
            return job;
        }

        private static UserScopePurgeJob ReadJob(string json)
        {
            var job = json == null ? null : JsonConvert.DeserializeObject<UserScopePurgeJob>(json);
            if (job == null) return null;
            job.RowsAffected = job.RowsAffected == null
                ? new Dictionary<string, long>(StringComparer.Ordinal)
                : new Dictionary<string, long>(job.RowsAffected, StringComparer.Ordinal);
            return job;
        }

        /// <summary>The most recently started purge, or null.</summary>
        public async Task<UserScopePurgeJob> GetLatestAsync()
        {
            var id = await GetLatestIdAsync().ConfigureAwait(false);
            return id > 0 ? await GetAsync(id).ConfigureAwait(false) : null;
        }

        /// <summary>Saves a purge's record. A finished purge's record is kept for <see cref="FinishedRetention"/>.</summary>
        public Task SaveAsync(UserScopePurgeJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            return WriteAsync(JobKey(job.Id), JsonConvert.SerializeObject(job), job.IsActive ? (TimeSpan?)null : FinishedRetention);
        }

        internal Task<UserScopePurgeJob> TryClaimAsync(int id, Func<Task> checkSqlLock, CancellationToken token) =>
            TrySetOwnerAsync(id, null, checkSqlLock, token);

        internal async Task<bool> RenewOwnerAsync(int id, string owner, Func<Task> checkFences, CancellationToken token)
        {
            // Process-local records cannot exclude a run on another instance. A read alone is insufficient even
            // in Table: a former SQL owner may still have a claim in flight based on the ETag we are reading.
            if (!IsDurable || string.IsNullOrEmpty(owner)) throw new UserScopePurgeLockLostException();
            var job = await TrySetOwnerAsync(id, owner, checkFences, token).ConfigureAwait(false);
            if (job == null) throw new UserScopePurgeLockLostException();
            return !job.CancelRequested;
        }

        private async Task<UserScopePurgeJob> TrySetOwnerAsync(int id, string requiredOwner, Func<Task> checkSqlLock, CancellationToken token)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var row = await ReadVersionAsync(id, token).ConfigureAwait(false);
                var job = ReadJob(row.Value);
                if (job == null || job.Id != id || !job.IsActive || (requiredOwner != null && job.RunToken != requiredOwner)
                    || await GetLatestIdAsync(token).ConfigureAwait(false) != id) return null;
                // Read the ETag BEFORE proving SQL ownership. An older in-flight progress write might win this
                // ETag, so reread and reprove on conflict; never claim on a version read after the lock was lost.
                await checkSqlLock().ConfigureAwait(false);
                job.RunToken = requiredOwner ?? Guid.NewGuid().ToString("N");
                if (await TrySaveVersionAsync(job, row.VersionToken, token).ConfigureAwait(false))
                {
                    job.CancelRequested = await IsCancelRequestedAsync(id, token).ConfigureAwait(false);
                    return job;
                }
            }
            return null;
        }

        internal async Task SaveOwnedAsync(UserScopePurgeJob job, CancellationToken token)
        {
            if (string.IsNullOrEmpty(job.RunToken)) throw new UserScopePurgeLockLostException();
            var row = await ReadVersionAsync(job.Id, token).ConfigureAwait(false);
            var current = row.Value == null ? null : JsonConvert.DeserializeObject<UserScopePurgeJob>(row.Value);
            if (current == null || !current.IsActive || current.Id != job.Id || current.RunToken != job.RunToken
                || !await TrySaveVersionAsync(job, row.VersionToken, token).ConfigureAwait(false))
                throw new UserScopePurgeLockLostException();
        }

        private async Task<VersionedValue> ReadVersionAsync(int id, CancellationToken token)
        {
            try { return await ((IExpiringConditionalKeyValueStore)_values).GetVersionedAsync(JobKey(id), token).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                throw new UserScopePurgeStateUnavailableException("Could not read the purge's ownership record.", ex);
            }
        }

        private async Task<bool> TrySaveVersionAsync(UserScopePurgeJob job, string expected, CancellationToken token)
        {
            var json = JsonConvert.SerializeObject(job);
            try
            {
                return await ((IExpiringConditionalKeyValueStore)_values).TrySetExpiringStringAsync(JobKey(job.Id), json,
                    expected, job.IsActive ? (TimeSpan?)null : FinishedRetention, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                throw new UserScopePurgeStateUnavailableException("Could not update the purge's ownership record.", ex);
            }
        }

        /// <summary>Asks an active purge to stop after the batch it is on. False when it is not active, or does not exist.</summary>
        public async Task<bool> RequestCancelAsync(int id)
        {
            var job = await GetAsync(id).ConfigureAwait(false);
            if (job == null || !job.IsActive)
            {
                return false;
            }
            await WriteAsync(CancelKey(id), "1", FinishedRetention).ConfigureAwait(false);
            return true;
        }

        /// <summary>Whether a stop has been asked for. Read on its own, so a running purge can check cheaply.</summary>
        public Task<bool> IsCancelRequestedAsync(int id) => IsCancelRequestedAsync(id, CancellationToken.None);

        internal async Task<bool> IsCancelRequestedAsync(int id, CancellationToken token)
            => await ReadAsync(CancelKey(id), token).ConfigureAwait(false) != null;

        private async Task<int> GetLatestIdAsync(CancellationToken token = default)
        {
            var value = await ReadAsync(LatestKey, token).ConfigureAwait(false);
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }

        // The partition is the purge's own (StatePartitions.UserScopePurge), so the keys need no prefix.
        private static string JobKey(int id) => "Job:" + id.ToString(CultureInfo.InvariantCulture);

        private static string CancelKey(int id) => JobKey(id) + ":Cancel";

        private async Task<string> ReadAsync(string key, CancellationToken token)
        {
            try
            {
                return await _values.GetStringAsync(key, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new UserScopePurgeStateUnavailableException($"Couldn't read '{key}' from {_values.Description}: {ex.Message}", ex);
            }
        }

        private async Task WriteAsync(string key, string value, TimeSpan? timeToLive)
        {
            try
            {
                await _values.SetStringAsync(key, value, timeToLive).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new UserScopePurgeStateUnavailableException($"Couldn't save '{key}' to {_values.Description}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// The store that keeps purge records - Azure Table storage - could not be reached, or refused the request. Only the
    /// store's own failures: a record that can't be read back as a purge is not one of them.
    /// </summary>
    public sealed class UserScopePurgeStateUnavailableException : Exception
    {
        public UserScopePurgeStateUnavailableException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
