using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Common.Entities.UserOrgs
{
    /// <summary>One line of an uploaded file that could not be used, and why.</summary>
    public sealed class UserOrgCsvRowProblem
    {
        public UserOrgCsvRowProblem(int lineNumber, string reason)
        {
            LineNumber = lineNumber;
            Reason = reason;
        }

        /// <summary>The 1-based line in the file, so the admin can go and look at it.</summary>
        public int LineNumber { get; }

        public string Reason { get; }
    }

    /// <summary>The outcome of parsing an uploaded CSV.</summary>
    public sealed class UserOrgCsvParseResult
    {
        public IReadOnlyList<UserOrgStagedRow> Rows { get; set; } = new UserOrgStagedRow[0];

        public IReadOnlyList<UserOrgCsvRowProblem> Problems { get; set; } = new UserOrgCsvRowProblem[0];

        /// <summary>The delimiter that was detected, shown to the admin so a wrong guess is visible.</summary>
        public char Delimiter { get; set; } = ',';

        /// <summary>Whether a header row was recognised (as opposed to assuming column order).</summary>
        public bool HeaderDetected { get; set; }

        public string UpnColumnName { get; set; }

        public string OrgColumnName { get; set; }

        /// <summary>Data lines read, excluding any header.</summary>
        public int DataLinesRead { get; set; }

        /// <summary>Whether reading stopped at a cap rather than at the end of the file.</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// Reads an uploaded "UPN, organisation" CSV.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hand-rolled rather than taking a dependency. <c>CsvHelper</c> is already in this solution but
    /// only referenced by the importer web-job; pulling it into <c>Common.Entities</c> would mean
    /// reworking binding redirects in <c>App.Template.config</c> and <c>App.config</c> across the web
    /// app, both web-jobs, the installer and the test project - a disproportionate amount of churn and
    /// risk for a two-column file. The full RFC 4180 grammar for that case is short and is tested
    /// directly.
    /// </para>
    /// <para>
    /// Pure: takes a stream, returns values. No database, no HTTP, no configuration.
    /// </para>
    /// </remarks>
    public static class UserOrgCsvParser
    {
        /// <summary>
        /// Most data lines that will be read from one upload.
        /// </summary>
        /// <remarks>
        /// Comfortably above a 200,000-user tenant while still bounding what a mis-selected file can
        /// do to memory. Hitting it is reported rather than silently ignored, so an admin who really
        /// does have more rows finds out instead of quietly importing a prefix of their file.
        /// </remarks>
        public const int MaxDataLines = 500000;

        private static readonly char[] CandidateDelimiters = { ',', ';', '\t', '|' };

        private static readonly string[] UpnHeaderNames =
        {
            "upn", "userprincipalname", "user principal name", "user", "username",
            "user name", "email", "emailaddress", "email address", "mail",
        };

        private static readonly string[] OrgHeaderNames =
        {
            "org", "orgname", "org name", "organisation", "organization",
            "organisationname", "organizationname", "organisation name", "organization name",
            "value", "orgvalue", "org value", "group", "team", "costcentre", "costcenter",
            "cost centre", "cost center", "businessunit", "business unit", "division", "department",
        };

        /// <summary>
        /// Parses an uploaded file.
        /// </summary>
        /// <param name="stream">The uploaded content. Read from the current position to the end.</param>
        /// <param name="maxDataLines">
        /// Stop after this many data lines. The preview passes a small number so a 200,000-row file can
        /// be sampled without reading all of it.
        /// </param>
        public static UserOrgCsvParseResult Parse(Stream stream, int maxDataLines = MaxDataLines)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var result = new UserOrgCsvParseResult();
            var rows = new List<UserOrgStagedRow>();
            var problems = new List<UserOrgCsvRowProblem>();

            // detectEncodingFromByteOrderMarks handles a UTF-8/UTF-16 BOM, which Excel writes; UTF-8
            // without a BOM is the fallback, which is what almost every other tool produces. Getting
            // this wrong is how non-Latin organisation names turn into mojibake.
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true))
            {
                var lines = ReadRecords(reader, maxDataLines + 1, out var hitCap);

                if (lines.Count == 0)
                {
                    result.Problems = problems;
                    return result;
                }

                var delimiter = DetectDelimiter(lines);
                result.Delimiter = delimiter;

                var firstFields = SplitRecord(lines[0].Text, delimiter);
                int upnIndex, orgIndex;
                var headerDetected = TryReadHeader(firstFields, out upnIndex, out orgIndex);

                result.HeaderDetected = headerDetected;
                if (headerDetected)
                {
                    result.UpnColumnName = firstFields[upnIndex].Trim();
                    result.OrgColumnName = orgIndex >= 0 && orgIndex < firstFields.Count
                        ? firstFields[orgIndex].Trim()
                        : null;
                }
                else
                {
                    // No recognisable header, so fall back to position. Reported so the admin can see
                    // the assumption rather than having to infer it from a wrong result.
                    upnIndex = 0;
                    orgIndex = 1;
                }

                var startIndex = headerDetected ? 1 : 0;
                var dataLinesRead = 0;

                for (var i = startIndex; i < lines.Count; i++)
                {
                    if (dataLinesRead >= maxDataLines)
                    {
                        result.Truncated = true;
                        break;
                    }

                    var line = lines[i];
                    dataLinesRead++;

                    if (string.IsNullOrWhiteSpace(line.Text))
                    {
                        // A trailing newline is normal, not a problem worth reporting.
                        dataLinesRead--;
                        continue;
                    }

                    var fields = SplitRecord(line.Text, delimiter);

                    if (upnIndex >= fields.Count)
                    {
                        problems.Add(new UserOrgCsvRowProblem(line.LineNumber, "the row has no user column"));
                        continue;
                    }

                    var upn = UserOrgRules.NormaliseUpn(fields[upnIndex]);
                    if (upn == null)
                    {
                        problems.Add(new UserOrgCsvRowProblem(line.LineNumber, "the user column is empty or too long"));
                        continue;
                    }

                    if (!IsPlausibleUpn(upn))
                    {
                        // Entra restricts a UPN to a known ASCII set, so a value outside it cannot match
                        // any user. Saying so beats letting it fail silently as an unknown UPN later.
                        problems.Add(new UserOrgCsvRowProblem(
                            line.LineNumber,
                            "the user column contains characters that cannot appear in a Microsoft Entra user principal name"));
                        continue;
                    }

                    var orgValue = orgIndex >= 0 && orgIndex < fields.Count ? fields[orgIndex] : null;
                    rows.Add(new UserOrgStagedRow(line.LineNumber, upn, UserOrgRules.NormaliseOrgValue(orgValue)));
                }

                result.DataLinesRead = dataLinesRead;
                if (hitCap && !result.Truncated)
                {
                    result.Truncated = true;
                }
            }

            result.Rows = rows;
            result.Problems = problems;
            return result;
        }

        /// <summary>
        /// Whether a value could be a Microsoft Entra user principal name.
        /// </summary>
        /// <remarks>
        /// Entra restricts a UPN to <c>A-Z a-z 0-9 ' . - _ ! # ^ ~ @</c> and explicitly disallows
        /// accented characters; non-Latin names live in the display name instead. That is also why
        /// <c>dbo.users.user_name</c> is <c>varchar(250)</c> and why that is not a bug. Checking here
        /// means the admin is told their file has an unusable value, rather than the row being silently
        /// counted as an unknown user.
        ///
        /// Note this is the one place in this feature where ASCII is assumed, and it is assumed about a
        /// UPN specifically - never about an organisation name, which is Unicode throughout.
        /// </remarks>
        internal static bool IsPlausibleUpn(string value)
        {
            foreach (var c in value)
            {
                if (c > 127)
                {
                    return false;
                }
            }

            return true;
        }

        private struct Record
        {
            public string Text;
            public int LineNumber;
        }

        /// <summary>
        /// Picks the delimiter by seeing which candidate splits the sampled records most consistently.
        /// </summary>
        /// <remarks>
        /// Not over-engineering: Excel writes a semicolon-delimited CSV on any machine whose locale uses
        /// a comma as the decimal separator, which is most of Europe. Assuming a comma would leave those
        /// admins with a file that parses into one column and imports nothing.
        /// </remarks>
        internal static char DetectDelimiterIn(IReadOnlyList<string> sampleLines)
        {
            var sample = sampleLines.Take(20).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (sample.Count == 0)
            {
                return ',';
            }

            var best = ',';
            var bestScore = -1;

            foreach (var candidate in CandidateDelimiters)
            {
                var counts = sample.Select(t => SplitRecord(t, candidate).Count).ToList();
                var fieldCount = counts[0];
                if (fieldCount < 2)
                {
                    continue;
                }

                // Reward a delimiter that yields the same number of fields on every sampled line.
                var consistent = counts.Count(c => c == fieldCount);
                var score = (consistent * 100) + Math.Min(fieldCount, 10);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>
        /// Reads logical CSV records, honouring quoted fields that span physical lines.
        /// </summary>
        private static List<Record> ReadRecords(TextReader reader, int maxRecords, out bool hitCap)
        {
            var records = new List<Record>();
            var current = new StringBuilder();
            var inQuotes = false;
            var physicalLine = 0;
            var recordStartLine = 1;
            hitCap = false;

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                physicalLine++;
                if (current.Length == 0 && !inQuotes)
                {
                    recordStartLine = physicalLine;
                }

                foreach (var c in line)
                {
                    if (c == '"')
                    {
                        inQuotes = !inQuotes;
                    }
                }

                if (inQuotes)
                {
                    // A quoted field containing a newline: keep going, preserving the line break.
                    current.Append(line).Append('\n');
                    continue;
                }

                current.Append(line);
                records.Add(new Record { Text = current.ToString(), LineNumber = recordStartLine });
                current.Clear();

                if (records.Count >= maxRecords)
                {
                    hitCap = true;
                    return records;
                }
            }

            if (current.Length > 0)
            {
                // An unterminated quote at end of file - take what there is rather than discarding it.
                records.Add(new Record { Text = current.ToString(), LineNumber = recordStartLine });
            }

            return records;
        }

        /// <summary>
        /// Picks the delimiter by seeing which candidate splits the sampled records most consistently.
        /// </summary>
        /// <remarks>
        /// Not over-engineering: Excel writes a semicolon-delimited CSV on any machine whose locale uses
        /// a comma as the decimal separator, which is most of Europe. Assuming a comma would leave those
        /// admins with a file that parses into one column and imports nothing.
        /// </remarks>
        private static char DetectDelimiter(IReadOnlyList<Record> records)
        {
            return DetectDelimiterIn(records.Select(r => r.Text).ToList());
        }

        /// <summary>Splits one record into fields, honouring RFC 4180 quoting.</summary>
        internal static List<string> SplitRecord(string record, char delimiter)
        {
            var fields = new List<string>();
            if (record == null)
            {
                return fields;
            }

            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < record.Length; i++)
            {
                var c = record[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // A doubled quote inside a quoted field is a literal quote.
                        if (i + 1 < record.Length && record[i + 1] == '"')
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
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimiter)
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else
                {
                    field.Append(c);
                }
            }

            fields.Add(field.ToString());
            return fields;
        }

        /// <summary>
        /// Recognises a header row and which columns hold the UPN and the organisation.
        /// </summary>
        /// <remarks>
        /// Order-independent on purpose: an export that puts the organisation first is just as valid,
        /// and forcing admins to re-arrange columns before uploading would be a poor trade for a few
        /// lines of matching.
        /// </remarks>
        internal static bool TryReadHeader(IReadOnlyList<string> fields, out int upnIndex, out int orgIndex)
        {
            upnIndex = -1;
            orgIndex = -1;

            for (var i = 0; i < fields.Count; i++)
            {
                var name = Normalise(fields[i]);
                if (name.Length == 0)
                {
                    continue;
                }

                if (upnIndex < 0 && UpnHeaderNames.Contains(name))
                {
                    upnIndex = i;
                }
                else if (orgIndex < 0 && OrgHeaderNames.Contains(name))
                {
                    orgIndex = i;
                }
            }

            if (upnIndex < 0)
            {
                return false;
            }

            if (orgIndex < 0)
            {
                // A recognised user column and an unrecognised second column still reads as a header -
                // the organisation column is just named something bespoke. Take the first other column.
                for (var i = 0; i < fields.Count; i++)
                {
                    if (i != upnIndex)
                    {
                        orgIndex = i;
                        break;
                    }
                }
            }

            return orgIndex >= 0;
        }

        private static string Normalise(string header)
        {
            return (header ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
        }
    }
}
