using Common.Entities.Entities.UsageReports;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// Turns the wide Copilot user-count CSVs (getMicrosoft365CopilotUserCountSummary /
    /// getMicrosoft365CopilotUserCountTrend) into narrow/tall <see cref="CopilotUserCountLog"/> rows.
    ///
    /// Apps are discovered from the header row rather than listed in code: every
    /// "&lt;app&gt; Enabled Users" / "&lt;app&gt; Active Users" pair becomes one row per date. When Microsoft
    /// adds another Copilot surface it appears as new rows with no code change and no schema migration - the
    /// whole reason for the narrow/tall shape.
    /// </summary>
    public static class CopilotUserCountReportParser
    {
        private const string EnabledUsersSuffix = " Enabled Users";
        private const string ActiveUsersSuffix = " Active Users";

        private const string ReportRefreshDateHeader = "Report Refresh Date";
        private const string ReportDateHeader = "Report Date";
        private const string ReportPeriodHeader = "Report Period";

        // Version 2 tenant-level totals. These aren't per-app, so they're carried on the "Any App" row.
        private const string TotalPromptsHeader = "Total prompts submitted";
        private const string AveragePromptsHeader = "Average prompts submitted";
        private const string TrendPromptsHeader = "Prompts submitted";

        /// <summary>
        /// Parse getMicrosoft365CopilotUserCountSummary. One roll-up per requested period; the CSV has no
        /// per-day column, so the roll-up is dated to the report's refresh date.
        /// </summary>
        public static List<CopilotUserCountLog> ParseSummary(CsvReportTable table)
        {
            return Parse(table, CopilotUserCountReportTypes.Summary);
        }

        /// <summary>
        /// Parse getMicrosoft365CopilotUserCountTrend. One row per calendar day per app.
        /// </summary>
        public static List<CopilotUserCountLog> ParseTrend(CsvReportTable table)
        {
            return Parse(table, CopilotUserCountReportTypes.Trend);
        }

        private static List<CopilotUserCountLog> Parse(CsvReportTable table, string reportType)
        {
            var results = new List<CopilotUserCountLog>();
            if (table == null || table.Rows.Count == 0) return results;

            var isTrend = reportType == CopilotUserCountReportTypes.Trend;
            var appNames = DiscoverAppNames(table.Headers);

            foreach (var row in table.Rows)
            {
                var refreshDate = row.GetDate(ReportRefreshDateHeader);
                if (!refreshDate.HasValue) continue;   // no date, nothing we can key on

                // Trend rows carry their own day; summary rows describe the window ending at the refresh date.
                var reportDate = isTrend ? row.GetDate(ReportDateHeader) : refreshDate;
                if (!reportDate.HasValue) continue;

                // The CSV writes the period as a plain number of days (7, 28, ...), not the "D28" request value.
                // Trend rows are daily and therefore period-independent - see CopilotUserCountReportTypes.Trend.
                var periodDays = isTrend ? null : row.GetInt(ReportPeriodHeader);

                foreach (var appName in appNames)
                {
                    var enabled = row.GetInt(appName + EnabledUsersSuffix);
                    var active = row.GetInt(appName + ActiveUsersSuffix);

                    // A pair where neither side has a value is a column Graph emitted but didn't populate.
                    if (!enabled.HasValue && !active.HasValue) continue;

                    var log = new CopilotUserCountLog
                    {
                        ReportRefreshDate = refreshDate.Value,
                        ReportDate = reportDate.Value,
                        ReportType = reportType,
                        ReportPeriodDays = periodDays,
                        AppName = appName,
                        EnabledUsers = enabled ?? 0,
                        ActiveUsers = active ?? 0,
                    };

                    if (appName == CopilotAppNames.AnyApp)
                    {
                        log.PromptsSubmitted = isTrend
                            ? row.GetLong(TrendPromptsHeader)
                            : row.GetLong(TotalPromptsHeader);
                        log.AveragePromptsSubmitted = isTrend ? null : row.GetDouble(AveragePromptsHeader);
                    }

                    results.Add(log);
                }
            }

            return results;
        }

        /// <summary>
        /// Every app that has an "Enabled Users" or "Active Users" column, in the order Graph listed them.
        /// </summary>
        public static IReadOnlyList<string> DiscoverAppNames(IReadOnlyList<string> headers)
        {
            var apps = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (headers == null) return apps;

            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header)) continue;

                string appName = null;
                if (header.EndsWith(EnabledUsersSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    appName = header.Substring(0, header.Length - EnabledUsersSuffix.Length);
                }
                else if (header.EndsWith(ActiveUsersSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    appName = header.Substring(0, header.Length - ActiveUsersSuffix.Length);
                }

                if (string.IsNullOrWhiteSpace(appName)) continue;

                appName = appName.Trim();
                if (seen.Add(appName)) apps.Add(appName);
            }

            return apps;
        }
    }
}
