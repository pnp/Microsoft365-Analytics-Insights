using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Common.Entities.CopilotAuditBackfill
{
    public sealed class CopilotAuditBackfillStateStore
    {
        private const string LatestKey = "LatestJob";
        private const string SubmissionLedgerKey = "SubmissionLedger";
        private static readonly DateTime IdEpoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly IKeyValueStore _values;
        private readonly Func<DateTime> _utcNow;

        public CopilotAuditBackfillStateStore(IKeyValueStore values, bool isDurable, Func<DateTime> utcNow = null)
        {
            _values = values ?? throw new ArgumentNullException(nameof(values));
            IsDurable = isDurable;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public bool IsDurable { get; }
        public string Description => _values.Description;

        public async Task<CopilotAuditBackfillJob> CreateAsync(DateTime startUtc, DateTime endUtc, string requestedBy)
        {
            var now = _utcNow();
            var latestId = await GetLatestIdAsync().ConfigureAwait(false);
            var previous = latestId > 0 ? await GetAsync(latestId).ConfigureAwait(false) : null;
            var job = NewJob(NextId(latestId, now), now, startUtc, endUtc, requestedBy, previous);
            await SaveAsync(job).ConfigureAwait(false);
            await WriteAsync(LatestKey, job.Id.ToString(CultureInfo.InvariantCulture), null).ConfigureAwait(false);
            return job;
        }

        /// <summary>
        /// Creates a job only while no job is queued or running, and atomically: of two concurrent starts (a double
        /// click, two tabs, two administrators) exactly one gets a job; the other, like a start while a job is active,
        /// gets null. The new job's row is inserted only if absent, so a start that picked the same id in the same
        /// second never overwrites it (a start whose own insert landed but lost its reply recognises the row by its
        /// start attempt), and the latest-job pointer moves only from the version this call read the
        /// previous job under, so a start that lost the race never replaces the winner. A start told it lost that
        /// in fact won is answered with its job as it now stands, and a row is deleted only on the version it was
        /// reserved under, so a published job is never deleted. A store without conditional
        /// writes cannot make that promise and falls back to check-then-write. Throws
        /// <see cref="CopilotAuditBackfillStateUnavailableException"/> when the store fails, including when it cannot
        /// confirm whether a refused pointer write in fact landed.
        /// </summary>
        public async Task<CopilotAuditBackfillJob> TryCreateWhenIdleAsync(DateTime startUtc, DateTime endUtc, string requestedBy,
            bool copilotImportEnabled)
        {
            if (!(_values is IConditionalKeyValueStore conditional))
            {
                var current = await GetLatestAsync().ConfigureAwait(false);
                if (current != null && current.IsActive) return null;
                var created = await CreateAsync(startUtc, endUtc, requestedBy).ConfigureAwait(false);
                created.CopilotImportEnabled = copilotImportEnabled;
                await SaveAsync(created).ConfigureAwait(false);
                return created;
            }

            var now = _utcNow();
            var latest = await StoreAsync(LatestKey, "read", () => conditional.GetVersionedAsync(LatestKey)).ConfigureAwait(false);
            var latestId = ParseId(latest.Value);
            var previous = latestId > 0 ? await GetAsync(latestId).ConfigureAwait(false) : null;
            if (previous != null && previous.IsActive) return null;

            var job = NewJob(NextId(latestId, now), now, startUtc, endUtc, requestedBy, previous);
            job.CopilotImportEnabled = copilotImportEnabled;
            job.StartAttempt = Guid.NewGuid().ToString("N");
            var key = JobKey(job.Id);
            if (!await StoreAsync(key, "save", () => conditional.TrySetStringAsync(key, JsonConvert.SerializeObject(job), null)).ConfigureAwait(false))
            {
                // "Not inserted" is not proof either: an insert whose reply was lost is retried by the SDK, and the
                // retry then fails with 409 on the row its own first attempt created. That start would be refused as
                // "a job is active" while no job runs, and leave a row with no time-to-live behind. Only a row carrying
                // this call's start attempt is this call's. Matching on what was requested is not enough: two starts by
                // the same administrator for the same range in the same clock tick ask for the same thing, and the one
                // that adopted the other's row would publish it while its owner is still to rewrite it.
                var existing = await StoreAsync(key, "read", () => conditional.GetVersionedAsync(key)).ConfigureAwait(false);
                if (!string.Equals(StartAttemptOf(existing.Value), job.StartAttempt, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            // Rewritten with its time-to-live before it becomes the latest job, so a row left behind by a lost race
            // below still expires. Until the pointer names it, nothing but this call writes the row; once it does, the
            // importer, a cancel and the job's own progress do. So the version it holds now - before it is published -
            // is the proof, below, of whether it ever was.
            await SaveAsync(job).ConfigureAwait(false);
            var reserved = await StoreAsync(key, "read", () => conditional.GetVersionedAsync(key)).ConfigureAwait(false);
            var id = job.Id.ToString(CultureInfo.InvariantCulture);
            if (await StoreAsync(LatestKey, "save", () => conditional.TrySetStringAsync(LatestKey, id, latest.VersionToken)).ConfigureAwait(false))
            {
                return job;
            }

            // "Not written" is not proof the pointer did not move. The table SDK retries a write whose reply it did
            // not get, and an If-Match retry then fails on the ETag its own first attempt changed - so a start that
            // DID become the latest job can be told it lost. Deleting its row then would leave the pointer naming a
            // job that no longer exists, and tell the administrator a start that happened was refused. If the
            // pointer cannot be re-read, the outcome is unknown: the row is left (it expires with the job
            // time-to-live if nothing names it) and the caller gets "state unavailable", not "a job is active".
            var pointer = await StoreAsync(LatestKey, "read", () => conditional.GetVersionedAsync(LatestKey)).ConfigureAwait(false);
            if (string.Equals(pointer.Value, id, StringComparison.Ordinal))
            {
                return job;
            }

            // The pointer names another job. Either this start lost, or it won and - while its reply was still on the
            // way - its job was picked up and finished, and a later start moved the pointer on: a start replaces only a
            // job that is no longer active, and only a write to a published row makes it so. A row still at the
            // version it was reserved under was therefore never published: delete it, on that version, and refuse.
            // A row written since was published and has run: it is this call's job, as it now stands.
            var row = await StoreAsync(key, "read", () => conditional.GetVersionedAsync(key)).ConfigureAwait(false);
            if (row.Value != null && !string.Equals(row.VersionToken, reserved.VersionToken, StringComparison.Ordinal))
            {
                return await GetAsync(job.Id).ConfigureAwait(false);
            }

            await TryDeleteUnclaimedAsync(conditional, key, reserved.VersionToken).ConfigureAwait(false);
            return null;
        }

        private static string StartAttemptOf(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonConvert.DeserializeObject<CopilotAuditBackfillJob>(json)?.StartAttempt; }
            catch (JsonException) { return null; }
        }

        private static async Task TryDeleteUnclaimedAsync(IConditionalKeyValueStore conditional, string key, string reservedVersion)
        {
            try
            {
                // Only on the version it was reserved under: a row written since is not unclaimed.
                await conditional.TryDeleteAsync(key, reservedVersion).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Nothing points at the row, and it expires with the job time-to-live.
            }
        }

        private static int NextId(int latestId, DateTime now)
        {
            var seed = (int)Math.Min(int.MaxValue - 1, Math.Max(1, (now - IdEpoch).TotalSeconds));
            return Math.Max(latestId + 1, seed);
        }

        private static CopilotAuditBackfillJob NewJob(int id, DateTime now, DateTime startUtc, DateTime endUtc, string requestedBy,
            CopilotAuditBackfillJob previous)
        {
            var job = new CopilotAuditBackfillJob
            {
                Id = id,
                State = CopilotAuditBackfillStates.Queued,
                RequestedBy = requestedBy == null || requestedBy.Length <= 256 ? requestedBy : requestedBy.Substring(0, 256),
                CreatedUtc = now,
                UpdatedUtc = now,
                StartUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc),
                EndUtc = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc),
                SubmissionTimestampsUtc = new System.Collections.Generic.List<DateTime>(
                    previous?.SubmissionTimestampsUtc ?? new System.Collections.Generic.List<DateTime>()),
                SubmissionsPausedUntilUtc = previous?.SubmissionsPausedUntilUtc,
                SubmitFailureBackoffMinutes = previous?.SubmitFailureBackoffMinutes ?? 0,
            };
            job.PendingSlices = CopilotAuditBackfillSlicer.BuildDaySlices(job.StartUtc, job.EndUtc);
            return job;
        }

        public async Task<CopilotAuditBackfillJob> GetAsync(int id)
        {
            var json = await ReadAsync(JobKey(id)).ConfigureAwait(false);
            if (json == null) return null;
            var job = JsonConvert.DeserializeObject<CopilotAuditBackfillJob>(json);
            if (job == null) return null;
            job.PendingSlices = job.PendingSlices ?? new System.Collections.Generic.List<CopilotAuditBackfillSlice>();
            job.InFlightSlices = job.InFlightSlices ?? new System.Collections.Generic.List<CopilotAuditBackfillSlice>();
            if (job.CurrentSlice != null)
            {
                job.InFlightSlices.Add(job.CurrentSlice);
                job.CurrentSlice = null;
            }
            job.CompletedDays = job.CompletedDays ?? new System.Collections.Generic.List<string>();
            job.Gaps = job.Gaps ?? new System.Collections.Generic.List<CopilotAuditBackfillGap>();
            job.SubmissionTimestampsUtc = job.SubmissionTimestampsUtc ?? new System.Collections.Generic.List<DateTime>();
            job.MappingFailureCounts = job.MappingFailureCounts ?? new System.Collections.Generic.Dictionary<string, long>(StringComparer.Ordinal);
            job.CancelRequested = await IsCancelRequestedAsync(id).ConfigureAwait(false);
            return job;
        }

        public async Task<CopilotAuditBackfillJob> GetLatestAsync()
        {
            var id = await GetLatestIdAsync().ConfigureAwait(false);
            return id > 0 ? await GetAsync(id).ConfigureAwait(false) : null;
        }

        public Task SaveAsync(CopilotAuditBackfillJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            job.UpdatedUtc = _utcNow();
            return WriteAsync(JobKey(job.Id), JsonConvert.SerializeObject(job), TimeSpan.FromDays(180));
        }

        public async Task<bool> RequestCancelAsync(int id)
        {
            var job = await GetAsync(id).ConfigureAwait(false);
            if (job == null || !job.IsActive) return false;
            // The importer owns the job row and may have advanced it since this read. GetAsync reads the marker separately.
            await WriteAsync(CancelKey(id), "1", TimeSpan.FromDays(30)).ConfigureAwait(false);
            return true;
        }

        public async Task<bool> IsCancelRequestedAsync(int id) => await ReadAsync(CancelKey(id)).ConfigureAwait(false) != null;

        public async Task<CopilotAuditBackfillSubmissionLedger> GetSubmissionLedgerAsync()
        {
            var json = await ReadAsync(SubmissionLedgerKey).ConfigureAwait(false);
            var ledger = string.IsNullOrEmpty(json)
                ? new CopilotAuditBackfillSubmissionLedger()
                : JsonConvert.DeserializeObject<CopilotAuditBackfillSubmissionLedger>(json) ?? new CopilotAuditBackfillSubmissionLedger();
            ledger.SubmissionTimestampsUtc = ledger.SubmissionTimestampsUtc ?? new System.Collections.Generic.List<DateTime>();
            return ledger;
        }

        public Task SaveSubmissionLedgerAsync(CopilotAuditBackfillSubmissionLedger ledger)
        {
            ledger = ledger ?? new CopilotAuditBackfillSubmissionLedger();
            ledger.SubmissionTimestampsUtc = ledger.SubmissionTimestampsUtc ?? new System.Collections.Generic.List<DateTime>();
            return WriteAsync(SubmissionLedgerKey, JsonConvert.SerializeObject(ledger), TimeSpan.FromDays(2));
        }

        private async Task<int> GetLatestIdAsync() => ParseId(await ReadAsync(LatestKey).ConfigureAwait(false));

        private static int ParseId(string value)
            => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

        private static string JobKey(int id) => "Job:" + id.ToString(CultureInfo.InvariantCulture);
        private static string CancelKey(int id) => JobKey(id) + ":Cancel";

        private async Task<string> ReadAsync(string key)
        {
            try { return await _values.GetStringAsync(key).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new CopilotAuditBackfillStateUnavailableException($"Couldn't read '{key}' from {_values.Description}: {ex.Message}", ex); }
        }

        private async Task WriteAsync(string key, string value, TimeSpan? ttl)
        {
            try { await _values.SetStringAsync(key, value, ttl).ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new CopilotAuditBackfillStateUnavailableException($"Couldn't save '{key}' to {_values.Description}: {ex.Message}", ex); }
        }

        private async Task<T> StoreAsync<T>(string key, string verb, Func<Task<T>> action)
        {
            try { return await action().ConfigureAwait(false); }
            catch (Exception ex) when (!(ex is OperationCanceledException)) { throw new CopilotAuditBackfillStateUnavailableException($"Couldn't {verb} '{key}' in {_values.Description}: {ex.Message}", ex); }
        }
    }

    public sealed class CopilotAuditBackfillStateUnavailableException : Exception
    {
        public CopilotAuditBackfillStateUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    }
}
