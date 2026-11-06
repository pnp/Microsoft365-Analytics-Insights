using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    public interface ICopilotAdoptionProgressPlan
    {
        void SetPlan(IEnumerable<string> steps, bool blocked = false, IEnumerable<string> blockedSteps = null);
    }

    public sealed class CopilotAdoptionProgressSnapshot
    {
        [JsonProperty("generationId")]
        public string GenerationId { get; set; }
        [JsonProperty("phase")]
        public string Phase { get; set; }
        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }
        [JsonProperty("percentComplete")]
        public int? PercentComplete { get; set; }
        [JsonProperty("steps")]
        public List<CopilotAdoptionProgressStep> Steps { get; set; }
    }

    public sealed class CopilotAdoptionProgressStep
    {
        [JsonProperty("step")]
        public string Step { get; set; }
        [JsonProperty("state")]
        public string State { get; set; }
        [JsonProperty("durationMs")]
        public long DurationMs { get; set; }
        [JsonProperty("queryElapsedMs")]
        public long? QueryElapsedMs { get; set; }
        [JsonProperty("failure")]
        public string Failure { get; set; }
        [JsonProperty("reference")]
        public string Reference { get; set; }
    }

    /// <summary>
    /// A bounded, memory-only view of real execution boundaries, independent of telemetry delivery.
    /// Snapshots never share mutable step objects with the analysis or with another reader.
    /// </summary>
    public sealed class CopilotAdoptionRunProgress : ICopilotAdoptionRunTelemetry, ICopilotAdoptionProgressPlan
    {
        public const string Publishing = "Publishing";
        private static readonly string[] AllSteps =
        {
            CopilotAdoptionSteps.LicenceTypes, CopilotAdoptionSteps.DataSourceProbes,
            CopilotAdoptionSteps.LicensedUsers, CopilotAdoptionSteps.UsageByApp,
            CopilotAdoptionSteps.SeatHolderTimeSaved, CopilotAdoptionSteps.WeeklyTrend,
            CopilotAdoptionSteps.CoworkReadiness, CopilotAdoptionSteps.LicenceOpportunities,
            CopilotAdoptionSteps.UnlicensedPopulation, CopilotAdoptionSteps.ResourceTypes,
            CopilotAdoptionSteps.AgentEstate, CopilotAdoptionSteps.AgentReach,
            CopilotAdoptionSteps.AgentGrowth, CopilotAdoptionSteps.MicrosoftReportFigures,
            CopilotAdoptionSteps.Scoring, Publishing,
        };
        private readonly object _sync = new object();
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly Dictionary<string, StepState> _steps =
            AllSteps.ToDictionary(step => step, step => new StepState(), StringComparer.Ordinal);
        private ICopilotAdoptionRunTelemetry _telemetry = NullCopilotAdoptionRunTelemetry.Instance;
        private string _phase = "starting";
        private bool _planKnown;

        public string GenerationId { get; } = Guid.NewGuid().ToString("N");

        public void Observe(ICopilotAdoptionRunTelemetry telemetry)
        {
            _telemetry = telemetry ?? NullCopilotAdoptionRunTelemetry.Instance;
        }

        public void SetPhase(string phase)
        {
            lock (_sync) _phase = phase;
        }

        public void SetPlan(IEnumerable<string> steps, bool blocked = false, IEnumerable<string> blockedSteps = null)
        {
            var planned = new HashSet<string>(steps, StringComparer.Ordinal) { Publishing };
            var blockedSet = new HashSet<string>(blockedSteps ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            lock (_sync)
            {
                foreach (var entry in _steps)
                    if (!planned.Contains(entry.Key) && entry.Value.State == "pending")
                        entry.Value.State = blocked || blockedSet.Contains(entry.Key) ? "blocked" : "skipped";
                _planKnown = true;
            }
        }

        public CopilotAdoptionProgressSnapshot Snapshot(bool published = false)
        {
            lock (_sync)
            {
                var elapsed = _watch.ElapsedMilliseconds;
                var steps = AllSteps.Select(name =>
                {
                    var step = _steps[name];
                    return new CopilotAdoptionProgressStep
                    {
                        Step = name,
                        State = name == Publishing && published ? "loaded" : step.State,
                        DurationMs = step.State == "running" ? elapsed - step.StartedMs : step.DurationMs,
                        QueryElapsedMs = step.QueryStartedMs.HasValue ? elapsed - step.QueryStartedMs.Value : (long?)null,
                        Failure = step.Failure,
                        Reference = step.Reference,
                    };
                }).ToList();
                var planned = steps.Where(step => step.State != "skipped" && step.State != "blocked").ToList();
                var processed = planned.Count(step => step.State == "loaded" || step.State == "failed");
                return new CopilotAdoptionProgressSnapshot
                {
                    GenerationId = GenerationId,
                    Phase = published ? "completed" : _phase,
                    ElapsedMs = elapsed,
                    PercentComplete = _planKnown ? (int?)(processed * 100 / Math.Max(1, planned.Count)) : null,
                    Steps = steps,
                };
            }
        }

        public long StepStarted(string step)
        {
            lock (_sync)
            {
                if (_steps.TryGetValue(step, out var state))
                {
                    state.State = "running";
                    state.StartedMs = _watch.ElapsedMilliseconds;
                    _phase = step == CopilotAdoptionSteps.Scoring ? "scoring" : "analysing";
                }
            }
            return _telemetry.StepStarted(step);
        }

        public void StepCompleted(long operationId, string step, long durationMs, bool failed, CopilotAdoptionFailure failure = null)
        {
            lock (_sync)
            {
                if (_steps.TryGetValue(step, out var state))
                {
                    state.State = failed ? "failed" : "loaded";
                    state.DurationMs = durationMs;
                    state.QueryStartedMs = null;
                    if (failure != null)
                    {
                        state.Failure = failure.FailureKind;
                        state.Reference = failure.ErrorId;
                    }
                }
            }
            _telemetry.StepCompleted(operationId, step, durationMs, failed, failure);
        }

        public long QueryStarted(string step, string query)
        {
            lock (_sync)
                if (step != null && _steps.TryGetValue(step, out var state))
                    state.QueryStartedMs = _watch.ElapsedMilliseconds;
            return _telemetry.QueryStarted(step, query);
        }

        public void QueryCompleted(long operationId, string step, string query, long durationMs, bool failed, CopilotAdoptionFailure failure = null)
        {
            lock (_sync)
                if (step != null && _steps.TryGetValue(step, out var state))
                {
                    state.QueryStartedMs = null;
                    if (failed && failure != null)
                    {
                        state.Failure = failure.FailureKind;
                        state.Reference = failure.ErrorId;
                    }
                }
            _telemetry.QueryCompleted(operationId, step, query, durationMs, failed, failure);
        }

        public void Checkpoint(string stage, long durationMs = 0) => _telemetry.Checkpoint(stage, durationMs);

        private sealed class StepState
        {
            public string State = "pending";
            public long StartedMs;
            public long DurationMs;
            public long? QueryStartedMs;
            public string Failure;
            public string Reference;
        }
    }
}
