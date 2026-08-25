using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Common.Entities.Xlsx
{
    /// <summary>
    /// A dependency-free writer for real Office Open XML (<c>.xlsx</c>) workbooks.
    ///
    /// This exists because the two obvious alternatives are both unacceptable here: EPPlus 5+ is
    /// commercially licensed (this is a public open-source repo), and adding <c>DocumentFormat.OpenXml</c>
    /// drags a NuGet dependency into a solution that has strict rules about package additions and
    /// binding-redirect churn across many projects. So we emit the OPC package by hand using only the
    /// BCL: <see cref="ZipArchive"/> for the container and hand-built XML for the parts. The whole thing
    /// is written to the .NET Standard 2.0 / .NET Framework 4.8 common surface so it compiles wherever
    /// <c>Common.Entities</c> is consumed.
    ///
    /// Three failure modes dominate spreadsheet generation and every one of them is invisible until a
    /// customer hits it, so they are handled deliberately throughout:
    /// <list type="number">
    ///   <item><b>Culture.</b> Every number written into the XML uses <see cref="CultureInfo.InvariantCulture"/>.
    ///   A server whose locale uses a decimal comma would otherwise write <c>1,5</c> where the format
    ///   demands <c>1.5</c> and produce a workbook Excel refuses to open.</item>
    ///   <item><b>Escaping.</b> All caller-supplied text (sheet names, cell values, chart titles) is
    ///   XML-escaped and stripped of characters illegal in XML 1.0. Tenant data routinely contains
    ///   <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c> and quotes.</item>
    ///   <item><b>Unicode.</b> Parts are written as UTF-8 without a byte-order mark, and text is stored as
    ///   inline strings, so non-Latin scripts (e.g. Greek <c>Καλημέρα κόσμε</c>) survive the round-trip.</item>
    /// </list>
    ///
    /// The buffering model is deliberately simple: rows and charts are held in memory and the whole
    /// package is rendered on <see cref="Save"/>. That is the right trade-off for report-sized workbooks
    /// (thousands of rows), not for streaming millions of rows.
    /// </summary>
    public class XlsxWriter : IDisposable
    {
        private readonly List<XlsxSheet> _sheets = new List<XlsxSheet>();

        /// <summary>The sheets added to the workbook, in tab order.</summary>
        public IReadOnlyList<XlsxSheet> Sheets => _sheets;

        /// <summary>
        /// Adds a worksheet and returns it for fluent population.
        ///
        /// The name is sanitised to Excel's rules (see <see cref="SanitiseSheetName"/>) rather than
        /// rejected, because sheet names are frequently derived from tenant data (a site title, a report
        /// label) and a hard throw halfway through building a report is a worse outcome than a tidied-up
        /// tab name.
        /// </summary>
        public XlsxSheet AddSheet(string name)
        {
            string safe = SanitiseSheetName(name);
            var sheet = new XlsxSheet(this, safe);
            _sheets.Add(sheet);
            return sheet;
        }

        /// <summary>
        /// Writes the workbook to <paramref name="output"/> as a complete OPC package.
        ///
        /// The caller's stream is intentionally left open (<c>leaveOpen: true</c>) so this can write into
        /// the middle of a larger stream (an HTTP response, a zip entry) without the writer deciding the
        /// stream's lifetime on the caller's behalf.
        /// </summary>
        public void Save(Stream output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            List<KeyValuePair<string, string>> package = BuildPackage();

            // leaveOpen: true - see the summary. The archive's own Dispose still flushes the central
            // directory; it just does not touch the caller's stream afterwards.
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (KeyValuePair<string, string> part in package)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(part.Key, CompressionLevel.Optimal);
                    using (Stream entryStream = entry.Open())
                    // UTF-8 with no BOM: the XML declaration already states the encoding, and a stray BOM
                    // inside an OPC part upsets stricter readers.
                    using (var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                    {
                        writer.Write(part.Value);
                    }
                }
            }
        }

        /// <summary>Renders the workbook to a byte array, e.g. for an <c>HttpResponseMessage</c> payload.</summary>
        public byte[] ToArray()
        {
            using (var ms = new MemoryStream())
            {
                Save(ms);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Provided so the writer can be used in a <c>using</c> block alongside the output stream it feeds.
        /// The writer holds no unmanaged resources; disposing simply releases the buffered model.
        /// </summary>
        public void Dispose()
        {
            _sheets.Clear();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finds a sheet by name, case-insensitively, because Excel treats sheet names case-insensitively
        /// and chart range references (<c>'KPIs'!$A$2</c>) may not match the stored casing exactly.
        /// </summary>
        internal XlsxSheet FindSheet(string name)
        {
            return _sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves a chart range reference (<c>'Sheet'!$A$2:$A$8</c>, <c>A2:B4</c>, <c>C5</c>) to the
        /// underlying cell values, row-major. Used to populate the chart data caches so charts render in
        /// viewers that do not recompute them. An unresolvable reference yields an empty list rather than
        /// throwing - a missing cache degrades to "Excel recomputes on open", which is far better than
        /// failing the whole export.
        /// </summary>
        internal List<object> ResolveRange(string range, XlsxSheet ownerSheet)
        {
            var values = new List<object>();
            if (string.IsNullOrWhiteSpace(range)) return values;

            XlsxSheet target = ownerSheet;
            string cellPart = range.Trim();

            int bang = cellPart.LastIndexOf('!');
            if (bang >= 0)
            {
                string sheetPart = cellPart.Substring(0, bang).Trim();
                cellPart = cellPart.Substring(bang + 1);

                // A quoted sheet name escapes an embedded apostrophe by doubling it, per the formula grammar.
                if (sheetPart.Length >= 2 && sheetPart[0] == '\'' && sheetPart[sheetPart.Length - 1] == '\'')
                {
                    sheetPart = sheetPart.Substring(1, sheetPart.Length - 2).Replace("''", "'");
                }

                target = FindSheet(sheetPart) ?? ownerSheet;
            }

            if (target == null) return values;

            string[] ends = cellPart.Split(':');
            if (!XlsxSheet.TryParseA1(ends[0], out int r1, out int c1)) return values;

            int r2 = r1, c2 = c1;
            if (ends.Length > 1 && !XlsxSheet.TryParseA1(ends[1], out r2, out c2)) return values;

            int rowStart = Math.Min(r1, r2), rowEnd = Math.Max(r1, r2);
            int colStart = Math.Min(c1, c2), colEnd = Math.Max(c1, c2);

            for (int r = rowStart; r <= rowEnd; r++)
            {
                for (int c = colStart; c <= colEnd; c++)
                {
                    target.TryGetCellValue(r, c, out object value);
                    values.Add(value);
                }
            }

            return values;
        }

        /// <summary>
        /// Coerces a requested name into the sheet name Excel will accept: non-empty, at most 31
        /// characters, none of <c>\ / ? * [ ] :</c>, no leading/trailing apostrophe, and unique within the
        /// workbook (case-insensitively). Collisions get a <c> (2)</c>, <c> (3)</c> suffix, trimming the
        /// base first so the result still fits in 31 characters.
        /// </summary>
        private string SanitiseSheetName(string requested)
        {
            var sb = new StringBuilder();
            foreach (char ch in requested ?? string.Empty)
            {
                switch (ch)
                {
                    case '\\':
                    case '/':
                    case '?':
                    case '*':
                    case '[':
                    case ']':
                    case ':':
                        sb.Append(' ');
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }

            string name = sb.ToString().Trim().Trim('\'').Trim();
            if (name.Length == 0) name = "Sheet";

            if (name.Length > 31)
            {
                // Trimming happens again after truncation: cutting at 31 characters can expose an
                // apostrophe that was safely mid-name, and Excel rejects a name that starts or ends
                // with one. Doing this only before the cut leaves that case broken.
                name = name.Substring(0, 31).Trim().Trim('\'').Trim();
                if (name.Length == 0) name = "Sheet";
            }

            if (!IsNameTaken(name)) return name;

            for (int suffix = 2; ; suffix++)
            {
                string tail = " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
                string baseName = name;
                if (baseName.Length + tail.Length > 31)
                {
                    baseName = baseName.Substring(0, 31 - tail.Length);
                }

                string candidate = baseName + tail;
                if (!IsNameTaken(candidate)) return candidate;
            }
        }

        private bool IsNameTaken(string name)
        {
            return _sheets.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Renders every part of the package into an ordered path-&gt;content map. Relationship ids are
        /// allocated here so the workbook, its rels and the content-types manifest all agree: worksheets
        /// take <c>rId1..rIdN</c>, styles the next id. Charts and drawings are numbered globally /
        /// per-sheet as they are discovered.
        /// </summary>
        private List<KeyValuePair<string, string>> BuildPackage()
        {
            var parts = new List<KeyValuePair<string, string>>();
            var overrides = new List<KeyValuePair<string, string>>();

            void AddPart(string path, string content) => parts.Add(new KeyValuePair<string, string>(path, content));
            void AddOverride(string partName, string contentType) => overrides.Add(new KeyValuePair<string, string>(partName, contentType));

            // Fixed package plumbing.
            AddPart("_rels/.rels", BuildRootRels());
            AddPart("docProps/core.xml", BuildCoreProps());
            AddPart("docProps/app.xml", BuildAppProps());
            AddOverride("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml");
            AddOverride("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml");

            AddPart("xl/styles.xml", XlsxStyles.BuildStylesXml());
            AddOverride("/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");

            AddPart("xl/workbook.xml", BuildWorkbookXml());
            AddPart("xl/_rels/workbook.xml.rels", BuildWorkbookRels());
            AddOverride("/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");

            int drawingCounter = 0;
            int chartCounter = 0;

            for (int i = 0; i < _sheets.Count; i++)
            {
                XlsxSheet sheet = _sheets[i];
                int sheetFileIndex = i + 1;
                bool isFirst = i == 0;

                bool hasCharts = sheet.Charts.Count > 0;
                int drawingFileIndex = 0;
                if (hasCharts)
                {
                    drawingFileIndex = ++drawingCounter;
                }

                string worksheetXml = sheet.BuildWorksheetXml(isFirst, hasCharts, "rId1");
                AddPart("xl/worksheets/sheet" + sheetFileIndex + ".xml", worksheetXml);
                AddOverride("/xl/worksheets/sheet" + sheetFileIndex + ".xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");

                if (!hasCharts) continue;

                // The worksheet points at exactly one drawing part; that drawing part hosts an anchor per
                // chart, each anchor pointing at its own chart part via a local relationship id.
                var chartFileIndices = new List<int>();
                foreach (XlsxChart chart in sheet.Charts)
                {
                    int chartFileIndex = ++chartCounter;
                    chartFileIndices.Add(chartFileIndex);

                    Func<string, List<object>> resolver = r => ResolveRange(r, sheet);
                    string chartXml = chart.BuildChartXml(resolver);
                    AddPart("xl/charts/chart" + chartFileIndex + ".xml", chartXml);
                    AddOverride("/xl/charts/chart" + chartFileIndex + ".xml",
                        "application/vnd.openxmlformats-officedocument.drawingml.chart+xml");
                }

                AddPart("xl/worksheets/_rels/sheet" + sheetFileIndex + ".xml.rels",
                    BuildWorksheetRels(drawingFileIndex));

                AddPart("xl/drawings/drawing" + drawingFileIndex + ".xml",
                    XlsxChart.BuildDrawingXml(sheet.Charts));
                AddOverride("/xl/drawings/drawing" + drawingFileIndex + ".xml",
                    "application/vnd.openxmlformats-officedocument.drawing+xml");

                AddPart("xl/drawings/_rels/drawing" + drawingFileIndex + ".xml.rels",
                    BuildDrawingRels(chartFileIndices));
            }

            // Content types first so the manifest can enumerate every override gathered above.
            parts.Insert(0, new KeyValuePair<string, string>("[Content_Types].xml", BuildContentTypes(overrides)));
            return parts;
        }

        private static string BuildContentTypes(List<KeyValuePair<string, string>> overrides)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            foreach (KeyValuePair<string, string> o in overrides)
            {
                sb.Append("<Override PartName=\"").Append(XlsxXml.Attr(o.Key))
                  .Append("\" ContentType=\"").Append(XlsxXml.Attr(o.Value)).Append("\"/>");
            }
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string BuildRootRels()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            sb.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>");
            sb.Append("<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>");
            sb.Append("<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private string BuildWorkbookXml()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ")
              .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            // Every worksheet emits sheetView workbookViewId="0", and SpreadsheetML requires that index
            // to resolve against a declared workbook view. Without this element the reference dangles,
            // which strict consumers treat as a broken document even though the XML is well-formed.
            sb.Append("<bookViews><workbookView activeTab=\"0\"/></bookViews>");
            sb.Append("<sheets>");
            for (int i = 0; i < _sheets.Count; i++)
            {
                sb.Append("<sheet name=\"").Append(XlsxXml.Attr(_sheets[i].Name))
                  .Append("\" sheetId=\"").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" r:id=\"rId").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("\"/>");
            }
            sb.Append("</sheets>");
            sb.Append("</workbook>");
            return sb.ToString();
        }

        private string BuildWorkbookRels()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < _sheets.Count; i++)
            {
                sb.Append("<Relationship Id=\"rId").Append((i + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet")
                  .Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(".xml\"/>");
            }
            sb.Append("<Relationship Id=\"rId").Append((_sheets.Count + 1).ToString(CultureInfo.InvariantCulture))
              .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildWorksheetRels(int drawingFileIndex)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            sb.Append("<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing")
              .Append(drawingFileIndex.ToString(CultureInfo.InvariantCulture)).Append(".xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildDrawingRels(List<int> chartFileIndices)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int k = 0; k < chartFileIndices.Count; k++)
            {
                sb.Append("<Relationship Id=\"rId").Append((k + 1).ToString(CultureInfo.InvariantCulture))
                  .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/chart\" Target=\"../charts/chart")
                  .Append(chartFileIndices[k].ToString(CultureInfo.InvariantCulture)).Append(".xml\"/>");
            }
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildCoreProps()
        {
            // A generation timestamp is standard workbook metadata and is not customer data. It is written
            // in invariant ISO-8601 UTC so the value never depends on the server locale.
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" ")
              .Append("xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" ")
              .Append("xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
            sb.Append("<dc:creator>Microsoft 365 Analytics Insights</dc:creator>");
            sb.Append("<cp:lastModifiedBy>Microsoft 365 Analytics Insights</cp:lastModifiedBy>");
            sb.Append("<dcterms:created xsi:type=\"dcterms:W3CDTF\">").Append(now).Append("</dcterms:created>");
            sb.Append("<dcterms:modified xsi:type=\"dcterms:W3CDTF\">").Append(now).Append("</dcterms:modified>");
            sb.Append("</cp:coreProperties>");
            return sb.ToString();
        }

        private string BuildAppProps()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" ")
              .Append("xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\">");
            sb.Append("<Application>Microsoft 365 Analytics Insights</Application>");
            sb.Append("<DocSecurity>0</DocSecurity>");
            sb.Append("<ScaleCrop>false</ScaleCrop>");
            sb.Append("<HeadingPairs><vt:vector size=\"2\" baseType=\"variant\">");
            sb.Append("<vt:variant><vt:lpstr>Worksheets</vt:lpstr></vt:variant>");
            sb.Append("<vt:variant><vt:i4>").Append(_sheets.Count.ToString(CultureInfo.InvariantCulture)).Append("</vt:i4></vt:variant>");
            sb.Append("</vt:vector></HeadingPairs>");
            sb.Append("<TitlesOfParts><vt:vector size=\"").Append(_sheets.Count.ToString(CultureInfo.InvariantCulture))
              .Append("\" baseType=\"lpstr\">");
            foreach (XlsxSheet sheet in _sheets)
            {
                sb.Append("<vt:lpstr>").Append(XlsxXml.Text(sheet.Name)).Append("</vt:lpstr>");
            }
            sb.Append("</vt:vector></TitlesOfParts>");
            sb.Append("<LinksUpToDate>false</LinksUpToDate>");
            sb.Append("<SharedDoc>false</SharedDoc>");
            sb.Append("<HyperlinksChanged>false</HyperlinksChanged>");
            sb.Append("<AppVersion>16.0300</AppVersion>");
            sb.Append("</Properties>");
            return sb.ToString();
        }
    }

    /// <summary>
    /// The built-in cell formats, exposed as an enum so callers never hand-craft a style index. Each value
    /// is the zero-based position of the corresponding <c>&lt;xf&gt;</c> in <see cref="XlsxStyles"/>'s
    /// <c>cellXfs</c>; the two must stay in lock-step.
    /// </summary>
    public enum XlsxCellStyle
    {
        /// <summary>General format, default font, no fill.</summary>
        Default = 0,
        /// <summary>Bold white text on a solid fill - the standard column-heading look.</summary>
        Header = 1,
        /// <summary>Large bold text for a report title.</summary>
        Title = 2,
        /// <summary>Integer with a thousands separator (<c>#,##0</c>).</summary>
        Thousands = 3,
        /// <summary>Percentage with one decimal place (<c>0.0%</c>); expects a fraction, e.g. 0.42 for 42%.</summary>
        Percent = 4,
        /// <summary>ISO-style date (<c>yyyy-mm-dd</c>).</summary>
        Date = 5,
        /// <summary>Left-aligned text with word wrap on, for long free-text columns.</summary>
        Wrap = 6
    }

    /// <summary>
    /// A single cell value paired with an explicit <see cref="XlsxCellStyle"/>. Rows are normally written
    /// as plain <see cref="object"/> values (auto-styled), but wrapping a value in an <see cref="XlsxCell"/>
    /// lets a caller opt into a specific format for that one cell - a thousands-separated count, a
    /// percentage, a wrapped note - without changing the <c>AddRow(params object[])</c> signature.
    /// </summary>
    public struct XlsxCell
    {
        /// <summary>The underlying value (see <see cref="XlsxSheet.AddRow"/> for the supported types).</summary>
        public object Value { get; }

        /// <summary>The format to render <see cref="Value"/> with.</summary>
        public XlsxCellStyle Style { get; }

        public XlsxCell(object value, XlsxCellStyle style)
        {
            Value = value;
            Style = style;
        }

        /// <summary>An arbitrary value rendered with an explicit style.</summary>
        public static XlsxCell Styled(object value, XlsxCellStyle style) => new XlsxCell(value, style);

        /// <summary>A number rendered with a thousands separator.</summary>
        public static XlsxCell Number(double value) => new XlsxCell(value, XlsxCellStyle.Thousands);

        /// <summary>A fraction rendered as a percentage (0.42 -&gt; 42.0%).</summary>
        public static XlsxCell Percent(double value) => new XlsxCell(value, XlsxCellStyle.Percent);

        /// <summary>A date rendered as <c>yyyy-mm-dd</c>.</summary>
        public static XlsxCell Date(DateTime value) => new XlsxCell(value, XlsxCellStyle.Date);

        /// <summary>Long free text rendered with word wrap.</summary>
        public static XlsxCell Wrapped(string value) => new XlsxCell(value, XlsxCellStyle.Wrap);
    }

    /// <summary>
    /// Emits <c>xl/styles.xml</c>. The set of styles is fixed and small on purpose: this writer targets
    /// reports, not arbitrary spreadsheets, so a closed enum of "the formats a report needs" is far less
    /// error-prone than a general style registry. The indices here are the source of truth that
    /// <see cref="XlsxCellStyle"/> mirrors.
    /// </summary>
    internal static class XlsxStyles
    {
        internal static string BuildStylesXml()
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            // Custom number formats start at 164: ids below that are reserved for Excel's built-ins. Dates
            // are written with an explicit yyyy-mm-dd code rather than a built-in id so the rendering does
            // not depend on the reader's regional settings.
            sb.Append("<numFmts count=\"3\">");
            sb.Append("<numFmt numFmtId=\"164\" formatCode=\"#,##0\"/>");
            sb.Append("<numFmt numFmtId=\"165\" formatCode=\"0.0%\"/>");
            sb.Append("<numFmt numFmtId=\"166\" formatCode=\"yyyy\\-mm\\-dd\"/>");
            sb.Append("</numFmts>");

            sb.Append("<fonts count=\"3\">");
            sb.Append("<font><sz val=\"11\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>");
            sb.Append("<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/><family val=\"2\"/></font>");
            sb.Append("<font><b/><sz val=\"16\"/><color theme=\"1\"/><name val=\"Calibri\"/><family val=\"2\"/></font>");
            sb.Append("</fonts>");

            // Excel reserves fill index 0 (none) and index 1 (gray125); a custom fill must therefore be the
            // third entry or it is silently ignored. The header fill is index 2.
            sb.Append("<fills count=\"3\">");
            sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
            sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");
            sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4472C4\"/><bgColor indexed=\"64\"/></patternFill></fill>");
            sb.Append("</fills>");

            sb.Append("<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>");

            sb.Append("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");

            // The order of these xf entries defines the XlsxCellStyle enum values.
            sb.Append("<cellXfs count=\"7\">");
            sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\"/></xf>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>");
            sb.Append("<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>");
            sb.Append("<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>");
            sb.Append("<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\"/></xf>");
            sb.Append("</cellXfs>");

            sb.Append("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Shared XML text helpers. Centralised so that every dynamic value written into any part goes through
    /// the same escaping and the same invalid-character filter - the single most important safety net when
    /// the source is untrusted tenant data.
    /// </summary>
    internal static class XlsxXml
    {
        /// <summary>Escapes a value for use in element text content.</summary>
        internal static string Text(string value) => Escape(value, forAttribute: false);

        /// <summary>Escapes a value for use inside a double-quoted attribute.</summary>
        internal static string Attr(string value) => Escape(value, forAttribute: true);

        /// <summary>
        /// Escapes XML markup characters and drops characters that are illegal in XML 1.0 (most control
        /// characters). Illegal characters are stripped rather than escaped because there is no escape that
        /// makes them legal - a raw <c>0x01</c> in a value would otherwise corrupt the whole part.
        /// </summary>
        private static string Escape(string value, bool forAttribute)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var sb = new StringBuilder(value.Length + 8);
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append(forAttribute ? "&quot;" : "\""); break;
                    default:
                        if (IsLegalXmlChar(ch)) sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static bool IsLegalXmlChar(char ch)
        {
            // Per the XML 1.0 Char production. Surrogate-range code units are passed through: in a
            // well-formed .NET string they arrive as valid pairs mapping to legal supplementary characters.
            if (ch == '\t' || ch == '\n' || ch == '\r') return true;
            if (ch < 0x20) return false;
            if (ch == '\uFFFE' || ch == '\uFFFF') return false;
            return true;
        }

        /// <summary>Formats a double invariantly so a comma-decimal locale can never corrupt the XML.</summary>
        internal static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>Formats an integer invariantly.</summary>
        internal static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
