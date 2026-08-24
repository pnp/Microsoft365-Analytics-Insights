using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// How long one adoption analysis took, broken down by step.
    ///
    /// This exists because the analysis is the most expensive thing the web app does, it runs against
    /// tables that are small on a demo tenant and enormous on a real one, and its failure mode is
    /// quiet: a query that exceeds <see cref="CopilotAdoptionService.QueryTimeoutSecs"/> degrades to a
    /// warning on the page rather than an error, so a tenant can sit with a half-populated report for
    /// months and nobody raises a ticket. Timings shipped to App Insights turn that into something an
    /// operator can see and alert on.
    ///
    /// Contains no customer data by construction: step names are compile-time constants and every
    /// value is a duration or a count.
    /// </summary>
    public class CopilotAdoptionDiagnostics
    {
        /// <summary>Wall-clock time for the whole analysis.</summary>
        [JsonProperty("totalMs")]
        public long TotalMs { get; set; }

        /// <summary>Per-step wall-clock times, in the order the steps ran.</summary>
        [JsonProperty("steps")]
        public List<CopilotAdoptionStepTiming> Steps { get; set; } = new List<CopilotAdoptionStepTiming>();

        /// <summary>The slowest step, which is the one worth looking at first. Null when nothing ran.</summary>
        [JsonIgnore]
        public CopilotAdoptionStepTiming SlowestStep =>
            Steps.Count == 0 ? null : Steps.OrderByDescending(s => s.DurationMs).First();

        /// <summary>
        /// Records a completed step. Called from a finally block, so a step that threw is still timed -
        /// a query that failed after 90 seconds is exactly the one an operator needs to see.
        /// </summary>
        public void Record(string step, long durationMs, bool failed = false)
        {
            if (string.IsNullOrWhiteSpace(step)) return;

            Steps.Add(new CopilotAdoptionStepTiming
            {
                Step = step,
                DurationMs = durationMs,
                Failed = failed,
            });
        }
    }

    /// <summary>One timed step of the analysis.</summary>
    public class CopilotAdoptionStepTiming
    {
        /// <summary>A compile-time constant name, never anything derived from tenant data.</summary>
        [JsonProperty("step")]
        public string Step { get; set; }

        [JsonProperty("durationMs")]
        public long DurationMs { get; set; }

        /// <summary>Whether the step degraded to a warning rather than completing.</summary>
        [JsonProperty("failed")]
        public bool Failed { get; set; }
    }

    /// <summary>
    /// The step names, as constants so the App Insights metric names are stable and greppable. A
    /// renamed step silently breaks an operator's saved query or alert, so they live in one place.
    /// </summary>
    public static class CopilotAdoptionSteps
    {
        public const string LicenceTypes = "LicenceTypes";
        public const string DataSourceProbes = "DataSourceProbes";
        public const string LicensedUsers = "LicensedUsers";
        public const string UsageByApp = "UsageByApp";
        public const string WeeklyTrend = "WeeklyTrend";
        public const string LicenceOpportunities = "LicenceOpportunities";
        public const string AgentEstate = "AgentEstate";
        public const string UnlicensedPopulation = "UnlicensedPopulation";
        public const string ResourceTypes = "ResourceTypes";
        public const string Scoring = "Scoring";
    }
}
