using System;
using System.Collections.Generic;

namespace DataUtils.Health
{
    /// <summary>Minimal component status for the roll-up (decoupled from the web view-model).</summary>
    public class ComponentStatusInput
    {
        public string Component { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
    }

    /// <summary>Minimal per-job liveness for the roll-up.</summary>
    public class JobLivenessInput
    {
        public string JobName { get; set; }
        public DateTime? LastCycleUtc { get; set; }
    }

    /// <summary>
    /// Everything the overall-health roll-up needs, decoupled from the web view-model
    /// (<c>Web/Models/Health/</c>) so the roll-up rules can be unit-tested without a
    /// database, App Insights or the Web project. See HEALTH-MONITORING-DESIGN.md (#144).
    /// </summary>
    public class HealthRollupInput
    {
        public DateTime NowUtc { get; set; } = DateTime.UtcNow;
        public string DataError { get; set; }
        /// <summary>Null = not checked; true = at latest migration; false = DB behind this build.</summary>
        public bool? SchemaUpToDate { get; set; }
        public int PendingMigrationCount { get; set; }
        public List<ComponentStatusInput> Components { get; set; } = new List<ComponentStatusInput>();
        public List<JobLivenessInput> Jobs { get; set; } = new List<JobLivenessInput>();
        public long SqlCapacityExceptions24h { get; set; }
        public bool CallsImportEnabled { get; set; }
        public string WebhookState { get; set; }
        public bool AppInsightsConfigured { get; set; }
        /// <summary>Any of the App Insights cards failed to load (liveness / exceptions / component health).</summary>
        public bool AnyTelemetryQueryError { get; set; }
        public int CycleSlaHours { get; set; } = 24;
    }

    /// <summary>
    /// Pure, side-effect-free roll-up of the individual health cards into one traffic-light.
    /// Unhealthy beats Degraded beats Healthy. App Insights simply not being configured is treated as
    /// informational (no downgrade); a configured-but-failing telemetry query IS a downgrade.
    /// </summary>
    public static class HealthRollup
    {
        public static HealthStatus Evaluate(HealthRollupInput input, out List<string> reasons)
        {
            var reasonList = new List<string>();
            reasons = reasonList;
            if (input == null) return HealthStatus.Healthy;

            var worst = HealthStatus.Healthy;
            void Raise(HealthStatus status, string reason)
            {
                if ((int)status > (int)worst) worst = status;
                if (!string.IsNullOrEmpty(reason)) reasonList.Add(reason);
            }

            if (!string.IsNullOrEmpty(input.DataError))
                Raise(HealthStatus.Unhealthy, "Database query failed: " + input.DataError);

            if (input.SchemaUpToDate == false)
                Raise(HealthStatus.Unhealthy, $"Database schema is behind this build ({input.PendingMigrationCount} migration(s) pending) - run the upgrader.");

            foreach (var c in input.Components ?? new List<ComponentStatusInput>())
            {
                if (string.Equals(c.Status, HealthStatus.Unhealthy.ToString(), StringComparison.OrdinalIgnoreCase))
                    Raise(HealthStatus.Unhealthy, $"{c.Component} is unhealthy: {c.Detail}");
                else if (string.Equals(c.Status, HealthStatus.Degraded.ToString(), StringComparison.OrdinalIgnoreCase))
                    Raise(HealthStatus.Degraded, $"{c.Component} is degraded: {c.Detail}");
            }

            foreach (var job in input.Jobs ?? new List<JobLivenessInput>())
            {
                if (!job.LastCycleUtc.HasValue)
                {
                    Raise(HealthStatus.Degraded, $"No completed import cycle seen for {job.JobName}.");
                    continue;
                }
                var hours = (input.NowUtc - job.LastCycleUtc.Value).TotalHours;
                if (hours > input.CycleSlaHours * 2)
                    Raise(HealthStatus.Unhealthy, $"{job.JobName} hasn't completed a cycle in {hours:F0}h (SLA {input.CycleSlaHours}h).");
                else if (hours > input.CycleSlaHours)
                    Raise(HealthStatus.Degraded, $"{job.JobName} last completed a cycle {hours:F0}h ago (SLA {input.CycleSlaHours}h).");
            }

            if (input.SqlCapacityExceptions24h > 0)
                Raise(HealthStatus.Degraded, $"{input.SqlCapacityExceptions24h} SQL capacity / read-only exception(s) in the last 24h - check database storage.");

            // Missing is often transient (the importer re-registers each cycle), so treat as Degraded, not Unhealthy.
            if (input.CallsImportEnabled && (string.Equals(input.WebhookState, "Missing", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(input.WebhookState, "Error", StringComparison.OrdinalIgnoreCase)))
                Raise(HealthStatus.Degraded, $"Teams calls webhook subscription is '{input.WebhookState}'.");

            if (input.AppInsightsConfigured && input.AnyTelemetryQueryError)
                Raise(HealthStatus.Degraded, "Health telemetry queries are failing - see the affected cards.");

            if (worst == HealthStatus.Healthy && reasons.Count == 0)
                reasons.Add("All checks passing.");

            return worst;
        }
    }
}
