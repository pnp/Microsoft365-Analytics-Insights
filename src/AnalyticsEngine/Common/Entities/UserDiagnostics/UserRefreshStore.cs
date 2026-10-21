using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.UserDiagnostics
{
    /// <summary>
    /// One administrator's request to re-read one person's directory profile and licences, and what came of it. Stored
    /// as JSON in the <see cref="StatePartitions.UserRefresh"/> partition; returned to the portal as is.
    /// </summary>
    public class UserRefreshRequest
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("objectId")]
        public string ObjectId { get; set; }

        /// <summary>The signed-in administrator. Personal data: never logged or sent in telemetry.</summary>
        [JsonProperty("requestedBy")]
        public string RequestedBy { get; set; }

        [JsonProperty("requestedUtc")]
        public DateTime RequestedUtc { get; set; }

        /// <summary>One of <see cref="UserRefreshStates"/>.</summary>
        [JsonProperty("state")]
        public string State { get; set; }

        [JsonProperty("attempts")]
        public int Attempts { get; set; }

        /// <summary>Which worker attempt owns a running request; a completion carrying any other claim is refused.</summary>
        [JsonProperty("claimId")]
        public string ClaimId { get; set; }

        [JsonProperty("leaseUntilUtc")]
        public DateTime? LeaseUntilUtc { get; set; }

        [JsonProperty("startedUtc")]
        public DateTime? StartedUtc { get; set; }

        [JsonProperty("completedUtc")]
        public DateTime? CompletedUtc { get; set; }

        /// <summary>When Microsoft Graph was read for the result - the time the stored data was verified against.</summary>
        [JsonProperty("observedUtc")]
        public DateTime? ObservedUtc { get; set; }

        /// <summary>One of <see cref="UserRefreshOutcomes"/>, once finished.</summary>
        [JsonProperty("outcome")]
        public string Outcome { get; set; }

        [JsonProperty("added")]
        public List<string> Added { get; set; } = new List<string>();

        [JsonProperty("removed")]
        public List<string> Removed { get; set; } = new List<string>();

        /// <summary>Licences SQL holds that the refresh would have removed but did not, because the evidence was not enough.</summary>
        [JsonProperty("heldBack")]
        public List<string> HeldBack { get; set; } = new List<string>();

        /// <summary>Profile fields the refresh changed (e.g. <c>accountEnabled</c>, <c>mail</c>).</summary>
        [JsonProperty("profileChanges")]
        public List<string> ProfileChanges { get; set; } = new List<string>();

        /// <summary>Stable keys for caveats about the result (see <see cref="UserRefreshNotes"/>).</summary>
        [JsonProperty("notes")]
        public List<string> Notes { get; set; } = new List<string>();

        [JsonIgnore]
        public bool IsFinished => State == UserRefreshStates.Completed || State == UserRefreshStates.Failed || State == UserRefreshStates.Expired;

        /// <summary>When a request still waiting for an importer stops waiting (<see cref="UserRefreshStore.QueueExpiry"/>).</summary>
        [JsonProperty("expiresUtc")]
        public DateTime ExpiresUtc => RequestedUtc + UserRefreshStore.QueueExpiry;
    }

    public static class UserRefreshStates
    {
        /// <summary>Accepted and waiting for the importer.</summary>
        public const string Queued = "queued";

        /// <summary>An importer worker owns it.</summary>
        public const string Running = "running";

        public const string Completed = "completed";
        public const string Failed = "failed";

        /// <summary>No importer picked it up in time.</summary>
        public const string Expired = "expired";
    }

    /// <summary>Stable outcome keys; the portal writes the sentences.</summary>
    public static class UserRefreshOutcomes
    {
        /// <summary>Licences or profile changed to match the directory.</summary>
        public const string Updated = "updated";

        /// <summary>The directory and SQL already agreed: verified, nothing changed.</summary>
        public const string Unchanged = "unchanged";

        /// <summary>Some changes were applied, but some removals were held back for lack of evidence.</summary>
        public const string Partial = "partial";

        public const string ScopeExcluded = "scopeExcluded";
        public const string ScopeUnknown = "scopeUnknown";

        /// <summary>The person is not in the database; only a full user import adds people.</summary>
        public const string NotStored = "notStored";

        public const string UserNotFoundInDirectory = "userNotFoundInDirectory";
        public const string IdentityMismatch = "identityMismatch";
        public const string PermissionDenied = "permissionDenied";
        public const string GraphUnavailable = "graphUnavailable";
        public const string ImportDisabled = "importDisabled";

        /// <summary>The worker stopped part-way more than once; nothing more will be attempted.</summary>
        public const string Interrupted = "interrupted";

        /// <summary>Unexpected failure; details are in the importer's log.</summary>
        public const string Error = "error";

        /// <summary>Outcomes where something went wrong, as opposed to a decision (e.g. out of scope) or a result.</summary>
        public static bool IsFailure(string outcome)
            => outcome == GraphUnavailable || outcome == PermissionDenied || outcome == Error || outcome == Interrupted;
    }

    /// <summary>Stable keys for the profile fields a refresh can change; the portal translates them.</summary>
    public static class UserRefreshProfileFields
    {
        public const string AccountEnabled = "accountEnabled";
        public const string Mail = "mail";
        public const string PostalCode = "postalCode";
        public const string ObjectId = "objectId";
        public const string Department = "department";
        public const string JobTitle = "jobTitle";
        public const string OfficeLocation = "officeLocation";
        public const string UsageLocation = "usageLocation";
        public const string Country = "country";
        public const string StateOrProvince = "stateOrProvince";
        public const string CompanyName = "companyName";
        public const string Manager = "manager";
    }

    public static class UserRefreshNotes
    {
        /// <summary>The directory answer was incomplete, so no licence was removed.</summary>
        public const string IncompleteDirectoryAnswer = "incompleteDirectoryAnswer";

        /// <summary>A licence shares its name with another SKU, so it was not removed on one SKU's absence.</summary>
        public const string AmbiguousLicenceKept = "ambiguousLicenceKept";

        /// <summary>The directory's UPN differs from the stored one; the stored UPN is left for the full import to rename.</summary>
        public const string UserPrincipalNameChanged = "userPrincipalNameChanged";

        /// <summary>The database has no licence history tables, so the change was applied without history.</summary>
        public const string NoLicenceHistory = "noLicenceHistory";

        /// <summary>A previous attempt was interrupted after it may have changed SQL; this one re-read and re-applied.</summary>
        public const string ReconciledAfterInterruption = "reconciledAfterInterruption";

        /// <summary>Licences were checked, but the profile could not be updated (see the importer's log).</summary>
        public const string ProfileNotUpdated = "profileNotUpdated";

        /// <summary>
        /// Data changed, but reports could not be told; they show it once their caches expire (minutes, not hours).
        /// </summary>
        public const string ReportCachesNotRefreshed = "reportCachesNotRefreshed";
    }

    /// <summary>The last licence check that covered one person - a fact the tenant-wide import cannot give.</summary>
    public class UserVerificationRecord
    {
        [JsonProperty("verifiedUtc")]
        public DateTime VerifiedUtc { get; set; }

        [JsonProperty("outcome")]
        public string Outcome { get; set; }

        [JsonProperty("requestId")]
        public string RequestId { get; set; }
    }

    /// <summary>Why a refresh cannot be requested, or was not accepted.</summary>
    public static class UserRefreshRejections
    {
        public const string QueueFull = "queueFull";
        public const string StorageNotConfigured = "storageNotConfigured";
        public const string StorageUnavailable = "storageUnavailable";
        public const string ImportDisabled = "importDisabled";

        /// <summary>The person is not in the database; only the scheduled import adds people.</summary>
        public const string NotStored = "notStored";

        public const string ScopeExcluded = "scopeExcluded";
        public const string ScopeUnknown = "scopeUnknown";
    }

    public sealed class UserRefreshStoreUnavailableException : Exception
    {
        public UserRefreshStoreUnavailableException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The durable queue and records behind per-person refreshes, in Azure Table storage. Every change that two writers
    /// could race on - the queue, a request's claim - is a compare-and-set on the row's ETag, so the web app and any
    /// number of importer instances can share it without a lock: at most one claim of a request ever succeeds, and a
    /// worker whose lease has passed cannot record a result over its successor's.
    /// </summary>
    public sealed class UserRefreshStore
    {
        public const int MaxQueueLength = 25;

        /// <summary>Attempts before a request that keeps being interrupted is failed rather than retried forever.</summary>
        public const int MaxAttempts = 3;

        /// <summary>A queued request nobody picked up in this time is expired, not left looking alive.</summary>
        public static readonly TimeSpan QueueExpiry = TimeSpan.FromHours(24);

        public static readonly TimeSpan RecordLifetime = TimeSpan.FromDays(30);

        public const int RecentPerUser = 10;

        internal const string QueueKey = "Queue";
        internal const string GenerationKey = "LicenceDataGeneration";
        internal const string ScheduledImportKey = "ScheduledUserImportActive";

        private readonly IConditionalKeyValueStore _values;
        private readonly Func<DateTime> _utcNow;

        public UserRefreshStore(IConditionalKeyValueStore values, Func<DateTime> utcNow = null)
        {
            _values = values ?? throw new ArgumentNullException(nameof(values));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>The durable store for this deployment, or null without Azure Storage. Requests are never kept in memory.</summary>
        public static UserRefreshStore TryOpen(Common.Entities.Config.AppConfig config, Microsoft.Extensions.Logging.ILogger logger = null)
        {
            return StateStore.TryOpen(config, StatePartitions.UserRefresh, logger) is IConditionalKeyValueStore store
                ? new UserRefreshStore(store)
                : null;
        }

        /// <summary>
        /// Queues a refresh for one person, or returns the request already waiting or running for them - so repeated
        /// clicks, or two administrators, do not queue the same work twice. Returns null (with <paramref name="rejection"/>)
        /// when the queue is full.
        /// </summary>
        public async Task<(UserRefreshRequest Request, string Rejection)> SubmitAsync(int userId, string upn, string objectId, string requestedBy)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var queue = await ReadVersionedAsync(QueueKey);
                var ids = Deserialize<List<string>>(queue.Value) ?? new List<string>();

                foreach (var id in ids)
                {
                    var existing = await GetAsync(id);
                    if (existing != null && !existing.IsFinished && existing.UserId == userId)
                    {
                        return (existing, null);
                    }
                }

                if (ids.Count >= MaxQueueLength)
                {
                    return (null, UserRefreshRejections.QueueFull);
                }

                var now = _utcNow();
                var request = new UserRefreshRequest
                {
                    Id = Guid.NewGuid().ToString("N"),
                    UserId = userId,
                    UserPrincipalName = upn,
                    ObjectId = objectId,
                    RequestedBy = requestedBy == null || requestedBy.Length <= 256 ? requestedBy : requestedBy.Substring(0, 256),
                    RequestedUtc = now,
                    State = UserRefreshStates.Queued,
                };

                // The request row first: a queue entry must never point at nothing.
                await WriteAsync(RequestKey(request.Id), Serialize(request), RecordLifetime);
                ids.Add(request.Id);
                if (await TrySetAsync(QueueKey, Serialize(ids), queue.VersionToken))
                {
                    try
                    {
                        await RememberForUserAsync(userId, request.Id);
                    }
                    catch (UserRefreshStoreUnavailableException)
                    {
                        // The request is queued and will run; only the per-person history list missed it. Answering
                        // "not accepted" now would be wrong, and a repeat click still finds it through the queue.
                    }
                    return (request, null);
                }

                // Someone else changed the queue between our read and write: drop the orphan row and try again.
                await DeleteAsync(RequestKey(request.Id));
            }

            throw new UserRefreshStoreUnavailableException("The refresh queue kept changing while a request was being added.", null);
        }

        public async Task<UserRefreshRequest> GetAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return null;
            return Deserialize<UserRefreshRequest>(await ReadAsync(RequestKey(id)));
        }

        /// <summary>Whether anything is queued - one read, so an importer can skip taking the writer lock when idle.</summary>
        public async Task<bool> HasQueuedAsync()
        {
            var ids = Deserialize<List<string>>(await ReadAsync(QueueKey));
            return ids != null && ids.Count > 0;
        }

        /// <summary>This person's most recent requests, newest first. Read in parallel: one diagnostics page load.</summary>
        public async Task<List<UserRefreshRequest>> RecentForUserAsync(int userId)
        {
            var ids = Deserialize<List<string>>(await ReadAsync(UserKey(userId))) ?? new List<string>();
            var requests = await Task.WhenAll(ids.AsEnumerable().Reverse().Select(GetAsync));
            return requests.Where(r => r != null).ToList();
        }

        /// <summary>
        /// Claims the oldest request that can run: queued, or running with a lease that has passed (its worker died).
        /// Expires requests nobody picked up in time and retires finished ones from the queue on the way. Returns null
        /// when there is nothing to do.
        /// </summary>
        public async Task<UserRefreshRequest> TryClaimNextAsync(TimeSpan lease)
        {
            var queue = await ReadVersionedAsync(QueueKey);
            var ids = Deserialize<List<string>>(queue.Value) ?? new List<string>();
            var finished = new List<string>();
            UserRefreshRequest claimed = null;
            var now = _utcNow();

            foreach (var id in ids)
            {
                var row = await ReadVersionedAsync(RequestKey(id));
                var request = Deserialize<UserRefreshRequest>(row.Value);
                if (request == null || request.IsFinished)
                {
                    finished.Add(id);
                    continue;
                }

                if (request.State == UserRefreshStates.Queued && now - request.RequestedUtc > QueueExpiry)
                {
                    request.State = UserRefreshStates.Expired;
                    request.CompletedUtc = now;
                    if (await TrySetAsync(RequestKey(id), Serialize(request), row.VersionToken))
                    {
                        finished.Add(id);
                        await SealAsync(request);
                    }
                    continue;
                }

                var runnable = request.State == UserRefreshStates.Queued
                    || (request.State == UserRefreshStates.Running && request.LeaseUntilUtc.HasValue && request.LeaseUntilUtc.Value <= now);
                if (!runnable || claimed != null) continue;

                if (request.Attempts >= MaxAttempts)
                {
                    request.State = UserRefreshStates.Failed;
                    request.Outcome = UserRefreshOutcomes.Interrupted;
                    request.CompletedUtc = now;
                    if (await TrySetAsync(RequestKey(id), Serialize(request), row.VersionToken))
                    {
                        finished.Add(id);
                        await SealAsync(request);
                    }
                    continue;
                }

                if (request.State == UserRefreshStates.Running)
                {
                    // A previous worker may have changed SQL before it died. The next attempt re-reads Graph and SQL and
                    // re-applies only the difference, so the outcome converges; this note says why there are two attempts.
                    if (!request.Notes.Contains(UserRefreshNotes.ReconciledAfterInterruption)) request.Notes.Add(UserRefreshNotes.ReconciledAfterInterruption);
                }

                request.State = UserRefreshStates.Running;
                request.Attempts++;
                request.ClaimId = Guid.NewGuid().ToString("N");
                request.LeaseUntilUtc = now + lease;
                request.StartedUtc = request.StartedUtc ?? now;
                if (await TrySetAsync(RequestKey(id), Serialize(request), row.VersionToken))
                {
                    claimed = request;
                }
            }

            if (finished.Count > 0)
            {
                await RemoveFromQueueAsync(finished);
            }

            return claimed;
        }

        /// <summary>
        /// Records the result of a claimed request. Refused (returns false) when the claim is no longer this worker's -
        /// its lease passed and another attempt took over - so a slow worker cannot overwrite a newer result.
        /// </summary>
        public async Task<bool> CompleteAsync(UserRefreshRequest result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            var row = await ReadVersionedAsync(RequestKey(result.Id));
            var current = Deserialize<UserRefreshRequest>(row.Value);
            if (current == null || current.ClaimId != result.ClaimId || current.IsFinished)
            {
                return false;
            }

            result.CompletedUtc = _utcNow();
            result.LeaseUntilUtc = null;
            if (!await TrySetAsync(RequestKey(result.Id), Serialize(result), row.VersionToken))
            {
                return false;
            }

            await SealAsync(result);
            await RemoveFromQueueAsync(new[] { result.Id });
            return true;
        }

        /// <summary>
        /// A conditional write cannot carry a time-to-live, so a finished request is written once more with one. Nothing
        /// changes a finished request, so this unconditional write cannot overwrite anyone.
        /// </summary>
        private Task SealAsync(UserRefreshRequest finished)
            => WriteAsync(RequestKey(finished.Id), Serialize(finished), RecordLifetime);

        /// <summary>
        /// Records that a licence check of one person completed against a complete directory answer. Only a trusted check
        /// is recorded: a failed, partial or out-of-scope attempt says nothing about whether the person's licences are right.
        /// </summary>
        public Task RecordVerificationAsync(int userId, UserVerificationRecord record)
            => WriteAsync(VerificationKey(userId), Serialize(record), RecordLifetime);

        public async Task<UserVerificationRecord> GetVerificationAsync(int userId)
            => Deserialize<UserVerificationRecord>(await ReadAsync(VerificationKey(userId)));

        /// <summary>
        /// Advances the licence-data generation, so report caches keyed on it stop serving results computed before a
        /// licence change. Last-writer-wins is fine: any new value differs from every cached key.
        /// </summary>
        public Task BumpLicenceDataGenerationAsync()
            => WriteAsync(GenerationKey, _utcNow().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N").Substring(0, 6), null);

        public Task<string> GetLicenceDataGenerationAsync() => ReadAsync(GenerationKey);

        /// <summary>
        /// How long the scheduled user import's "running" marker lives without a heartbeat. A crashed import stops
        /// holding one-person refreshes back after this; a live one refreshes it every minute.
        /// </summary>
        public static readonly TimeSpan ScheduledImportMarkerLifetime = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Marks the scheduled user import as running. One-person refreshes wait while it is marked, even if the import
        /// lost its SQL lock: a batch of its older reading may still be in flight, and must not land after theirs.
        /// </summary>
        public Task MarkScheduledImportActiveAsync()
            => WriteAsync(ScheduledImportKey, _utcNow().ToString("o", CultureInfo.InvariantCulture), ScheduledImportMarkerLifetime);

        public Task ClearScheduledImportActiveAsync() => DeleteAsync(ScheduledImportKey);

        public async Task<bool> IsScheduledImportActiveAsync() => !string.IsNullOrEmpty(await ReadAsync(ScheduledImportKey));

        /// <summary>
        /// Deletes records whose 30-day lifetime has passed. Table storage only enforces a lifetime when a row is read,
        /// and most of these - requests, people's histories, verifications - hold UPNs and are never read again.
        /// Returns how many were deleted; 0 for a store that expires rows itself.
        /// </summary>
        public async Task<int> PurgeExpiredAsync(int maxToDelete = 5000)
        {
            if (!(_values is AzureTableKeyValueStore table)) return 0;
            try { return await table.PurgeExpiredAsync(maxToDelete).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new UserRefreshStoreUnavailableException($"Couldn't purge expired rows from {_values.Description}.", ex); }
        }

        private async Task RemoveFromQueueAsync(IEnumerable<string> ids)
        {
            var remove = new HashSet<string>(ids);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var queue = await ReadVersionedAsync(QueueKey);
                var current = Deserialize<List<string>>(queue.Value) ?? new List<string>();
                var kept = current.Where(id => !remove.Contains(id)).ToList();
                if (kept.Count == current.Count) return;
                if (await TrySetAsync(QueueKey, Serialize(kept), queue.VersionToken)) return;
            }
        }

        private async Task RememberForUserAsync(int userId, string requestId)
        {
            var ids = Deserialize<List<string>>(await ReadAsync(UserKey(userId))) ?? new List<string>();
            ids.Add(requestId);
            if (ids.Count > RecentPerUser) ids = ids.Skip(ids.Count - RecentPerUser).ToList();
            await WriteAsync(UserKey(userId), Serialize(ids), RecordLifetime);
        }

        internal static string RequestKey(string id) => "Request:" + id;
        internal static string UserKey(int userId) => "User:" + userId.ToString(CultureInfo.InvariantCulture);
        internal static string VerificationKey(int userId) => "Verified:" + userId.ToString(CultureInfo.InvariantCulture);

        private static string Serialize(object value) => JsonConvert.SerializeObject(value);

        private static T Deserialize<T>(string json) where T : class
            => string.IsNullOrEmpty(json) ? null : JsonConvert.DeserializeObject<T>(json);

        private async Task<string> ReadAsync(string key)
        {
            try { return await _values.GetStringAsync(key).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new UserRefreshStoreUnavailableException($"Couldn't read '{key}' from {_values.Description}.", ex); }
        }

        private async Task<VersionedValue> ReadVersionedAsync(string key)
        {
            try { return await _values.GetVersionedAsync(key).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new UserRefreshStoreUnavailableException($"Couldn't read '{key}' from {_values.Description}.", ex); }
        }

        private async Task WriteAsync(string key, string value, TimeSpan? ttl)
        {
            try { await _values.SetStringAsync(key, value, ttl).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new UserRefreshStoreUnavailableException($"Couldn't save '{key}' to {_values.Description}.", ex); }
        }

        private async Task<bool> TrySetAsync(string key, string value, string expectedVersion)
        {
            try { return await _values.TrySetStringAsync(key, value, expectedVersion).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new UserRefreshStoreUnavailableException($"Couldn't save '{key}' to {_values.Description}.", ex); }
        }

        private async Task DeleteAsync(string key)
        {
            try { await _values.DeleteAsync(key).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new UserRefreshStoreUnavailableException($"Couldn't delete '{key}' from {_values.Description}.", ex); }
        }
    }
}
