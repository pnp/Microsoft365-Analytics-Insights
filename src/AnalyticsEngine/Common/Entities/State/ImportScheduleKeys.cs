namespace Common.Entities.State
{
    /// <summary>
    /// Row keys the importer writes to the <see cref="StatePartitions.ImportSchedule"/> partition when an import
    /// completes. Defined here, not in the importer, so the portal's per-user diagnostics read exactly the keys the
    /// importer writes; a renamed key would otherwise read as "never completed" without failing anywhere.
    /// </summary>
    /// <remarks>
    /// Each value is a round-trip ("o") timestamp of the last <i>successful</i> run of that section, tenant-wide. It
    /// says the section finished, not that any particular person was processed by it.
    /// </remarks>
    public static class ImportScheduleKeys
    {
        /// <summary>Graph user metadata and licence refresh.</summary>
        public const string GraphUsersMetadata = UserImportCheckpointKeys.LastCompleted;

        public const string GraphTeams = "GraphTeamsLastImported";

        public const string GraphCopilotUsageReports = "GraphCopilotUsageReportsLastImported";

        public const string CopilotInteractionHistory = "CopilotInteractionHistoryLastImported";

        /// <summary>The once-a-day Microsoft 365 usage-report phase as a whole.</summary>
        public const string UsageReportsPhase = "UserActivityLastImported";

        /// <summary>
        /// Prefix of each usage report's own completion stamp; the rest of the key is the importer's loader type
        /// name (for example <c>TeamsUserUsageLoader</c>).
        /// </summary>
        public const string UsageReportPrefix = "UserActivityReportLastImported:";

        /// <summary>The completion key for one usage report loader.</summary>
        public static string UsageReport(string loaderTypeName) => UsageReportPrefix + loaderTypeName;
    }
}
