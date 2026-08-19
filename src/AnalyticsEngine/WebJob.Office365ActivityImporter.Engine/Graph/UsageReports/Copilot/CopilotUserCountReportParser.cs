using Common.Entities.Entities.UsageReports;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// Turns the Copilot user-count reports (getMicrosoft365CopilotUserCountSummary /
    /// getMicrosoft365CopilotUserCountTrend) into narrow/tall <see cref="CopilotUserCountLog"/> rows.
    ///
    /// Both reports nest their numbers one level down - summary under <c>adoptionByProduct</c> (one entry per
    /// requested period), trend under <c>adoptionByDate</c> (one entry per day) - and each entry carries a
    /// property pair per Copilot surface: <c>wordEnabledUsers</c> / <c>wordActiveUsers</c>.
    ///
    /// Apps are discovered from those property pairs rather than listed in code, so when Microsoft adds
    /// another Copilot surface it appears as new rows with no code change and no schema migration. That is
    /// the whole reason for the narrow/tall shape.
    /// </summary>
    public static class CopilotUserCountReportParser
    {
        private const string EnabledUsersSuffix = "EnabledUsers";
        private const string ActiveUsersSuffix = "ActiveUsers";

        private const string ReportRefreshDateProperty = "reportRefreshDate";
        private const string ReportPeriodProperty = "reportPeriod";
        private const string ReportDateProperty = "reportDate";
        private const string AdoptionByProductProperty = "adoptionByProduct";
        private const string AdoptionByDateProperty = "adoptionByDate";

        // Tenant-level totals (report version 2). Not per app, so they are carried on the "Any App" row.
        // Several spellings are accepted because Microsoft has not published the beta JSON schema for
        // version 2; an absent property simply leaves the value null.
        private static readonly string[] TotalPromptsProperties = { "totalPromptsSubmitted", "promptsSubmitted" };
        private static readonly string[] AveragePromptsProperties = { "averagePromptsSubmitted" };

        /// <summary>
        /// Canonical display names for the app property prefixes, so stored values match what the Microsoft
        /// 365 admin centre calls them. A prefix that isn't listed still imports - it just gets a name derived
        /// from the property, which is what keeps a brand-new Microsoft app working with no code change.
        /// </summary>
        private static readonly Dictionary<string, string> KnownAppNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "anyApp", CopilotAppNames.AnyApp },
            { "microsoftTeams", "Microsoft Teams" },
            { "word", "Word" },
            { "excel", "Excel" },
            { "powerPoint", "PowerPoint" },
            { "outlook", "Outlook" },
            { "oneNote", "OneNote" },
            { "loop", "Loop" },
            { "copilotChat", "Copilot Chat" },
            { "edge", "Edge" },
            { "microsoft365Copilot", "Microsoft 365 Copilot" },
            { "copilotChatWork", "Copilot Chat (work)" },
            { "copilotChatWeb", "Copilot Chat (web)" },
        };

        /// <summary>
        /// Parse getMicrosoft365CopilotUserCountSummary. One roll-up per requested period; there is no
        /// per-day value, so the roll-up is dated to the report's refresh date.
        /// </summary>
        public static List<CopilotUserCountLog> ParseSummary(IEnumerable<JObject> reports)
        {
            return Parse(reports, CopilotUserCountReportTypes.Summary);
        }

        /// <summary>Parse getMicrosoft365CopilotUserCountTrend. One row per calendar day per app.</summary>
        public static List<CopilotUserCountLog> ParseTrend(IEnumerable<JObject> reports)
        {
            return Parse(reports, CopilotUserCountReportTypes.Trend);
        }

        private static List<CopilotUserCountLog> Parse(IEnumerable<JObject> reports, string reportType)
        {
            var results = new List<CopilotUserCountLog>();
            if (reports == null) return results;

            var isTrend = reportType == CopilotUserCountReportTypes.Trend;

            foreach (var report in reports)
            {
                if (report == null) continue;

                var refreshDate = GetDate(report, ReportRefreshDateProperty);
                if (!refreshDate.HasValue) continue;   // nothing we can key on

                // The trend report states its period once, at the top; the summary states it per entry.
                var reportLevelPeriod = GetInt(report, ReportPeriodProperty);

                var entries = report[isTrend ? AdoptionByDateProperty : AdoptionByProductProperty] as JArray;
                if (entries == null) continue;

                foreach (var entry in entries.OfType<JObject>())
                {
                    // Trend rows carry their own day; summary rows describe the window ending at the refresh date.
                    var reportDate = isTrend ? GetDate(entry, ReportDateProperty) : refreshDate;
                    if (!reportDate.HasValue) continue;

                    // Trend rows are daily and therefore period-independent - see CopilotUserCountReportTypes.Trend.
                    var periodDays = isTrend ? null : (GetInt(entry, ReportPeriodProperty) ?? reportLevelPeriod);

                    foreach (var app in DiscoverApps(entry))
                    {
                        var enabled = GetInt(entry, app.EnabledProperty);
                        var active = GetInt(entry, app.ActiveProperty);

                        // A pair where neither side has a value is a property Graph emitted but didn't populate.
                        if (!enabled.HasValue && !active.HasValue) continue;

                        var log = new CopilotUserCountLog
                        {
                            ReportRefreshDate = refreshDate.Value,
                            ReportDate = reportDate.Value,
                            ReportType = reportType,
                            ReportPeriodDays = periodDays,
                            AppName = app.DisplayName,
                            EnabledUsers = enabled ?? 0,
                            ActiveUsers = active ?? 0,
                        };

                        if (log.AppName == CopilotAppNames.AnyApp)
                        {
                            log.PromptsSubmitted = GetLong(entry, TotalPromptsProperties);
                            log.AveragePromptsSubmitted = isTrend ? null : GetDouble(entry, AveragePromptsProperties);
                        }

                        results.Add(log);
                    }
                }
            }

            return results;
        }

        /// <summary>An app discovered from a report entry's property names.</summary>
        public class DiscoveredApp
        {
            public string DisplayName { get; set; }
            public string EnabledProperty { get; set; }
            public string ActiveProperty { get; set; }
        }

        /// <summary>
        /// Every app that has an enabled-users or active-users property, in the order Graph listed them.
        /// </summary>
        public static IReadOnlyList<DiscoveredApp> DiscoverApps(JObject entry)
        {
            var apps = new List<DiscoveredApp>();
            if (entry == null) return apps;

            var byPrefix = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in entry.Properties())
            {
                var name = property.Name;

                string prefix = null;
                var isEnabled = false;
                if (name.EndsWith(EnabledUsersSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    prefix = name.Substring(0, name.Length - EnabledUsersSuffix.Length);
                    isEnabled = true;
                }
                else if (name.EndsWith(ActiveUsersSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    prefix = name.Substring(0, name.Length - ActiveUsersSuffix.Length);
                }

                if (string.IsNullOrWhiteSpace(prefix)) continue;

                if (!byPrefix.TryGetValue(prefix, out var app))
                {
                    app = new DiscoveredApp { DisplayName = DisplayNameFor(prefix) };
                    byPrefix[prefix] = app;
                    apps.Add(app);
                }

                if (isEnabled) app.EnabledProperty = name;
                else app.ActiveProperty = name;
            }

            return apps;
        }

        /// <summary>
        /// Maps a property prefix to the name the admin centre uses. Unknown prefixes fall back to splitting
        /// the camel case, so a new Microsoft app still imports with a readable name rather than being
        /// dropped - "brandNewApp" becomes "Brand New App".
        /// </summary>
        public static string DisplayNameFor(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix)) return prefix;
            if (KnownAppNames.TryGetValue(prefix, out var known)) return known;

            var builder = new StringBuilder(prefix.Length + 8);
            for (var i = 0; i < prefix.Length; i++)
            {
                var c = prefix[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(prefix[i - 1]))
                {
                    builder.Append(' ');
                }
                builder.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
            return builder.ToString();
        }

        #region JSON value helpers - every one returns null rather than throwing on an unexpected value

        internal static DateTime? GetDate(JObject source, string property)
        {
            var raw = source?[property]?.Type == JTokenType.Date
                ? source[property].Value<DateTime>().Date
                : (DateTime?)null;
            if (raw.HasValue) return raw;

            var text = source?[property]?.Value<string>();
            if (string.IsNullOrWhiteSpace(text)) return null;

            return DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.Date
                : (DateTime?)null;
        }

        internal static int? GetInt(JObject source, string property)
        {
            if (property == null) return null;
            var token = source?[property];
            if (token == null || token.Type == JTokenType.Null) return null;

            return int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        }

        internal static long? GetLong(JObject source, string[] properties)
        {
            foreach (var property in properties)
            {
                var token = source?[property];
                if (token == null || token.Type == JTokenType.Null) continue;

                if (long.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
            return null;
        }

        internal static double? GetDouble(JObject source, string[] properties)
        {
            foreach (var property in properties)
            {
                var token = source?[property];
                if (token == null || token.Type == JTokenType.Null) continue;

                if (double.TryParse(token.Value<string>(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
            return null;
        }

        #endregion
    }
}
