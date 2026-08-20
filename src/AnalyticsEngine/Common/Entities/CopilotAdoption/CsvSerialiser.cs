using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// One column of a CSV export: its heading and how to get the value out of a row.
    /// </summary>
    public class CsvColumn<T>
    {
        public CsvColumn(string header, Func<T, object> value)
        {
            Header = header;
            Value = value;
        }

        public string Header { get; }

        public Func<T, object> Value { get; }
    }

    /// <summary>
    /// RFC 4180 CSV writing for the adoption exports.
    ///
    /// Written by hand rather than pulled from a library because the three things that actually go
    /// wrong with exported user lists all need deliberate handling, and all three are invisible until a
    /// customer hits them:
    ///
    /// <list type="number">
    ///   <item><b>Unicode.</b> Real tenants have users, departments and file names in Greek, Cyrillic,
    ///   Japanese and so on. The output is UTF-8 <i>with a BOM</i>, because Excel on Windows assumes the
    ///   local ANSI code page for a BOM-less CSV and will render "Καλημέρα" as mojibake - in a document
    ///   that is about to be sent to an executive.</item>
    ///
    ///   <item><b>CSV injection.</b> Values that begin with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c> or a
    ///   control character are treated as formulas by Excel and Google Sheets. Job titles and department
    ///   names come from the customer's own directory and are not trusted input, so they are prefixed
    ///   with an apostrophe, which Excel strips on display.</item>
    ///
    ///   <item><b>Culture.</b> Numbers and dates are written invariantly (ISO-8601 for dates), so a file
    ///   generated on a server with a comma decimal separator is not silently misread as two columns.</item>
    /// </list>
    /// </summary>
    public static class CsvSerialiser
    {
        /// <summary>Characters that make a spreadsheet treat a cell as a formula.</summary>
        private static readonly char[] FormulaTriggers = { '=', '+', '-', '@', '\t', '\r' };

        /// <summary>
        /// Serialises rows to an RFC 4180 CSV document (CRLF line endings, quoted where required).
        /// Does not include a byte-order mark - see <see cref="ToBytes{T}"/> for the file payload.
        /// </summary>
        public static string ToCsv<T>(IEnumerable<T> rows, IReadOnlyList<CsvColumn<T>> columns)
        {
            if (columns == null) throw new ArgumentNullException(nameof(columns));

            var sb = new StringBuilder();

            sb.Append(string.Join(",", columns.Select(c => Escape(c.Header))));
            sb.Append("\r\n");

            foreach (var row in rows ?? Enumerable.Empty<T>())
            {
                sb.Append(string.Join(",", columns.Select(c => Escape(Format(SafeValue(c, row))))));
                sb.Append("\r\n");
            }

            return sb.ToString();
        }

        /// <summary>
        /// The bytes to hand back as a downloadable file: UTF-8 with a BOM so Excel reads non-ASCII
        /// user and department names correctly.
        /// </summary>
        public static byte[] ToBytes<T>(IEnumerable<T> rows, IReadOnlyList<CsvColumn<T>> columns)
        {
            var csv = ToCsv(rows, columns);
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            return encoding.GetPreamble().Concat(encoding.GetBytes(csv)).ToArray();
        }

        /// <summary>
        /// A download file name that is stable, sortable and safe on every OS, e.g.
        /// <c>copilot-licensed-users-2026-08-20.csv</c>.
        /// </summary>
        public static string FileName(string prefix, DateTime generatedUtc)
        {
            var safePrefix = new string((prefix ?? "export")
                .Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-')
                .ToArray());

            return $"{safePrefix}-{generatedUtc:yyyy-MM-dd}.csv";
        }

        /// <summary>
        /// Reads one cell, turning an accessor failure into an empty cell rather than a failed export.
        /// A 20,000-row licence report should not be lost to one null navigation property.
        /// </summary>
        private static object SafeValue<T>(CsvColumn<T> column, T row)
        {
            try
            {
                return column.Value(row);
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }

        /// <summary>Culture-invariant rendering of a cell value.</summary>
        private static string Format(object value)
        {
            switch (value)
            {
                case null:
                    return string.Empty;

                case string s:
                    return s;

                case bool b:
                    // "Yes"/"No" rather than "True"/"False": these files are read by people, and Excel
                    // does not coerce them into anything unexpected.
                    return b ? "Yes" : "No";

                case DateTime dt:
                    return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

                case double d:
                    return d.ToString("0.##", CultureInfo.InvariantCulture);

                case float f:
                    return f.ToString("0.##", CultureInfo.InvariantCulture);

                case decimal m:
                    return m.ToString("0.##", CultureInfo.InvariantCulture);

                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);

                default:
                    return value.ToString();
            }
        }

        /// <summary>
        /// Quotes and escapes a single field per RFC 4180, and neutralises spreadsheet formula
        /// injection. Internal so the behaviour can be asserted directly by unit tests.
        /// </summary>
        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var text = value;

            // Defuse formulas before quoting. The apostrophe is inside the quoted field, which is what
            // Excel and Sheets look for; they strip it when displaying the cell.
            if (FormulaTriggers.Contains(text[0]))
            {
                text = "'" + text;
            }

            var mustQuote = text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                            || text != text.Trim();

            if (!mustQuote)
            {
                return text;
            }

            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
    }
}
