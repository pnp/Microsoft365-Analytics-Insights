using Common.Entities.CopilotAuditBackfill;
using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.CopilotAuditBackfill
{
    public sealed class CopilotAuditBackfillStatusModel
    {
        [JsonProperty("stateDurable")]
        public bool StateDurable { get; set; }
        [JsonProperty("copilotImportEnabled")]
        public bool CopilotImportEnabled { get; set; }
        [JsonProperty("latestJob")]
        public CopilotAuditBackfillJobModel LatestJob { get; set; }
    }

    public sealed class CopilotAuditBackfillJobModel
    {
        [JsonProperty("id")]
        public int Id { get; set; }
        [JsonProperty("state")]
        public string State { get; set; }
        [JsonProperty("requestedBy")]
        public string RequestedBy { get; set; }
        [JsonProperty("createdUtc")]
        public DateTime CreatedUtc { get; set; }
        [JsonProperty("updatedUtc")]
        public DateTime UpdatedUtc { get; set; }
        [JsonProperty("startedUtc")]
        public DateTime? StartedUtc { get; set; }
        [JsonProperty("completedUtc")]
        public DateTime? CompletedUtc { get; set; }
        [JsonProperty("startUtc")]
        public DateTime StartUtc { get; set; }
        [JsonProperty("endUtc")]
        public DateTime EndUtc { get; set; }
        [JsonProperty("pendingSlices")]
        public int PendingSlices { get; set; }
        [JsonProperty("inFlightSlices")]
        public int InFlightSlices { get; set; }
        [JsonProperty("slicesSubmitted")]
        public int SlicesSubmitted { get; set; }
        [JsonProperty("slicesCompleted")]
        public int SlicesCompleted { get; set; }
        [JsonProperty("slicesSplit")]
        public int SlicesSplit { get; set; }
        [JsonProperty("completedDays")]
        public string[] CompletedDays { get; set; }
        [JsonProperty("failedDays")]
        public string[] FailedDays { get; set; }
        [JsonProperty("incompleteDays")]
        public string[] IncompleteDays { get; set; }
        [JsonProperty("recordsSeen")]
        public long RecordsSeen { get; set; }
        [JsonProperty("recordsImported")]
        public long RecordsImported { get; set; }
        [JsonProperty("recordsAlreadyPresent")]
        public long RecordsAlreadyPresent { get; set; }
        [JsonProperty("permissionStatus")]
        public string PermissionStatus { get; set; }
        [JsonProperty("copilotImportEnabled")]
        public bool CopilotImportEnabled { get; set; }
        [JsonProperty("lastErrorCode")]
        public string LastErrorCode { get; set; }
        [JsonProperty("cancelRequested")]
        public bool CancelRequested { get; set; }
        [JsonProperty("currentSliceStartUtc")]
        public DateTime? CurrentSliceStartUtc { get; set; }
        [JsonProperty("currentSliceEndUtc")]
        public DateTime? CurrentSliceEndUtc { get; set; }
    }

    public sealed class CopilotAuditBackfillStartRequest
    {
        [JsonProperty("startDateUtc")]
        public DateTime? StartDateUtc { get; set; }
    }

    public sealed class CopilotAuditBackfillError
    {
        [JsonProperty("code")]
        public string Code { get; set; }
    }

    public sealed class CopilotAuditBackfillRequestException : Exception
    {
        public CopilotAuditBackfillRequestException(HttpStatusCode status, string code) : base(code)
        {
            Status = status;
            Code = code;
        }
        public HttpStatusCode Status { get; }
        public string Code { get; }
    }

    internal sealed class CopilotAuditBackfillService
    {
        private static readonly Lazy<AnalyticsLogger> ProductionLogger = new Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "CopilotAuditBackfill"));

        private static readonly Lazy<CopilotAuditBackfillStateStore> ProductionState = new Lazy<CopilotAuditBackfillStateStore>(() =>
        {
            var config = new AppConfig();
            var durable = StateStore.TryOpen(config, StatePartitions.CopilotAuditBackfill, ProductionLogger.Value);
            return durable != null
                ? new CopilotAuditBackfillStateStore(durable, isDurable: true)
                : new CopilotAuditBackfillStateStore(new InMemoryKeyValueStore(), isDurable: false);
        });

        private readonly CopilotAuditBackfillStateStore _state;
        private readonly AppConfig _config;
        private readonly Func<DateTime> _utcNow;

        public CopilotAuditBackfillService(CopilotAuditBackfillStateStore state, AppConfig config, Func<DateTime> utcNow = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public static CopilotAuditBackfillService ForThisDeployment()
            => new CopilotAuditBackfillService(ProductionState.Value, new AppConfig());

        public async Task<CopilotAuditBackfillStatusModel> GetStatusAsync()
            => new CopilotAuditBackfillStatusModel
            {
                StateDurable = _state.IsDurable,
                CopilotImportEnabled = _config.ImportJobSettings.Copilot,
                LatestJob = ToModel(await _state.GetLatestAsync().ConfigureAwait(false)),
            };

        public async Task<CopilotAuditBackfillJobModel> StartAsync(CopilotAuditBackfillStartRequest request, string requestedBy)
        {
            var latest = await _state.GetLatestAsync().ConfigureAwait(false);
            if (latest != null && latest.IsActive)
            {
                throw new CopilotAuditBackfillRequestException(HttpStatusCode.Conflict, "jobActive");
            }
            if (!_state.IsDurable)
            {
                throw new CopilotAuditBackfillRequestException(HttpStatusCode.Conflict, CopilotAuditBackfillErrorCodes.StateNotDurable);
            }
            if (!_config.ImportJobSettings.Copilot)
            {
                throw new CopilotAuditBackfillRequestException(HttpStatusCode.Conflict, CopilotAuditBackfillErrorCodes.CopilotImportOff);
            }

            var now = _utcNow();
            var start = CopilotAuditBackfillSlicer.ClampStart(request?.StartDateUtc, now);
            var job = await _state.CreateAsync(start, now, requestedBy).ConfigureAwait(false);
            job.CopilotImportEnabled = _config.ImportJobSettings.Copilot;
            await _state.SaveAsync(job).ConfigureAwait(false);
            return ToModel(job);
        }

        public async Task<CopilotAuditBackfillJobModel> CancelAsync(int id)
        {
            if (!await _state.RequestCancelAsync(id).ConfigureAwait(false))
            {
                var existing = await _state.GetAsync(id).ConfigureAwait(false);
                throw new CopilotAuditBackfillRequestException(existing == null ? HttpStatusCode.NotFound : HttpStatusCode.Conflict,
                    existing == null ? "jobNotFound" : "jobNotActive");
            }
            return ToModel(await _state.GetAsync(id).ConfigureAwait(false));
        }

        private static CopilotAuditBackfillJobModel ToModel(CopilotAuditBackfillJob job)
        {
            if (job == null) return null;
            var gaps = job.Gaps ?? new System.Collections.Generic.List<CopilotAuditBackfillGap>();
            var inFlight = job.InFlightSlices ?? new System.Collections.Generic.List<CopilotAuditBackfillSlice>();
            var firstInFlight = inFlight.OrderBy(s => s.EndUtc).FirstOrDefault();
            return new CopilotAuditBackfillJobModel
            {
                Id = job.Id,
                State = job.State,
                RequestedBy = job.RequestedBy,
                CreatedUtc = job.CreatedUtc,
                UpdatedUtc = job.UpdatedUtc,
                StartedUtc = job.StartedUtc,
                CompletedUtc = job.CompletedUtc,
                StartUtc = job.StartUtc,
                EndUtc = job.EndUtc,
                PendingSlices = job.PendingSlices?.Count ?? 0,
                InFlightSlices = inFlight.Count,
                SlicesSubmitted = job.SlicesSubmitted,
                SlicesCompleted = job.SlicesCompleted,
                SlicesSplit = job.SlicesSplit,
                CompletedDays = (job.CompletedDays ?? new System.Collections.Generic.List<string>()).OrderByDescending(x => x, StringComparer.Ordinal).ToArray(),
                FailedDays = gaps.Where(g => !g.Incomplete).Select(g => g.Day).Distinct().OrderByDescending(x => x, StringComparer.Ordinal).ToArray(),
                IncompleteDays = gaps.Where(g => g.Incomplete).Select(g => g.Day).Distinct().OrderByDescending(x => x, StringComparer.Ordinal).ToArray(),
                RecordsSeen = job.RecordsSeen,
                RecordsImported = job.RecordsImported,
                RecordsAlreadyPresent = job.RecordsAlreadyPresent,
                PermissionStatus = job.PermissionStatus,
                CopilotImportEnabled = job.CopilotImportEnabled,
                LastErrorCode = job.LastErrorCode,
                CancelRequested = job.CancelRequested,
                CurrentSliceStartUtc = firstInFlight?.StartUtc,
                CurrentSliceEndUtc = firstInFlight?.EndUtc,
            };
        }
    }
}
