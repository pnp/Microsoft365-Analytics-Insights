using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// Converts the GA v1.0 /copilot report CSV streams into the same JSON-shaped objects the original beta
    /// parser consumed. Keeping that boundary stable preserves the existing persistence and validation logic
    /// while moving the transport to the supported endpoint.
    /// </summary>
    public static class CopilotReportCsvParser
    {
        public static List<JObject> Parse(string reportName, string csv)
        {
            var records = ReadRecords(csv);
            if (records.Count < 2) return new List<JObject>();

            var headers = records[0];
            var rows = records.Skip(1).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();

            if (reportName == CopilotReportNames.UsageUserDetail) return ParseUserDetail(headers, rows);
            if (reportName == CopilotReportNames.UserCountSummary) return ParseSummary(headers, rows);
            if (reportName == CopilotReportNames.UserCountTrend) return ParseTrend(headers, rows);

            throw new ArgumentOutOfRangeException(nameof(reportName), reportName, "Unknown Copilot report name.");
        }

        private static List<JObject> ParseUserDetail(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            var result = new List<JObject>();
            foreach (var fields in rows)
            {
                var root = new JObject();
                var period = new JObject();

                for (var i = 0; i < headers.Count; i++)
                {
                    var value = Field(fields, i);
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    var property = UserDetailProperty(headers[i]);
                    if (property == null) continue;

                    if (IsUserDetailPeriodProperty(property)) period[property] = value;
                    else root[property] = value;
                }

                if (period.HasValues) root["copilotActivityUserDetailsByPeriod"] = new JArray(period);
                result.Add(root);
            }
            return result;
        }

        private static List<JObject> ParseSummary(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            var result = new List<JObject>();
            foreach (var fields in rows)
            {
                var root = new JObject();
                var entry = new JObject();
                for (var i = 0; i < headers.Count; i++)
                {
                    var value = Field(fields, i);
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    var property = CountReportProperty(headers[i]);
                    if (property == null) continue;

                    if (property == "reportRefreshDate") root[property] = value;
                    else entry[property] = value;
                }
                root["adoptionByProduct"] = new JArray(entry);
                result.Add(root);
            }
            return result;
        }

        private static List<JObject> ParseTrend(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
        {
            var result = new List<JObject>();
            foreach (var fields in rows)
            {
                var root = new JObject();
                var entry = new JObject();
                for (var i = 0; i < headers.Count; i++)
                {
                    var value = Field(fields, i);
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    var property = CountReportProperty(headers[i]);
                    if (property == null) continue;

                    if (property == "reportRefreshDate" || property == "reportPeriod") root[property] = value;
                    else entry[property] = value;
                }
                root["adoptionByDate"] = new JArray(entry);
                result.Add(root);
            }
            return result;
        }

        private static bool IsUserDetailPeriodProperty(string property)
        {
            return property == "reportPeriod"
                || property == "promptsSubmitted"
                || property == "promptsSubmittedForCopilotChatWork"
                || property == "promptsSubmittedForCopilotChatWeb"
                || property == "activeUsageDays"
                || property == "appsUsed";
        }

        private static string UserDetailProperty(string header)
        {
            var key = Key(header);
            switch (key)
            {
                case "reportrefreshdate": return "reportRefreshDate";
                case "reportperiod": return "reportPeriod";
                case "userprincipalname": return "userPrincipalName";
                case "displayname": return "displayName";
                case "lastactivitydate":
                case "lastactivitydateutcuniversaltimecode":
                case "lastactivitydateutc": return "lastActivityDate";
                case "microsoftteamscopilotlastactivitydate":
                case "lastactivitydateofteamscopilotutc": return "microsoftTeamsCopilotLastActivityDate";
                case "wordcopilotlastactivitydate":
                case "lastactivitydateofwordcopilotutc": return "wordCopilotLastActivityDate";
                case "excelcopilotlastactivitydate":
                case "lastactivitydateofexcelcopilotutc": return "excelCopilotLastActivityDate";
                case "powerpointcopilotlastactivitydate":
                case "lastactivitydateofpowerpointcopilotutc": return "powerPointCopilotLastActivityDate";
                case "outlookcopilotlastactivitydate":
                case "lastactivitydateofoutlookcopilotutc": return "outlookCopilotLastActivityDate";
                case "onenotecopilotlastactivitydate":
                case "lastactivitydateofonenotecopilotutc": return "oneNoteCopilotLastActivityDate";
                case "loopcopilotlastactivitydate":
                case "lastactivitydateofloopcopilotutc": return "loopCopilotLastActivityDate";
                case "copilotchatlastactivitydate": return "copilotChatLastActivityDate";
                case "copilotchatworklastactivitydate":
                case "lastactivitydateofcopilotchatworkutc": return "copilotChatWorkLastActivityDate";
                case "copilotchatweblastactivitydate":
                case "lastactivitydateofcopilotchatwebutc": return "copilotChatWebLastActivityDate";
                case "microsoft365copilotlastactivitydate":
                case "lastactivitydateofmicrosoft365copilotutc":
                case "lastactivitydateofmicrosoft365apputc": return "microsoft365CopilotLastActivityDate";
                case "edgecopilotlastactivitydate":
                case "microsoftedgelastactivitydate":
                case "lastactivitydateofmicrosoftedgeutc": return "edgeLastActivityDate";
                case "copilotagentlastactivitydate":
                case "anyagentlastactivitydate":
                case "lastactivitydateofanyagentutc": return "copilotAgentLastActivityDate";
                case "promptssubmittedanyapp":
                case "promptssubmittedforallapps": return "promptsSubmitted";
                case "copilotchatworkpromptssubmitted":
                case "promptssubmittedforcopilotchatwork": return "promptsSubmittedForCopilotChatWork";
                case "copilotchatwebpromptssubmitted":
                case "promptssubmittedforcopilotchatweb": return "promptsSubmittedForCopilotChatWeb";
                case "activedays":
                case "activeusagedays":
                case "activeusagedaysforallapps": return "activeUsageDays";
                case "appsused":
                case "reportappsused":
                case "activeapps": return "appsUsed";
                default: return null;
            }
        }

        private static string CountReportProperty(string header)
        {
            var key = Key(header);
            switch (key)
            {
                case "reportrefreshdate": return "reportRefreshDate";
                case "reportdate": return "reportDate";
                case "reportperiod": return "reportPeriod";
                case "totalpromptssubmitted": return "totalPromptsSubmitted";
                case "averagepromptssubmitted": return "averagePromptsSubmitted";
                case "promptssubmitted": return "promptsSubmitted";
            }

            if (key.EndsWith("enabledusers", StringComparison.Ordinal))
                return ToCamelProperty(header.Substring(0, header.LastIndexOf("Enabled Users", StringComparison.OrdinalIgnoreCase))) + "EnabledUsers";
            if (key.EndsWith("activeusers", StringComparison.Ordinal))
                return ToCamelProperty(header.Substring(0, header.LastIndexOf("Active Users", StringComparison.OrdinalIgnoreCase))) + "ActiveUsers";

            return null;
        }

        private static string ToCamelProperty(string displayName)
        {
            var tokens = Tokens(displayName).ToList();
            if (tokens.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                builder.Append(i == 0 ? char.ToLowerInvariant(t[0]) + t.Substring(1) : char.ToUpperInvariant(t[0]) + t.Substring(1));
            }
            return builder.ToString();
        }

        private static IEnumerable<string> Tokens(string value)
        {
            var token = new StringBuilder();
            foreach (var c in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c)) token.Append(c);
                else if (token.Length > 0)
                {
                    yield return token.ToString();
                    token.Clear();
                }
            }
            if (token.Length > 0) yield return token.ToString();
        }

        private static string Key(string value)
        {
            var builder = new StringBuilder();
            foreach (var c in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }

        private static string Field(IReadOnlyList<string> fields, int index)
        {
            return index < fields.Count ? fields[index]?.Trim() : null;
        }

        internal static List<List<string>> ReadRecords(string csv)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < (csv ?? string.Empty).Length; i++)
            {
                var c = csv[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < csv.Length && csv[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',')
                    {
                        row.Add(field.ToString());
                        field.Clear();
                    }
                    else if (c == '\r' || c == '\n')
                    {
                        if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                        row.Add(field.ToString());
                        field.Clear();
                        rows.Add(row);
                        row = new List<string>();
                    }
                    else field.Append(c);
                }
            }

            if (field.Length > 0 || row.Count > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }

            return rows;
        }
    }
}
