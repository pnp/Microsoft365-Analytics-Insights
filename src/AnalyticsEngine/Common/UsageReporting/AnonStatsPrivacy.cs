using System;
using System.Collections.Generic;
using System.Linq;

namespace UsageReporting
{
    /// <summary>
    /// The anonymisation rules for <see cref="AnonAdoptionStats"/>, kept in the shared library so the
    /// importer that applies them, the Telemetry Service that reads them, and the documentation that
    /// describes them all cite exactly the same thresholds.
    ///
    /// Two separate protections, because they answer different questions:
    ///
    /// <list type="bullet">
    /// <item><description><b>Bucketing</b> stops a figure being precise enough to fingerprint a tenant,
    /// or to track a named customer's headcount over time.</description></item>
    /// <item><description><b>Suppression</b> stops a tenant so small that even a bucketed figure
    /// describes a handful of identifiable individuals from being reported at all.</description></item>
    /// </list>
    ///
    /// Label allow-lists are a third, quieter protection: every string that survives into the payload
    /// must match a vocabulary defined HERE, in code. A value read from the customer's database can
    /// therefore never reach the wire by being passed through a label field, even if a future change
    /// starts putting tenant text into one.
    /// </summary>
    public static class AnonStatsPrivacy
    {
        /// <summary>
        /// Minimum Copilot seats before adoption figures are reported at all.
        ///
        /// Below this, a bucketed "5 active users out of 20" still describes a small, identifiable
        /// group, and the rates computed from it swing wildly on one person's behaviour - so the
        /// figures would be both intrusive and useless.
        /// </summary>
        public const int MinimumTenantSeats = 25;

        /// <summary>
        /// Minimum holders before a SKU is reported as its own row. Smaller SKUs are summed into
        /// <see cref="OtherSkuBucket"/>, which is itself dropped if the total does not clear this bar.
        /// </summary>
        public const int MinimumSkuSeats = 10;

        /// <summary>Placeholder for a SKU part number Microsoft does not publish (e.g. a reseller bundle).</summary>
        public const string UnlistedSku = "(unlisted)";

        /// <summary>Placeholder for the roll-up of SKUs below <see cref="MinimumSkuSeats"/>.</summary>
        public const string OtherSkuBucket = "(other)";

        /// <summary>Label used when a count could not be attributed to a known category.</summary>
        public const string UnknownLabel = "(unknown)";

        /// <summary>Fixed vocabulary for <see cref="AnonAdoptionStats.SuppressionReason"/>.</summary>
        public static class SuppressionReasons
        {
            /// <summary>Fewer than <see cref="MinimumTenantSeats"/> Copilot seats.</summary>
            public const string BelowMinimumPopulation = "belowMinimumPopulation";

            /// <summary>The deployment does not import what the analysis needs.</summary>
            public const string NotAvailable = "notAvailable";

            /// <summary>The analysis failed or ran out of time.</summary>
            public const string AnalysisFailed = "analysisFailed";
        }

        /// <summary>
        /// Rounds a count so it describes the scale of a tenant without describing the tenant.
        ///
        /// Zero is preserved exactly and deliberately: "nobody here uses this" is the single most
        /// useful answer in the whole payload, it cannot identify anyone, and rounding it away would
        /// make an unused feature indistinguishable from a barely-used one.
        ///
        /// 1-9 collapse to a single "a handful" value rather than rounding to 0 or 10, so a tiny
        /// non-zero population never masquerades as either an empty one or a ten-strong one.
        /// </summary>
        public static long? Bucket(long? value)
        {
            if (!value.HasValue) return null;

            var v = value.Value;
            if (v < 0) return null;      // Nonsense in, nothing out - never a negative on the wire.
            if (v == 0) return 0;
            if (v < 10) return 5;
            if (v < 100) return RoundTo(v, 10);
            if (v < 1000) return RoundTo(v, 50);
            if (v < 10000) return RoundTo(v, 100);
            if (v < 100000) return RoundTo(v, 1000);
            return RoundTo(v, 10000);
        }

        /// <summary>Convenience overload for the many <c>int</c> counts on the source models.</summary>
        public static long? Bucket(int value) => Bucket((long?)value);

        private static long RoundTo(long value, long step)
        {
            // Away-from-zero at the midpoint, matching how the rest of the analysis rounds, so a
            // consumer comparing bucketed and unbucketed figures sees consistent behaviour.
            var remainder = value % step;
            return remainder * 2 >= step ? value - remainder + step : value - remainder;
        }

