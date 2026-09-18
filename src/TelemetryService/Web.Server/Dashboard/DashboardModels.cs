namespace Web.Dashboard
{
    /// <summary>
    /// Headline figures shown at the top of the telemetry dashboard.
    /// All values are zero / empty when no clients have reported yet — the React side
    /// can render unconditionally without null checks.
    /// </summary>
    public class DashboardStats
    {
        public int ClientCount { get; set; }
        public long TotalRows { get; set; }
        public decimal TotalSpaceMB { get; set; }
        public System.DateTime? LastUpdated { get; set; }

        /// <summary>Number of distinct tables seen across every reporting client.</summary>
        public int DistinctTableCount { get; set; }

        /// <summary>Sum of <c>DataPointsFromAITotal</c> across clients that report it.</summary>
        public long AiDataPointsTotal { get; set; }

        /// <summary>How many clients reported a non-null <c>DataPointsFromAITotal</c>.</summary>
        public int ClientsReportingAi { get; set; }

        public System.Collections.Generic.List<TableTotal> TableTotals { get; set; } = new();

        /// <summary>Per-SQL-schema roll-up, so app tables (dbo) can be told apart from profiling ones.</summary>
        public System.Collections.Generic.List<SchemaTotal> SchemaTotals { get; set; } = new();

        /// <summary>Build version adoption across the install base, most common first.</summary>
        public System.Collections.Generic.List<VersionAdoption> Versions { get; set; } = new();

        /// <summary>Per-import-toggle adoption, derived from each client's settings string.</summary>
        public System.Collections.Generic.List<FeatureAdoption> ImportFeatures { get; set; } = new();

        /// <summary>How recently clients last reported in — identifies dead or stalled installs.</summary>
        public FreshnessBuckets Freshness { get; set; } = new();

        /// <summary>Deployment-size distribution, for judging how large real installs get.</summary>
        public SizeDistribution SizeDistribution { get; set; } = new();

        /// <summary>
        /// Copilot and licence adoption across the install base. Always present; its counts are zero
        /// when nobody has reported an adoption block yet.
        /// </summary>
        public AdoptionInsights Adoption { get; set; } = new();
    }

    /// <summary>
    /// Cross-client roll-up of the anonymised adoption blocks.
    /// </summary>
    /// <remarks>
    /// Most clients will not have one - older builds never send it, and a deployment that does not
    /// import user metadata plus a Copilot signal cannot produce it - so every figure here is over
    /// <see cref="ClientsReporting"/>, not over the whole install base. Presenting these as shares of
    /// all clients would make adoption look far worse than it is.
    ///
    /// The underlying counts are bucketed client-side (see <c>AnonStatsPrivacy</c>), so sums and
    /// medians here are approximate by construction. That is fine for "how is Copilot doing in the
    /// wild" and wrong for anything that needs exactness.
    /// </remarks>
    public class AdoptionInsights
    {
        /// <summary>Clients that sent an adoption block at all.</summary>
        public int ClientsReporting { get; set; }

        /// <summary>Clients whose block was suppressed because the tenant was too small to report.</summary>
        public int ClientsSuppressed { get; set; }

        /// <summary>Clients reporting actual Copilot figures (reporting, and not suppressed).</summary>
        public int ClientsWithCopilotFigures { get; set; }

        /// <summary>Summed bucketed Copilot seats across reporting clients.</summary>
        public long TotalLicensedUsers { get; set; }

        /// <summary>Summed bucketed active Copilot users across reporting clients.</summary>
        public long TotalActiveUsers { get; set; }

        public long MedianLicensedUsersPerClient { get; set; }

        public long MedianActiveUsersPerClient { get; set; }

        /// <summary>Median of each client's own adoption rate - not a rate computed from the totals.</summary>
        public double MedianAdoptionRatePct { get; set; }

        public double LowerQuartileAdoptionRatePct { get; set; }

        public double UpperQuartileAdoptionRatePct { get; set; }

        /// <summary>Median of each client's habitual-user rate.</summary>
        public double MedianHabitRatePct { get; set; }

        /// <summary>How many reporting clients have any custom agents at all.</summary>
        public int ClientsWithCustomAgents { get; set; }

        /// <summary>How stale the adoption blocks are. They are recomputed weekly, so "last 7 days" is the healthy state.</summary>
        public FreshnessBuckets Freshness { get; set; } = new();

        /// <summary>Which Copilot data sources clients actually have, most common first.</summary>
        public List<FeatureAdoption> DataSources { get; set; } = new();

        /// <summary>SKU popularity across the install base, most widely held first.</summary>
        public List<SkuPopularity> Skus { get; set; } = new();

        /// <summary>Whether each workload could actually be measured, by status.</summary>
        public List<CoverageStatusTotal> Coverage { get; set; } = new();
    }

    /// <summary>How many clients report a given SKU, and how many seats they hold between them.</summary>
    public class SkuPopularity
    {
        public string SkuPartNumber { get; set; } = string.Empty;

        /// <summary>Clients reporting this SKU.</summary>
        public int ClientCount { get; set; }

        /// <summary>Summed bucketed assignments. Licence ASSIGNMENTS, not distinct people.</summary>
        public long AssignedUsers { get; set; }
    }

    /// <summary>Per-workload coverage outcome across the install base.</summary>
    public class CoverageStatusTotal
    {
        public string Workload { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int ClientCount { get; set; }
    }

    public class TableTotal
    {
        public string TableName { get; set; } = string.Empty;

        /// <summary>Owning SQL schema. Null on reports from clients older than this field.</summary>
        public string? SchemaName { get; set; }

        /// <summary>Schema-qualified name when the schema is known, otherwise the bare table name.</summary>
        public string DisplayName { get; set; } = string.Empty;

        public long Rows { get; set; }
        public decimal TotalSpaceMB { get; set; }
        public int ClientCount { get; set; }
    }

    public class SchemaTotal
    {
        /// <summary>Schema name, or "(unknown)" for clients predating the SchemaName field.</summary>
        public string SchemaName { get; set; } = string.Empty;
        public long Rows { get; set; }
        public decimal TotalSpaceMB { get; set; }
        public int TableCount { get; set; }
    }

    public class VersionAdoption
    {
        /// <summary>Reported build label, or "(unknown)" when the client did not send one.</summary>
        public string BuildVersionLabel { get; set; } = string.Empty;
        public int ClientCount { get; set; }
        public System.DateTime? LastSeen { get; set; }
    }

    /// <summary>
    /// Adoption of a single import toggle across the install base. Derived by parsing each client's
    /// <c>ConfiguredImportsEnabledDescription</c>, which is a <c>Name=True;Name=False</c> string.
    /// </summary>
    public class FeatureAdoption
    {
        public string Name { get; set; } = string.Empty;
        public int EnabledCount { get; set; }
        public int DisabledCount { get; set; }

        /// <summary>Clients that reported this toggle at all (enabled + disabled).</summary>
        public int ReportingClients => EnabledCount + DisabledCount;
    }

    public class FreshnessBuckets
    {
        public int Last24Hours { get; set; }
        public int Last7Days { get; set; }
        public int Last30Days { get; set; }

        /// <summary>Reported more than 30 days ago, or never.</summary>
        public int Stale { get; set; }
    }

    /// <summary>
    /// Row-count and size distribution across clients. Averages alone hide the shape of the
    /// install base, so the median and maximum are reported too.
    /// </summary>
    public class SizeDistribution
    {
        public long AvgRowsPerClient { get; set; }
        public long MedianRowsPerClient { get; set; }
        public long MaxRowsPerClient { get; set; }

        public decimal AvgSpaceMBPerClient { get; set; }
        public decimal MedianSpaceMBPerClient { get; set; }
        public decimal MaxSpaceMBPerClient { get; set; }

        public int AvgTablesPerClient { get; set; }
    }

    /// <summary>
    /// Per-client summary row for the dashboard's "reporting clients" table.
    /// </summary>
    public class ClientSummary
    {
        public string AnonClientId { get; set; } = string.Empty;
        public System.DateTime? Generated { get; set; }
        public string? BuildVersionLabel { get; set; }
        public string? ConfiguredImportsEnabledDescription { get; set; }
        public string? ConfiguredSolutionsEnabledDescription { get; set; }
        public int? DataPointsFromAITotal { get; set; }
        public long Rows { get; set; }
        public decimal TotalSpaceMB { get; set; }
        public int TableCount { get; set; }

        /// <summary>The import toggles this client has switched on, already parsed for display.</summary>
        public System.Collections.Generic.List<string> EnabledImports { get; set; } = new();

        /// <summary>
        /// Maintainer-entered name for this client, when someone has recorded who it is. Null means the
        /// client is still just an anonymous id - which is the default and the norm.
        /// </summary>
        public string? AnnotationDisplayName { get; set; }

        /// <summary>Maintainer-entered note, if any.</summary>
        public string? AnnotationNotes { get; set; }

        /// <summary>When the adoption block was computed. Up to a week older than <see cref="Generated"/>.</summary>
        public System.DateTime? AdoptionGeneratedUtc { get; set; }

        /// <summary>Null when the client sent no adoption block, or its block was suppressed.</summary>
        public long? CopilotLicensedUsers { get; set; }

        public long? CopilotActiveUsers { get; set; }

        public double? CopilotAdoptionRatePct { get; set; }

        /// <summary>True when the client reported, but its tenant was too small to publish figures.</summary>
        public bool AdoptionSuppressed { get; set; }
    }
}
