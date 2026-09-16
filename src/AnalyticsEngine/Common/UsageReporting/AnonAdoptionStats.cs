using System;
using System.Collections.Generic;

namespace UsageReporting
{
    /// <summary>
    /// Anonymised Copilot- and licence-adoption aggregates, uploaded alongside the rest of
    /// <see cref="AnonUsageStatsModel"/> when the operator has opted in to telemetry.
    ///
    /// Everything here is a tenant-wide aggregate or a Microsoft-global SKU identifier. No department,
    /// country, agent, user or free-text value from the customer's tenant reaches this type - see
    /// <see cref="AnonStatsPrivacy"/> for the rules and the reasoning, and the mapper that builds it
    /// for where each hazard is dropped.
    ///
    /// Added after the original payload shipped, so every member is nullable or defaulted: an older
    /// server simply ignores the block, and an older importer never sends one.
    /// </summary>
    public class AnonAdoptionStats
    {
        /// <summary>
        /// When the underlying analysis actually ran.
        ///
        /// Deliberately separate from <see cref="AnonUsageStatsModel.Generated"/>: the analysis is
        /// expensive, so it runs weekly while the payload uploads daily, and the daily payload re-sends
        /// the last computed block. Without this, a consumer would read a week-old figure as today's,
        /// and the history container would look like seven genuine data points instead of one.
        /// </summary>
        public DateTime? GeneratedUtc { get; set; }

        /// <summary>
        /// Reporting window the figures cover, in days. Fixed by the collector rather than taken from
        /// whatever the operator last chose in the UI, so figures are comparable between clients.
        /// </summary>
        public int? WindowDays { get; set; }

        /// <summary>
        /// True when the tenant was too small to report adoption figures without making them
        /// identifying. <see cref="Copilot"/> is null in that case; <see cref="Licences"/> may still
        /// carry SKU rows that individually cleared their own threshold.
        /// </summary>
        public bool Suppressed { get; set; }

        /// <summary>
        /// Why the block was suppressed, from a fixed vocabulary (see
        /// <see cref="AnonStatsPrivacy.SuppressionReasons"/>). Never free text, and never an exception
        /// message.
        /// </summary>
        public string SuppressionReason { get; set; }

        public AnonCopilotAdoptionStats Copilot { get; set; }

        public AnonLicenceAdoptionStats Licences { get; set; }

        public override string ToString()
        {
            if (Suppressed)
            {
                return $"AnonAdoptionStats: suppressed ({SuppressionReason}), generated {GeneratedUtc}";
            }

            return $"AnonAdoptionStats: generated {GeneratedUtc}, window {WindowDays}d, "
                   + $"copilot {(Copilot == null ? "none" : "present")}, "
                   + $"licences {(Licences == null ? "none" : Licences.Skus?.Count.ToString() ?? "0")} sku(s)";
        }
    }

    /// <summary>
    /// Tenant-wide Copilot adoption aggregates. All counts are bucketed by
    /// <see cref="AnonStatsPrivacy.Bucket(long?)"/>; rates and scores are rounded.
    /// </summary>
    public class AnonCopilotAdoptionStats
    {
        // ---- Which inputs the tenant actually has. Booleans about the DEPLOYMENT, not about people,
        // so they are reported exactly - they are the context that makes every figure below readable.

        public bool AuditAvailable { get; set; }
        public bool CopilotUsageReportAvailable { get; set; }
        public bool M365UsageReportsAvailable { get; set; }
        public bool UserMetadataAvailable { get; set; }

        /// <summary>
        /// True when Microsoft's usage report was returned with user names concealed, which changes how
        /// much of the analysis can be attributed to individuals.
        /// </summary>
        public bool CopilotUsageReportObfuscated { get; set; }

        // ---- Population. Bucketed.

        public long? LicensedUsers { get; set; }
        public long? ScoredUsers { get; set; }
        public long? ActiveUsers { get; set; }
        public long? HabitualUsers { get; set; }
        public long? NeverUsedUsers { get; set; }
        public long? DormantUsers { get; set; }
        public long? DisabledLicensedUsers { get; set; }
        public long? ReclaimableSeats { get; set; }
        public long? UnlicensedActiveUsers { get; set; }
        public long? RecommendedForLicence { get; set; }
        public long? CoworkUsers { get; set; }

        // ---- Volume. Bucketed.

        public long? TotalInteractions { get; set; }
        public long? CoworkInteractions { get; set; }
        public long? AgentInteractions { get; set; }

        // ---- Agent estate. Counts only: agent NAMES are tenant-authored and never sent.

        public long? KnownAgents { get; set; }
        public long? ActiveAgents { get; set; }
        public long? CustomAgents { get; set; }
        public long? AgentUsers { get; set; }

        // ---- Derived rates. Rounded, not bucketed: they are ratios, so they do not identify anyone
        // on their own, and bucketing them would destroy the only figures that compare across tenants
        // of different sizes.

        public double? AdoptionRatePct { get; set; }
        public double? HabitRatePct { get; set; }
        public double? CoworkAdoptionPct { get; set; }
        public double? AverageAdoptionScore { get; set; }
        public double? MedianAdoptionScore { get; set; }