        /// <summary>
        /// Rounds a percentage to the nearest half point. Rates are ratios, so they do not identify
        /// anyone on their own - and they are the only figures that compare a 200-seat tenant with a
        /// 200,000-seat one, so they keep more precision than the counts do.
        /// </summary>
        public static double? RoundPct(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return null;
            return Math.Round(value.Value * 2, MidpointRounding.AwayFromZero) / 2;
        }

        /// <summary>Rounds an adoption score to the nearest whole point.</summary>
        public static double? RoundScore(double? value)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value)) return null;
            return Math.Round(value.Value, MidpointRounding.AwayFromZero);
        }

        // ---------------------------------------------------------------------------------------
        // Label allow-lists.
        //
        // These mirror vocabularies defined in Common.Entities, which this netstandard2.0 library
        // cannot reference (it targets .NET Standard 2.0; Entities targets .NET Framework 4.8), and
        // some of which are 'internal' there in any case. They are duplicated deliberately rather
        // than shared: this copy is the PRIVACY BOUNDARY, so it must fail closed if the other side
        // changes. An unrecognised label is dropped, which loses a row - a visible, harmless failure -
        // instead of forwarding an unreviewed string.
        // ---------------------------------------------------------------------------------------

        /// <summary>Copilot adoption bands. Mirrors <c>CopilotAdoptionScoring.BandDisplayName</c>.</summary>
        public static readonly IReadOnlyCollection<string> AdoptionBands = new HashSet<string>(StringComparer.Ordinal)
        {
            "Never used", "Dormant", "Trialling", "Developing", "Established", "Champion",
        };

        /// <summary>Habit strip buckets. Mirrors <c>CopilotAdoptionScoring.AllHabitBuckets</c>.</summary>
        public static readonly IReadOnlyCollection<string> HabitBuckets = new HashSet<string>(StringComparer.Ordinal)
        {
            "Infrequent", "Moderate", "Frequent", "Daily",
        };

        /// <summary>Adoption funnel stages. Mirrors <c>CopilotAdoptionService.BuildFunnel</c>.</summary>
        public static readonly IReadOnlyCollection<string> FunnelStages = new HashSet<string>(StringComparer.Ordinal)
        {
            "Licensed", "Ever used Copilot", "Active this period", "Habitual users", "Champions",
        };

        /// <summary>Microsoft 365 workloads. Mirrors <c>LicenceActivityQuery.Workloads</c>.</summary>
        public static readonly IReadOnlyCollection<string> Workloads = new HashSet<string>(StringComparer.Ordinal)
        {
            "teams", "outlook", "onedrive", "sharepoint", "copilot",
        };

        /// <summary>Coverage statuses. Mirrors <c>LicenceActivityRules.StatusLabel</c>.</summary>
        public static readonly IReadOnlyCollection<string> CoverageStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            "available", "partial", "missingCoverage", "unmatchableIdentity", "notImported", "disabled", "unknown",
        };

        /// <summary>Coverage sources. Mirrors the <c>LicenceActivitySql.*Source</c> constants.</summary>
        public static readonly IReadOnlyCollection<string> CoverageSources = new HashSet<string>(StringComparer.Ordinal)
        {
            "microsoftGraphUsageReport", "microsoftGraphCopilotUsageReport", "copilotAudit", "copilotInteractions",
        };

        /// <summary>Coverage granularities. Mirrors <c>LicenceActivityRules.GranularityLabel</c>.</summary>
        public static readonly IReadOnlyCollection<string> CoverageGranularities = new HashSet<string>(StringComparer.Ordinal)
        {
            "weeklySupportingSnapshot", "singleRollingWindow", "weeklySampleOfRolling7DayReport",
            "eventPositiveOnly", "unknown",
        };

        /// <summary>
        /// Returns <paramref name="label"/> when it is in <paramref name="allowed"/>, otherwise null.
        /// Callers drop the row on null - failing closed is the whole point.
        /// </summary>
        public static string AllowLabel(string label, IReadOnlyCollection<string> allowed)
        {
            if (string.IsNullOrEmpty(label) || allowed == null) return null;

            // Enumerable.Contains short-circuits to ICollection<T>.Contains, so the backing HashSet
            // still answers in O(1) despite the read-only interface.
            return allowed.Contains(label) ? label : null;
        }
    }
}
