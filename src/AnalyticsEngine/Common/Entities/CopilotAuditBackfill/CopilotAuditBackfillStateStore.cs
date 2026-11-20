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
        /// second never overwrites it (a start whose own insert landed but lost its reply recognises the row as its
        /// own), and the latest-job pointer moves only from the version this call read the
        /// previous job under, so a start that lost the race never replaces the winner. A store without conditional
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
            var key = JobKey(job.Id);
            var json = JsonConvert.SerializeObject(job);
            if (!await StoreAsync(key, "save", () => conditional.TrySetStringAsync(key, json, null)).ConfigureAwait(false))
            {
                // "Not inserted" is not proof either: an insert whose reply was lost is retried by the SDK, and the
                // retry then fails with 409 on the row its own first attempt created. That start would be refused as
                // "a job is active" while no job runs, and leave a row with no time-to-live behind. A row holding
                // exactly what this call wrote (its creation instant to the tick) is this call's; anything else is
                // another start's, and is left alone.
                var existing = await StoreAsync(key, "read", () => conditional.GetVersionedAsync(key)).ConfigureAwait(false);
                if (!string.Equals(existing.Value, json, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            // Rewritten with its time-to-live before it becomes the latest job, so nobody else ever writes it and a
            // row left behind by a lost race below still expires.
            await SaveAsync(job).ConfigureAwait(false);
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

            await TryDeleteUnclaimedAsync(conditional, key).ConfigureAwait(false);
            return null;
        }

        private static async Task TryDeleteUnclaimedAsync(IConditionalKeyValueStore conditional, string key)
        {
            try
            {
                var row = await conditional.GetVersionedAsync(key).ConfigureAwait(false);
                await conditional.TryDeleteAsync(key, row.VersionToken).ConfigureAwait(false);
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
            await WriteAsync(CancelKey(id), "1", TimeSpan.FromDays(30)).ConfigureAwait(false);
            job.CancelRequested = true;
            await SaveAsync(job).ConfigureAwait(false);
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
