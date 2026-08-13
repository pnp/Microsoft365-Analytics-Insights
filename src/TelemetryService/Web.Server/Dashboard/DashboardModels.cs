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

        public System.Collections.Generic.List<TableTotal> TableTotals { get; set; } = new();
    }

    public class TableTotal
    {
        public string TableName { get; set; } = string.Empty;
        public long Rows { get; set; }
        public decimal TotalSpaceMB { get; set; }
        public int ClientCount { get; set; }
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
    }
}
