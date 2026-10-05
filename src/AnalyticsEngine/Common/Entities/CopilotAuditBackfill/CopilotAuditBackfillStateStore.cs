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
            var seed = (int)Math.Min(int.MaxValue - 1, Math.Max(1, (now - IdEpoch).TotalSeconds));
            var id = Math.Max(await GetLatestIdAsync().ConfigureAwait(false) + 1, seed);
            var job = new CopilotAuditBackfillJob
            {
                Id = id,
                State = CopilotAuditBackfillStates.Queued,
                RequestedBy = requestedBy == null || requestedBy.Length <= 256 ? requestedBy : requestedBy.Substring(0, 256),
                CreatedUtc = now,
                UpdatedUtc = now,
                StartUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc),
                EndUtc = DateTime.SpecifyKind(endUtc, DateTimeKind.Utc),
            };
            job.PendingSlices = CopilotAuditBackfillSlicer.BuildDaySlices(job.StartUtc, job.EndUtc);
            await SaveAsync(job).ConfigureAwait(false);
            await WriteAsync(LatestKey, id.ToString(CultureInfo.InvariantCulture), null).ConfigureAwait(false);
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

        private async Task<int> GetLatestIdAsync()
        {
            var value = await ReadAsync(LatestKey).ConfigureAwait(false);
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }

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
    }

    public sealed class CopilotAuditBackfillStateUnavailableException : Exception
    {
        public CopilotAuditBackfillStateUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    }
}
