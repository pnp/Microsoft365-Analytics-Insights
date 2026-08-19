using CsvHelper;
using CsvHelper.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot
{
    /// <summary>
    /// A parsed CSV report: the header row plus each data row keyed by header name.
    ///
    /// Deliberately header-driven rather than mapped onto a fixed class. The Copilot user-count reports use a
    /// column pair per Copilot surface ("Word Enabled Users" / "Word Active Users"), and Microsoft keeps
    /// adding surfaces - Edge, Microsoft 365 Copilot and Copilot Chat work/web all arrived with report
    /// version 2. Reading columns by name means a new Microsoft app turns into new rows in a narrow/tall
    /// table instead of a schema migration on every customer database, and means a version 1 response (which
    /// simply lacks the version 2 columns) parses without error.
    /// </summary>
    public class CsvReportTable
    {
        private CsvReportTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
        {
            Headers = headers;
            Rows = rows;
        }

        public IReadOnlyList<string> Headers { get; }

        public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; }

        public static CsvReportTable Empty { get; } =
            new CsvReportTable(new List<string>(), new List<IReadOnlyDictionary<string, string>>());

        /// <summary>
        /// Parse a CSV payload. Header matching is case-insensitive and tolerant of the surrounding
        /// whitespace and UTF-8 byte-order mark that Graph's report streams carry.
        /// </summary>
        public static CsvReportTable Parse(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Empty;

            using (var reader = new StringReader(csv))
            {
                return Parse(reader);
            }
        }

        /// <summary>Parse a whole CSV from a reader. Only for reports small enough to hold in memory.</summary>
        public static CsvReportTable Parse(TextReader reader)
        {
            var headers = new List<string>();
            var rows = new List<IReadOnlyDictionary<string, string>>();

            foreach (var row in StreamRows(reader, headers))
            {
                rows.Add(row);
            }

            return rows.Count == 0 && headers.Count == 0
                ? Empty
                : new CsvReportTable(headers, rows);
        }

        /// <summary>
        /// Reads the CSV row by row without ever holding more than one row in memory.
        ///
        /// This is what the per-user report uses. At ~200,000 licensed users, materialising the whole report
        /// as a string plus one dictionary per row costs hundreds of megabytes before anything is written -
        /// the same class of problem that caused the OutOfMemoryException the other usage-report loaders were
        /// changed to avoid. Streaming keeps the peak at a single row.
        /// </summary>
        /// <param name="reader">CSV source. Not disposed here; the caller owns it.</param>
        /// <param name="headersOut">Optional list populated with the header row before the first row is yielded.</param>
        public static IEnumerable<IReadOnlyDictionary<string, string>> StreamRows(TextReader reader, List<string> headersOut = null)
        {
            if (reader == null) yield break;

            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                // Graph occasionally pads the header row; never let a stray column abort a whole import.
                MissingFieldFound = null,
                BadDataFound = null,
                DetectColumnCountChanges = false,
                TrimOptions = TrimOptions.Trim,
            };

            using (var parser = new CsvParser(reader, config, leaveOpen: true))
            {
                if (!parser.Read()) yield break;

                var headers = parser.Record
                    .Select(h => (h ?? string.Empty).Trim().TrimStart('\uFEFF'))
                    .ToList();

                headersOut?.AddRange(headers);

                while (parser.Read())
                {
                    var record = parser.Record;
                    if (record == null || record.Length == 0) continue;

                    // A trailing newline yields a single empty field; that isn't a row.
                    if (record.Length == 1 && string.IsNullOrWhiteSpace(record[0])) continue;

                    var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < headers.Count; i++)
                    {
                        var header = headers[i];
                        if (string.IsNullOrEmpty(header) || row.ContainsKey(header)) continue;
                        row[header] = i < record.Length ? record[i] : null;
                    }
                    yield return row;
                }
            }
        }

        /// <summary>
        /// Throws when a column the loader depends on is absent. Without this a renamed Microsoft column
        /// yields zero parsed rows, which is indistinguishable from the perfectly normal "this tenant has no
        /// Copilot licences" case - the import would look successful while quietly storing nothing.
        /// </summary>
        public static void RequireHeaders(IReadOnlyList<string> headers, CopilotReportRequest request, params string[] required)
        {
            if (headers == null || headers.Count == 0)
            {
                throw new InvalidOperationException($"Copilot report {request} returned no CSV header row.");
            }

            var present = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
            var missing = required.Where(h => !present.Contains(h)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Copilot report {request} is missing expected column(s): {string.Join(", ", missing)}. " +
                    $"Columns returned: {string.Join(", ", headers)}. " +
                    "Microsoft has probably changed the report schema; the import was stopped rather than storing an empty or partial snapshot.");
            }
        }
    }

    /// <summary>
    /// Field readers for the Copilot report CSVs. Every one returns null/false rather than throwing on a
    /// missing or unparseable value: a single odd cell in a 200,000-row report must not fail the import, and
    /// a column that is absent simply means Graph returned report version 1.
    /// </summary>
    public static class CsvReportFieldExtensions
    {
        private const string GraphDateFormat = "yyyy-MM-dd";

        public static string GetString(this IReadOnlyDictionary<string, string> row, string header)
        {
            if (row == null || header == null) return null;
            if (!row.TryGetValue(header, out var value)) return null;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>
        /// Graph writes dates as yyyy-MM-dd and writes an empty cell for "never". Parsed exactly and with the
        /// invariant culture so a server running under a non-Gregorian or day-first locale can't misread them.
        /// </summary>
        public static DateTime? GetDate(this IReadOnlyDictionary<string, string> row, string header)
        {
            var raw = row.GetString(header);
            if (raw == null) return null;

            return DateTime.TryParseExact(raw, GraphDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.Date
                : (DateTime?)null;
        }

        public static int? GetInt(this IReadOnlyDictionary<string, string> row, string header)
        {
            var raw = row.GetString(header);
            if (raw == null) return null;

            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        }

        public static long? GetLong(this IReadOnlyDictionary<string, string> row, string header)
        {
            var raw = row.GetString(header);
            if (raw == null) return null;

            return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (long?)null;
        }

        public static double? GetDouble(this IReadOnlyDictionary<string, string> row, string header)
        {
            var raw = row.GetString(header);
            if (raw == null) return null;

            return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (double?)null;
        }
    }
}