        // ---- Distribution shapes. Labels come from a fixed vocabulary; anything unrecognised is
        // dropped rather than passed through, so a future label carrying tenant text cannot leak.

        public List<AnonAdoptionCategory> BandBreakdown { get; set; } = new List<AnonAdoptionCategory>();
        public List<AnonAdoptionCategory> Funnel { get; set; } = new List<AnonAdoptionCategory>();
        public List<AnonAdoptionCategory> HabitBuckets { get; set; } = new List<AnonAdoptionCategory>();

        // ---- Quality flags, so a consumer can tell a real zero from a failed query.

        public bool FiguresIncomplete { get; set; }
        public bool UsageReportWindowMismatch { get; set; }

        /// <summary>
        /// How many warnings the analysis raised.
        ///
        /// A COUNT, never the strings: the analysis builds warnings as
        /// <c>"Could not load {description}: {InnermostMessage(ex)}"</c>, so a SQL failure embeds
        /// database and object names straight into the text.
        /// </summary>
        public int WarningCount { get; set; }
    }

    /// <summary>
    /// A single labelled count in a distribution. The label is always from a fixed, code-defined
    /// vocabulary - never a value read from the customer's database.
    /// </summary>
    public class AnonAdoptionCategory
    {
        public string Label { get; set; }

        public long? Value { get; set; }

        public override string ToString() => $"{Label}: {Value}";
    }

    /// <summary>
    /// Licence adoption across the tenant, per SKU. SKU part numbers are Microsoft-global identifiers
    /// (<c>ENTERPRISEPACK</c> and friends), not tenant data - but only those Microsoft actually
    /// publishes are sent, because an unrecognised one can be a reseller-specific string.
    /// </summary>
    public class AnonLicenceAdoptionStats
    {
        /// <summary>Distinct users holding at least one licence. Bucketed.</summary>
        public long? DistinctAssignedUsers { get; set; }

        public List<AnonLicenceSkuStats> Skus { get; set; } = new List<AnonLicenceSkuStats>();

        public List<AnonLicenceCoverage> Coverage { get; set; } = new List<AnonLicenceCoverage>();
    }

    public class AnonLicenceSkuStats
    {
        /// <summary>
        /// Microsoft's published SKU part number, or <see cref="AnonStatsPrivacy.UnlistedSku"/> when it
        /// is not one Microsoft publishes, or <see cref="AnonStatsPrivacy.OtherSkuBucket"/> for the
        /// roll-up of SKUs too small to report individually. The SKU DISPLAY NAME is never sent: it
        /// falls back to the raw part number for unknown SKUs, so it adds nothing but risk.
        /// </summary>
        public string SkuPartNumber { get; set; }

        /// <summary>Users holding this SKU. Bucketed.</summary>
        public long? AssignedUsers { get; set; }

        /// <summary>
        /// How many SKUs were folded together when this is the <c>(other)</c> roll-up row. Null
        /// otherwise.
        /// </summary>
        public int? RolledUpSkuCount { get; set; }

        public List<AnonWorkloadDistribution> Workloads { get; set; } = new List<AnonWorkloadDistribution>();
    }

    /// <summary>
    /// How the holders of a SKU split across activity levels for one Microsoft 365 workload. All
    /// counts bucketed.
    /// </summary>
    public class AnonWorkloadDistribution
    {
        /// <summary>Fixed vocabulary: teams, outlook, onedrive, sharepoint, copilot.</summary>
        public string Workload { get; set; }

        public long? High { get; set; }
        public long? Moderate { get; set; }
        public long? Low { get; set; }
        public long? Zero { get; set; }
        public long? Unknown { get; set; }
    }

    /// <summary>
    /// Whether a workload could be measured at all, and from which source. This is the most useful
    /// part of the payload for the project team: it says which imports are actually producing usable
    /// data in the wild, as opposed to merely being switched on.
    /// </summary>
    public class AnonLicenceCoverage
    {
        /// <summary>Fixed vocabulary. See <see cref="AnonStatsPrivacy.Workloads"/>.</summary>
        public string Workload { get; set; }

        /// <summary>Fixed vocabulary. See <see cref="AnonStatsPrivacy.CoverageStatuses"/>.</summary>
        public string Status { get; set; }

        /// <summary>Fixed vocabulary of Graph/source identifiers. See <see cref="AnonStatsPrivacy.CoverageSources"/>.</summary>
        public string Source { get; set; }

        /// <summary>Fixed vocabulary. See <see cref="AnonStatsPrivacy.CoverageGranularities"/>.</summary>
        public string Granularity { get; set; }

        /// <summary>Import lag in days. A property of the source, not of any person, so reported exactly.</summary>
        public int LagDays { get; set; }

        public int? ReportPeriodDays { get; set; }

        /// <summary>
        /// Sampling completeness. Counts of READINGS, not of people - so they are reported exactly;
        /// the ratio between them is what says whether coverage was whole.
        /// </summary>
        public int ExpectedSamples { get; set; }

        public int ObservedSamples { get; set; }

        /// <summary>Licensed users the source could not be matched to. A count of people, so bucketed.</summary>
        public long? UnmatchedUsers { get; set; }
    }
}
