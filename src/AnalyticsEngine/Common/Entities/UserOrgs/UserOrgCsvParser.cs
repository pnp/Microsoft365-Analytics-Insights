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
            : this(lineNumber, null, reason, null, null)
        {
        }

        public UserOrgCsvRowProblem(int lineNumber, string code, string reason, string upn, string orgValue)
        {
            LineNumber = lineNumber;
            Code = code;
            Reason = reason;
            Upn = upn;
            OrgValue = orgValue;
        }

        /// <summary>The 1-based line in the file, so the admin can go and look at it.</summary>
        public int LineNumber { get; }

        /// <summary>A key from <see cref="UserOrgCsvProblemCodes"/>, which the portal words itself.</summary>
        public string Code { get; }

        /// <summary>The English reason, for anything that is not the portal.</summary>
        public string Reason { get; }

        /// <summary>The user column exactly as it reads in the file, or <c>null</c> when the row has none.</summary>
        public string Upn { get; }

        /// <summary>The value column as it reads in the file, or <c>null</c>.</summary>
        public string OrgValue { get; }
    }

    /// <summary>Why a row is not imported. The portal words these; they are an API contract.</summary>
    public static class UserOrgCsvProblemCodes
    {
        public const string MissingUserColumn = "missingUserColumn";
        public const string UserEmptyOrTooLong = "userEmptyOrTooLong";
        public const string NotAValidUpn = "notAValidUpn";

        /// <summary>
        /// The row carries text past the file's other rows - almost always a value holding the separator
        /// without quotes, which reading only the chosen columns would import cut short.
        /// </summary>
        public const string TooManyValues = "tooManyValues";

        /// <summary>A well-formed row whose user matches nobody in the database. Decided in SQL, not by the parser.</summary>
        public const string UnknownUser = "unknownUser";
    }

    /// <summary>Why a whole file cannot be imported. The portal words these; they are an API contract.</summary>
    public static class UserOrgCsvBlockingCodes
    {
        /// <summary>The bytes are not valid UTF-8 - almost always Excel's plain "CSV (Comma delimited)".</summary>
        public const string NotUtf8 = "notUtf8";

        /// <summary>An Excel workbook (.xlsx or .xls) rather than a CSV.</summary>
        public const string ExcelWorkbook = "excelWorkbook";

        /// <summary>Some other binary file.</summary>
        public const string NotText = "notText";

        public const string UnterminatedQuote = "unterminatedQuote";

        /// <summary>A row ran on across several lines, almost always because of a stray quotation mark.</summary>
        public const string RowSpansLines = "rowSpansLines";

        /// <summary>The columns to read could not be decided, or the ones chosen do not exist.</summary>
        public const string ChooseColumns = "chooseColumns";

        /// <summary>The file has a single column, so no row can carry a value.</summary>
        public const string OneColumn = "oneColumn";

        public const string TooManyRows = "tooManyRows";
        public const string NoRows = "noRows";
        public const string NoUsableRows = "noUsableRows";
    }

    /// <summary>Why a file cannot be imported at all, and where.</summary>
    public sealed class UserOrgCsvBlocking
    {
        public UserOrgCsvBlocking(string code, int? line = null, int? lastLine = null)
        {
            Code = code;
            Line = line;
            LastLine = lastLine;
        }

        /// <summary>A key from <see cref="UserOrgCsvBlockingCodes"/>.</summary>
        public string Code { get; }

        /// <summary>The line the problem starts on, where there is one.</summary>
        public int? Line { get; }

        /// <summary>For <see cref="UserOrgCsvBlockingCodes.RowSpansLines"/>, the line the run-on row ends on.</summary>
        public int? LastLine { get; }
    }

    /// <summary>How to read one upload.</summary>
    public sealed class UserOrgCsvParseOptions
    {
        /// <summary>
        /// The org type's name. A header equal to it identifies the value column, which is what the
        /// portal's example file tells the admin to write - and the only safe way to pick a column out
        /// of a wide export without asking.
        /// </summary>
        public string OrgTypeName { get; set; }

        /// <summary>The 0-based column the admin chose as the user, overriding detection.</summary>
        public int? UserColumn { get; set; }

        /// <summary>The 0-based column the admin chose as the value, overriding detection.</summary>
        public int? ValueColumn { get; set; }

        public int MaxDataLines { get; set; } = UserOrgCsvParser.MaxDataLines;
    }

    /// <summary>The outcome of parsing an uploaded CSV.</summary>
    public sealed class UserOrgCsvParseResult
    {
        public IReadOnlyList<UserOrgStagedRow> Rows { get; set; } = new UserOrgStagedRow[0];

        public IReadOnlyList<UserOrgCsvRowProblem> Problems { get; set; } = new UserOrgCsvRowProblem[0];

        /// <summary>
        /// Why the file must not be imported at all, or <c>null</c>. Every caller refuses a file with
        /// one: each of these means rows would be misread, and in a Replace a misread row is a user
        /// whose value is cleared.
        /// </summary>
        public UserOrgCsvBlocking Blocking { get; set; }

        /// <summary>The delimiter that was detected, shown to the admin so a wrong guess is visible.</summary>
        public char Delimiter { get; set; } = ',';

        /// <summary>Whether a header row was recognised (as opposed to assuming column order).</summary>
        public bool HeaderDetected { get; set; }

        /// <summary>The header row's names, or <c>null</c> when there is no header row.</summary>
        public IReadOnlyList<string> Columns { get; set; }

        /// <summary>How many columns the file has, header or not.</summary>
        public int ColumnCount { get; set; }

        /// <summary>The 0-based column read as the user, or <c>null</c> when it could not be decided.</summary>
        public int? UserColumnIndex { get; set; }

        /// <summary>The 0-based column read as the value, or <c>null</c> when it could not be decided.</summary>
        public int? ValueColumnIndex { get; set; }

        public string UpnColumnName { get; set; }

        public string OrgColumnName { get; set; }

        /// <summary>Data lines read, excluding any header.</summary>
        public int DataLinesRead { get; set; }

        /// <summary>Whether reading stopped at a cap rather than at the end of the file.</summary>
        public bool Truncated { get; set; }

        /// <summary>
        /// Rows whose organisation name was longer than the column and was therefore shortened.
        /// </summary>
        /// <remarks>
        /// Reported rather than rejected. An over-length name is stored shortened because dropping the
        /// user from the organisation entirely would be the worse outcome - but it has to be said out
        /// loud, because two names that differ only after the cut-off silently become one organisation,
        /// and nothing else in the import summary would reveal that.
        /// </remarks>
        public int TruncatedValueCount { get; set; }

        /// <summary>
        /// Whether the file ended inside a quoted field, meaning a closing quote is missing.
        /// </summary>
        /// <remarks>
        /// Treated as unreadable rather than salvaged: everything after the stray quote is swallowed
        /// into one value, so in a Replace import all those users would look absent - and absent means
        /// their value is cleared.
        /// </remarks>
        public bool UnterminatedQuote { get; set; }
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

        /// <summary>
        /// Every header recognised as the user column. The first three name the sign-in name itself;
        /// the rest are looser, and lose to any of the first three when a file has both - an HR export
        /// commonly carries an email column beside the UPN, and in a hybrid tenant the two differ, so
        /// taking whichever came first matched nobody.
        /// </summary>
        private static readonly string[] UpnHeaderNames =
        {
            "upn", "userprincipalname", "user principal name", "user", "username",
            "user name", "email", "emailaddress", "email address", "mail",
        };

        private static readonly HashSet<string> StrongUpnHeaderNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "upn", "userprincipalname", "user principal name",
        };

        private static readonly string[] OrgHeaderNames =
        {
            "org", "orgname", "org name", "organisation", "organization",
            "organisationname", "organizationname", "organisation name", "organization name",
            "value", "orgvalue", "org value", "group", "team", "costcentre", "costcenter",
            "cost centre", "cost center", "businessunit", "business unit", "division", "department",
        };

        /// <summary>
        /// Characters a spreadsheet never shows and a UPN or header never contains: a byte order mark
        /// left in the middle of a file by concatenation, and zero-width characters pasted from a web
        /// page or a chat. Left in, they make a correct UPN "not a valid user principal name" with
        /// nothing visible to explain it.
        /// </summary>
        private static readonly char[] InvisibleCharacters = { '\uFEFF', '\u200B', '\u200C', '\u200D', '\u2060' };

        /// <summary>UTF-8 that throws on an invalid byte instead of quietly substituting U+FFFD.</summary>
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>How many leading records decide the delimiter and the columns.</summary>
        private const int SampleRecords = 20;

        /// <summary>
        /// Parses an uploaded file.
        /// </summary>
        /// <param name="stream">The uploaded content. Read from the current position to the end.</param>
        /// <param name="maxDataLines">
        /// Stop after this many data lines. Defaults to the full allowance: the preview reads the whole
        /// file, because it doubles as the blast-radius preflight for a Replace and a sample of the
        /// first few rows cannot answer that question.
        /// </param>
        public static UserOrgCsvParseResult Parse(Stream stream, int maxDataLines = MaxDataLines)
        {
            return Parse(stream, new UserOrgCsvParseOptions { MaxDataLines = maxDataLines });
        }

        /// <summary>Parses an uploaded file with the org type's name and any column choice.</summary>
        public static UserOrgCsvParseResult Parse(Stream stream, UserOrgCsvParseOptions options)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            // Read as bytes, not text: the encoding has to be proved before a single character is
            // decoded. The upload is already in memory - the controller buffers it, capped at 32 MB -
            // so a MemoryStream that exposes its buffer is read in place rather than copied.
            ArraySegment<byte> bytes;
            var memory = stream as MemoryStream;
            ArraySegment<byte> buffer;
            if (memory != null && memory.TryGetBuffer(out buffer))
            {
                var position = (int)memory.Position;
                bytes = new ArraySegment<byte>(buffer.Array, buffer.Offset + position, (int)memory.Length - position);
            }
            else
            {
                var copy = new MemoryStream();
                stream.CopyTo(copy);
                bytes = new ArraySegment<byte>(copy.GetBuffer(), 0, (int)copy.Length);
            }

            return Parse(bytes, options ?? new UserOrgCsvParseOptions());
        }

        private static UserOrgCsvParseResult Parse(ArraySegment<byte> bytes, UserOrgCsvParseOptions options)
        {
            var result = new UserOrgCsvParseResult();
            var data = bytes.Array ?? new byte[0];
            var start = bytes.Offset;
            var count = bytes.Count;

            if (LooksLikeExcelWorkbook(data, start, count))
            {
                result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.ExcelWorkbook);
                return result;
            }

            // A byte order mark says outright what the file is; Excel writes one for "CSV UTF-8" and for
            // "Unicode Text". Without one the file must be UTF-8, and is checked byte by byte below.
            Encoding encoding;
            var preamble = 0;
            if (count >= 3 && data[start] == 0xEF && data[start + 1] == 0xBB && data[start + 2] == 0xBF)
            {
                encoding = StrictUtf8;
                preamble = 3;
            }
            else if (count >= 2 && data[start] == 0xFF && data[start + 1] == 0xFE)
            {
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
                preamble = 2;
            }
            else if (count >= 2 && data[start] == 0xFE && data[start + 1] == 0xFF)
            {
                encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
                preamble = 2;
            }
            else
            {
                encoding = StrictUtf8;

                // A NUL never appears in a text CSV. Early on, it means a binary file - or UTF-16 with
                // no byte order mark, which no spreadsheet writes.
                if (ContainsNul(data, start, Math.Min(count, 4096)))
                {
                    result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.NotText);
                    return result;
                }
            }

            if (ReferenceEquals(encoding, StrictUtf8))
            {
                // Refused rather than decoded with replacement characters. Excel's plain "CSV (Comma
                // delimited)" is saved in the Windows code page, and read as UTF-8 every accented letter
                // becomes U+FFFD - worse, every Greek name becomes a run of U+FFFD, so two different
                // organisations of the same length collapse into one. The UPNs are ASCII and would
                // still match, so nothing else would reveal it.
                var invalidAt = FindInvalidUtf8(data, start + preamble, count - preamble);
                if (invalidAt >= 0)
                {
                    result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.NotUtf8, LineOf(data, start, invalidAt));
                    return result;
                }
            }

            try
            {
                using (var text = new MemoryStream(data, start + preamble, count - preamble, writable: false))
                using (var reader = new StreamReader(text, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 65536))
                {
                    ReadRows(new RecordReader(reader), options, result);
                }
            }
            catch (DecoderFallbackException)
            {
                // Only UTF-16 can get here - UTF-8 was proved above - and only with a broken surrogate
                // pair, which no spreadsheet writes.
                result.Rows = new UserOrgStagedRow[0];
                result.Problems = new UserOrgCsvRowProblem[0];
                result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.NotText);
            }

            return result;
        }

        /// <summary>
        /// Reads the records into rows, deciding the delimiter and the columns from the first few.
        /// </summary>
        /// <remarks>
        /// Streams: each record is split into a row as it is read, and only the first few are held at
        /// once, so a half-million-row file costs its rows and not also a list of every line.
        /// </remarks>
        private static void ReadRows(RecordReader records, UserOrgCsvParseOptions options, UserOrgCsvParseResult result)
        {
            var maxDataLines = options.MaxDataLines <= 0 ? MaxDataLines : options.MaxDataLines;
            var rows = new List<UserOrgStagedRow>();
            var problems = new List<UserOrgCsvRowProblem>();
            result.Rows = rows;
            result.Problems = problems;

            var sample = new List<Record>();
            Record record;
            while (sample.Count < SampleRecords && records.TryRead(out record))
            {
                if (Blocked(record, result))
                {
                    return;
                }

                sample.Add(record);
            }

            if (sample.Count == 0)
            {
                result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.NoRows);
                return;
            }

            // Excel's "sep=;" line names the delimiter outright and is not a row.
            char delimiter;
            var skip = TryReadSeparatorHint(sample[0].Text, out delimiter) ? 1 : 0;
            if (skip == 0)
            {
                delimiter = DetectDelimiterIn(sample.Select(r => r.Text).ToList());
            }

            result.Delimiter = delimiter;

            var sampleFields = sample.Skip(skip).Select(r => SplitRecord(r.Text, delimiter)).ToList();
            if (sampleFields.Count == 0)
            {
                result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.NoRows);
                return;
            }

            var columnCount = sampleFields.Max(f => f.Count);
            result.ColumnCount = columnCount;

            var header = sampleFields[0].Select(CleanHeader).ToList();
            while (header.Count < columnCount)
            {
                header.Add(string.Empty);
            }

            var keys = header.Select(Normalise).ToList();

            // A header names its columns; it does not hold an email address. Without the second test a
            // file with no header whose first row was "alice@contoso.com,User" read the value "User" as
            // a column name, and Alice was dropped as the header row - even with the columns chosen.
            var firstRowHoldsAUpn = sampleFields[0].Any(field =>
            {
                var upn = UserOrgRules.NormaliseUpn(field);
                return upn != null && IsPlausibleUpn(upn);
            });
            var headerDetected = keys.Any(k => UpnHeaderNames.Contains(k)) && !firstRowHoldsAUpn;
            result.HeaderDetected = headerDetected;
            if (headerDetected)
            {
                result.Columns = header;
            }

            // Columns that carry anything at all. A spreadsheet exported with an empty column after the
            // data writes a trailing delimiter on every line; that column is not a choice to make.
            var used = new bool[columnCount];
            for (var i = 0; i < columnCount; i++)
            {
                used[i] = (headerDetected && header[i].Length > 0)
                    || sampleFields.Skip(headerDetected ? 1 : 0).Any(f => i < f.Count && !string.IsNullOrWhiteSpace(f[i]));
            }

            var usedColumns = Enumerable.Range(0, columnCount).Where(i => used[i]).ToList();

            int? userColumn = null;
            int? valueColumn = null;

            if (options.UserColumn.HasValue || options.ValueColumn.HasValue)
            {
                var u = options.UserColumn;
                var v = options.ValueColumn;
                if (u.HasValue && v.HasValue && u.Value >= 0 && v.Value >= 0 && u.Value < columnCount && v.Value < columnCount && u.Value != v.Value)
                {
                    userColumn = u;
                    valueColumn = v;
                }
            }
            else if (usedColumns.Count < 2)
            {
                result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.OneColumn);
                return;
            }
            else if (headerDetected)
            {
                userColumn = FindUserColumn(keys);
                valueColumn = FindValueColumn(keys, userColumn.Value, usedColumns, options.OrgTypeName);
            }
            else if (usedColumns.Count == 2)
            {
                // No header: the historical positional reading, first column the user.
                userColumn = usedColumns[0];
                valueColumn = usedColumns[1];
            }

            result.UserColumnIndex = userColumn;
            result.ValueColumnIndex = valueColumn;
            if (headerDetected)
            {
                result.UpnColumnName = userColumn.HasValue ? header[userColumn.Value] : null;
                result.OrgColumnName = valueColumn.HasValue ? header[valueColumn.Value] : null;
            }

            if (!userColumn.HasValue || !valueColumn.HasValue)
            {
                // Several candidate columns and nothing that says which is which. Asked rather than
                // guessed: taking "the first other column" imported everyone's employee number as their
                // cost centre from an ordinary HR export, with no problem reported.
                result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.ChooseColumns);
                return;
            }

            var firstData = skip + (headerDetected ? 1 : 0);
            var dataLines = 0;
            var position = 0;
            var expectedWidth = ExpectedRowWidth(sampleFields, headerDetected, userColumn.Value, valueColumn.Value);

            while (true)
            {
                Record next;
                if (position < sample.Count)
                {
                    next = sample[position++];
                    if (position <= firstData)
                    {
                        continue;
                    }
                }
                else if (records.TryRead(out next))
                {
                    if (Blocked(next, result))
                    {
                        break;
                    }
                }
                else
                {
                    break;
                }

                var fields = SplitRecord(next.Text, delimiter);
                if (fields.All(string.IsNullOrWhiteSpace))
                {
                    // An empty spreadsheet row - ",,," - is not a row the admin wrote.
                    continue;
                }

                if (dataLines >= maxDataLines)
                {
                    // Truncated means there is genuinely a row past the allowance - not merely that the
                    // count was reached. A file of exactly maxDataLines rows must not be refused.
                    result.Truncated = true;
                    result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.TooManyRows);
                    break;
                }

                dataLines++;
                ReadRow(next.LineNumber, fields, userColumn.Value, valueColumn.Value, expectedWidth, delimiter, rows, problems, result);
            }

            result.DataLinesRead = dataLines;

            if (result.Blocking == null && rows.Count == 0)
            {
                result.Blocking = new UserOrgCsvBlocking(
                    problems.Count > 0 ? UserOrgCsvBlockingCodes.NoUsableRows : UserOrgCsvBlockingCodes.NoRows);
            }
        }

        /// <summary>
        /// How many fields a row may carry text in before it is refused as <see cref="UserOrgCsvProblemCodes.TooManyValues"/>.
        /// </summary>
        /// <remarks>
        /// A value holding the separator without quotes - <c>Retail, North</c> in a comma-separated file -
        /// splits into one field more than its neighbours, and reading only the chosen columns would import
        /// <c>Retail</c> for that person without a word, wherever the row sits in the file. So the width is
        /// the widest a row may legitimately be: the header as written, since Excel writes a trailing
        /// separator for every column it holds data in; the width most sample rows share, so a file whose
        /// rows are all wider than its header is still read; and the columns being read.
        /// </remarks>
        private static int ExpectedRowWidth(List<List<string>> sampleFields, bool headerDetected, int userColumn, int valueColumn)
        {
            var width = Math.Max(userColumn, valueColumn) + 1;
            if (headerDetected)
            {
                width = Math.Max(width, sampleFields[0].Count);
            }

            var usual = sampleFields
                .Skip(headerDetected ? 1 : 0)
                .Select(TextWidth)
                .Where(w => w > 0)
                .GroupBy(w => w)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .Select(g => g.Key)
                .FirstOrDefault();

            return Math.Max(width, usual);
        }

        /// <summary>How many fields a row carries text in: up to and including its last non-blank one.</summary>
        private static int TextWidth(List<string> fields)
        {
            for (var i = fields.Count - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(fields[i]))
                {
                    return i + 1;
                }
            }

            return 0;
        }

        private static void ReadRow(
            int lineNumber,
            List<string> fields,
            int userColumn,
            int valueColumn,
            int expectedWidth,
            char delimiter,
            List<UserOrgStagedRow> rows,
            List<UserOrgCsvRowProblem> problems,
            UserOrgCsvParseResult result)
        {
            var rawValue = valueColumn < fields.Count ? fields[valueColumn] : null;

            if (userColumn >= fields.Count)
            {
                problems.Add(new UserOrgCsvRowProblem(
                    lineNumber, UserOrgCsvProblemCodes.MissingUserColumn, "the row has no user column", null, Shorten(rawValue)));
                return;
            }

            var rawUpn = StripInvisible(fields[userColumn]);
            var upn = UserOrgRules.NormaliseUpn(rawUpn);
            if (upn == null)
            {
                problems.Add(new UserOrgCsvRowProblem(
                    lineNumber, UserOrgCsvProblemCodes.UserEmptyOrTooLong, "the user column is empty or too long",
                    Shorten(rawUpn?.Trim()), Shorten(rawValue)));
                return;
            }

            if (!IsPlausibleUpn(upn))
            {
                // Entra restricts a UPN to a known ASCII set, so a value outside it cannot match any
                // user. Saying so beats letting it fail silently as an unknown UPN later - and it is
                // the signal that the delimiter was guessed wrongly, which in a Replace import is the
                // difference between a reported problem and a silent wipe.
                problems.Add(new UserOrgCsvRowProblem(
                    lineNumber,
                    UserOrgCsvProblemCodes.NotAValidUpn,
                    "the user column is not a valid Microsoft Entra user principal name - check the file's "
                    + "column separator and that the user column really holds user principal names",
                    Shorten(upn),
                    Shorten(rawValue)));
                return;
            }

            if (TextWidth(fields) > expectedWidth)
            {
                // Shown as the value and everything after it, which for a two-column file is the value as
                // it was meant - "Retail, North" - so the admin can see what needs quoting.
                problems.Add(new UserOrgCsvRowProblem(
                    lineNumber,
                    UserOrgCsvProblemCodes.TooManyValues,
                    "the row has more values than the file's other rows - a value that contains the column "
                    + "separator must be in double quotes",
                    Shorten(upn),
                    Shorten(string.Join(delimiter.ToString(), fields.Skip(Math.Min(valueColumn, fields.Count))))));
                return;
            }

            if (UserOrgRules.WouldTruncate(rawValue))
            {
                result.TruncatedValueCount++;
            }

            rows.Add(new UserOrgStagedRow(lineNumber, upn, UserOrgRules.NormaliseOrgValue(rawValue)));
        }

        /// <summary>
        /// Whether a record makes the file unreadable: a row that runs on across lines, or a quoted field
        /// that never closes.
        /// </summary>
        /// <remarks>
        /// A two-column file of names never legitimately spans lines, so a record that does is almost
        /// always a stray quotation mark - an inch mark in "12" Rack" - that swallowed the following
        /// lines into one value. Those people then look absent, which a Replace treats as "clear their
        /// value", and the first person's value becomes the text of other people's rows. Refused, with
        /// the lines named, rather than salvaged.
        /// </remarks>
        private static bool Blocked(Record record, UserOrgCsvParseResult result)
        {
            if (record.Unterminated)
            {
                result.UnterminatedQuote = true;
                result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.UnterminatedQuote, record.LineNumber);
                return true;
            }

            if (record.LastLine > record.LineNumber)
            {
                result.Blocking = new UserOrgCsvBlocking(UserOrgCsvBlockingCodes.RowSpansLines, record.LineNumber, record.LastLine);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a value could be a Microsoft Entra user principal name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Entra restricts a UPN to <c>A-Z a-z 0-9 ' . - _ ! # ^ ~ @</c> and explicitly disallows
        /// accented characters; non-Latin names live in the display name instead. That is also why
        /// <c>dbo.users.user_name</c> is <c>varchar(250)</c> and why that is not a bug.
        /// </para>
        /// <para>
        /// The character set is checked rather than just the ASCII range, and that is load-bearing
        /// rather than pedantic. If the delimiter is guessed wrongly - a semicolon-separated file read
        /// as comma-separated, say - the first field becomes something like
        /// <c>someone@contoso.com;Retail</c>. Accepting it would stage a UPN that matches nobody, and a
        /// Replace import would then clear the value of every user the file was supposed to keep.
        /// Rejecting it turns a silent wipe into a reported, actionable problem row.
        /// </para>
        /// <para>
        /// Note this is the one place in this feature where ASCII is assumed, and it is assumed about a
        /// UPN specifically - never about an organisation name, which is Unicode throughout.
        /// </para>
        /// </remarks>
        internal static bool IsPlausibleUpn(string value)
        {
            var seenAt = false;

            foreach (var c in value)
            {
                if (c > 127)
                {
                    return false;
                }

                if (c == '@')
                {
                    seenAt = true;
                    continue;
                }

                var allowed = (c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '\'' || c == '.' || c == '-' || c == '_'
                    || c == '!' || c == '#' || c == '^' || c == '~';

                if (!allowed)
                {
                    return false;
                }
            }

            return seenAt;
        }

        private struct Record
        {
            public string Text;

            /// <summary>The physical line the record starts on.</summary>
            public int LineNumber;

            /// <summary>The physical line it ends on - later than <see cref="LineNumber"/> for a run-on row.</summary>
            public int LastLine;

            /// <summary>The file ended inside this record's quoted field.</summary>
            public bool Unterminated;
        }

        /// <summary>
        /// Reads logical CSV records one at a time, honouring quoted fields that span physical lines.
        /// </summary>
        /// <remarks>
        /// Blank lines are skipped rather than returned. A trailing newline is normal and is not a row,
        /// and each record carries its own line number, so nothing is lost by skipping them - but
        /// counting them against the row allowance would let a file padded with blank lines silently
        /// lose real rows off the end.
        /// </remarks>
        private sealed class RecordReader
        {
            private readonly TextReader _reader;
            private readonly StringBuilder _current = new StringBuilder();
            private int _physicalLine;

            public RecordReader(TextReader reader)
            {
                _reader = reader;
            }

            public bool TryRead(out Record record)
            {
                record = default(Record);
                _current.Clear();
                var inQuotes = false;
                var startLine = 0;

                string line;
                while ((line = _reader.ReadLine()) != null)
                {
                    _physicalLine++;
                    if (!inQuotes && _current.Length == 0)
                    {
                        startLine = _physicalLine;
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
                        // A quoted field containing a newline: keep going, preserving the line break, so
                        // the caller can see where the record started and where it ended.
                        _current.Append(line).Append('\n');
                        continue;
                    }

                    _current.Append(line);
                    var text = _current.ToString();
                    _current.Clear();

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    record = new Record { Text = text, LineNumber = startLine, LastLine = _physicalLine };
                    return true;
                }

                if (_current.Length > 0 && !string.IsNullOrWhiteSpace(_current.ToString()))
                {
                    // An unterminated quote ran to the end of the file, swallowing every remaining line
                    // into one value. Returned flagged, so the file is refused rather than imported with
                    // all those people missing.
                    record = new Record
                    {
                        Text = _current.ToString(),
                        LineNumber = startLine,
                        LastLine = _physicalLine,
                        Unterminated = true,
                    };
                    _current.Clear();
                    return true;
                }

                return false;
            }
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
                var split = sample.Select(t => SplitRecord(t, candidate)).ToList();
                var fieldCount = split[0].Count;
                if (fieldCount < 2)
                {
                    continue;
                }

                // Reward a delimiter that yields the same number of fields on every sampled line.
                var consistent = split.Count(f => f.Count == fieldCount);

                // ...and, decisively, one that leaves something that could actually be a user principal
                // name in one of the columns. Consistency alone is not enough to choose between
                // candidates: a semicolon-separated file whose organisation names contain commas splits
                // just as consistently on the comma, and ties were previously broken by the order the
                // candidates happen to be listed in. That silently produced UPNs like
                // "someone@contoso.com;Retail", which match nobody - and in a Replace import
                // "matches nobody" means every user in the file loses their value.
                var plausible = split.Count(fields => fields.Any(f =>
                {
                    var upn = UserOrgRules.NormaliseUpn(f);
                    return upn != null && IsPlausibleUpn(upn);
                }));

                var score = (plausible * 10000) + (consistent * 100) + Math.Min(fieldCount, 10);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
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

        /// <summary>The user column: a UPN-specific header if there is one, otherwise the first looser one.</summary>
        private static int FindUserColumn(IReadOnlyList<string> keys)
        {
            for (var i = 0; i < keys.Count; i++)
            {
                if (StrongUpnHeaderNames.Contains(keys[i]))
                {
                    return i;
                }
            }

            for (var i = 0; i < keys.Count; i++)
            {
                if (UpnHeaderNames.Contains(keys[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// The value column, or <c>null</c> when it cannot be told without asking.
        /// </summary>
        /// <remarks>
        /// A header equal to the org type's name wins wherever it is - it is what the portal's example
        /// file asks for. Otherwise the value column is only assumed when the file has exactly one other
        /// column with anything in it, which is the two-column file this import was designed for.
        /// Guessing among several - by alias, or by position - is how an HR export imported everyone's
        /// employee number as their cost centre with nothing reported.
        /// </remarks>
        private static int? FindValueColumn(IReadOnlyList<string> keys, int userColumn, IReadOnlyList<int> usedColumns, string orgTypeName)
        {
            var typeKey = Normalise(orgTypeName);
            if (typeKey.Length > 0)
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    if (i != userColumn && keys[i] == typeKey)
                    {
                        return i;
                    }
                }
            }

            var others = usedColumns.Where(i => i != userColumn).ToList();
            return others.Count == 1 ? others[0] : (int?)null;
        }

        private static string CleanHeader(string field)
        {
            return StripInvisible(field ?? string.Empty).Trim().Trim('"').Trim();
        }

        private static string Normalise(string header)
        {
            return CleanHeader(header).ToLowerInvariant();
        }

        private static string StripInvisible(string value)
        {
            if (value == null || value.IndexOfAny(InvisibleCharacters) < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (Array.IndexOf(InvisibleCharacters, c) < 0)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        /// <summary>Caps a value echoed back in a problem, so a misread line cannot balloon the preview.</summary>
        private static string Shorten(string value)
        {
            const int Max = 256;
            return value == null || value.Length <= Max ? value : value.Substring(0, Max);
        }

        /// <summary>Excel's "sep=;" first line, which names the delimiter.</summary>
        private static bool TryReadSeparatorHint(string record, out char delimiter)
        {
            delimiter = ',';
            var text = (record ?? string.Empty).Trim();
            if (text.Length == 5 && text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase)
                && Array.IndexOf(CandidateDelimiters, text[4]) >= 0)
            {
                delimiter = text[4];
                return true;
            }

            return false;
        }

        /// <summary>
        /// An .xlsx (a zip archive) or a legacy .xls (an OLE compound file) - the commonest wrong upload,
        /// and one that otherwise reads as a wall of "not a valid user principal name" problems.
        /// </summary>
        private static bool LooksLikeExcelWorkbook(byte[] data, int start, int count)
        {
            if (count >= 4 && data[start] == 0x50 && data[start + 1] == 0x4B && data[start + 2] == 0x03 && data[start + 3] == 0x04)
            {
                return true;
            }

            return count >= 8
                && data[start] == 0xD0 && data[start + 1] == 0xCF && data[start + 2] == 0x11 && data[start + 3] == 0xE0
                && data[start + 4] == 0xA1 && data[start + 5] == 0xB1 && data[start + 6] == 0x1A && data[start + 7] == 0xE1;
        }

        private static bool ContainsNul(byte[] data, int start, int count)
        {
            for (var i = start; i < start + count; i++)
            {
                if (data[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The index of the first byte that is not part of a well-formed UTF-8 sequence, or -1.
        /// </summary>
        /// <remarks>
        /// Written out rather than decoding with a throwing encoder so the admin can be told which line
        /// to look at. Follows the Unicode well-formed byte table: no overlong forms, no surrogates, no
        /// code points past U+10FFFF, and no sequence cut off at the end of the file.
        /// </remarks>
        internal static int FindInvalidUtf8(byte[] data, int start, int count)
        {
            var end = start + count;
            var i = start;
            while (i < end)
            {
                var b = data[i];
                if (b < 0x80)
                {
                    i++;
                    continue;
                }

                int length;
                byte low = 0x80, high = 0xBF;
                if (b >= 0xC2 && b <= 0xDF)
                {
                    length = 2;
                }
                else if (b == 0xE0)
                {
                    length = 3;
                    low = 0xA0;
                }
                else if ((b >= 0xE1 && b <= 0xEC) || b == 0xEE || b == 0xEF)
                {
                    length = 3;
                }
                else if (b == 0xED)
                {
                    length = 3;
                    high = 0x9F;
                }
                else if (b == 0xF0)
                {
                    length = 4;
                    low = 0x90;
                }
                else if (b >= 0xF1 && b <= 0xF3)
                {
                    length = 4;
                }
                else if (b == 0xF4)
                {
                    length = 4;
                    high = 0x8F;
                }
                else
                {
                    return i;
                }

                if (i + length > end)
                {
                    return i;
                }

                // The second byte has the tighter range; the rest are ordinary continuation bytes.
                if (data[i + 1] < low || data[i + 1] > high)
                {
                    return i;
                }

                for (var j = 2; j < length; j++)
                {
                    if (data[i + j] < 0x80 || data[i + j] > 0xBF)
                    {
                        return i;
                    }
                }

                i += length;
            }

            return -1;
        }

        /// <summary>The 1-based line an offset falls on, counting LF, CRLF and a lone CR as line ends.</summary>
        private static int LineOf(byte[] data, int start, int offset)
        {
            var line = 1;
            for (var i = start; i < offset; i++)
            {
                if (data[i] == (byte)'\n')
                {
                    line++;
                }
                else if (data[i] == (byte)'\r' && (i + 1 >= offset || data[i + 1] != (byte)'\n'))
                {
                    line++;
                }
            }

            return line;
        }
    }
}
