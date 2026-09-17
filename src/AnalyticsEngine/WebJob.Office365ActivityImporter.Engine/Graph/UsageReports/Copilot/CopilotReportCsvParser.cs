using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
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
            using (var reader = new StringReader(csv ?? string.Empty))
            {
                return Parse(reportName, reader);
            }
        }

        public static List<JObject> Parse(string reportName, Stream csv)
        {
            if (csv == null) throw new ArgumentNullException(nameof(csv));
            using (var reader = new StreamReader(csv, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
            {
                return Parse(reportName, reader);
            }
        }

        public static List<JObject> Parse(string reportName, TextReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var headers = ReadRecord(reader);
            if (headers == null) return new List<JObject>();

            var rows = ReadRecords(reader).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));

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
                || property == "activeUsageDays";
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
                case "edgelastactivitydate":
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
            {
                var idx = header.LastIndexOf("Enabled", StringComparison.OrdinalIgnoreCase);
                var prefix = idx >= 0 ? header.Substring(0, idx) : key.Substring(0, key.Length - "enabledusers".Length);
                if (prefix.Length == 0) return null;
                return ToCamelProperty(prefix) + "EnabledUsers";
            }

            if (key.EndsWith("activeusers", StringComparison.Ordinal))
            {
                var idx = header.LastIndexOf("Active", StringComparison.OrdinalIgnoreCase);
                var prefix = idx >= 0 ? header.Substring(0, idx) : key.Substring(0, key.Length - "activeusers".Length);
                if (prefix.Length == 0) return null;
                return ToCamelProperty(prefix) + "ActiveUsers";
            }

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
            using (var reader = new StringReader(csv ?? string.Empty))
            {
                return ReadRecords(reader).ToList();
            }
        }

        private static IEnumerable<List<string>> ReadRecords(TextReader reader)
        {
            List<string> row;
            while ((row = ReadRecord(reader)) != null)
            {
                yield return row;
            }
        }

        private static List<string> ReadRecord(TextReader reader)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;
            var sawAnyCharacter = false;

            while (true)
            {
                var read = reader.Read();
                if (read < 0) break;

                sawAnyCharacter = true;
                var c = (char)read;
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (reader.Peek() == '"')
                        {
                            field.Append('"');
                            reader.Read();
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
                        if (c == '\r' && reader.Peek() == '\n') reader.Read();
                        row.Add(field.ToString());
                        return row;
                    }
                    else field.Append(c);
                }
            }

            if (field.Length > 0 || row.Count > 0 || sawAnyCharacter)
            {
                row.Add(field.ToString());
                return row;
            }

            return null;
        }
    }
}
