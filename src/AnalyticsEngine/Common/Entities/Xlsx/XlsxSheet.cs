using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Common.Entities.Xlsx
{
    /// <summary>
    /// One worksheet in an <see cref="XlsxWriter"/> workbook, plus the fluent API to populate it.
    ///
    /// The whole surface is <b>1-based</b> for rows and columns, matching how Excel presents cells to a
    /// user (A1 is row 1, column 1). This is deliberate: chart ranges and auto-filter bounds are the whole
    /// point of exposing coordinates, and a caller reasoning about "the header is on row 1, data starts on
    /// row 2" should not have to translate to a 0-based model in their head. <see cref="CurrentRow"/> and
    /// the <see cref="RangeReference"/> helpers all speak the same 1-based language.
    ///
    /// Cells are stored densely per row; a value of <c>null</c> still advances the column so columns stay
    /// aligned across rows. The sheet keeps enough structure to answer "what value is in cell (r, c)?"
    /// because chart data caches are resolved from that model at save time.
    /// </summary>
    public class XlsxSheet
    {
        private readonly XlsxWriter _workbook;
        private readonly List<Row> _rows = new List<Row>();
        private readonly List<XlsxChart> _charts = new List<XlsxChart>();
        private double[] _columnWidths;
        private int _freezeRows;
        private AutoFilterRange _autoFilter;

        internal XlsxSheet(XlsxWriter workbook, string name)
        {
            _workbook = workbook;
            Name = name;
        }

        /// <summary>The sanitised, workbook-unique tab name.</summary>
        public string Name { get; }

        /// <summary>
        /// The 1-based index of the most recently written row, or 0 if the sheet is empty. Exposed so a
        /// caller can capture where a block of data starts and ends and hand those coordinates to a chart
        /// or an auto-filter, e.g. <c>int first = sheet.CurrentRow + 1;</c> before writing data.
        /// </summary>
        public int CurrentRow => _rows.Count;

        internal IReadOnlyList<XlsxChart> Charts => _charts;

        /// <summary>
        /// Sets column widths in Excel's width unit (roughly "number of '0' characters at the default
        /// font"). Widths are applied left to right from column 1; pass 0 to leave a column at its default.
        /// </summary>
        public XlsxSheet SetColumnWidths(params double[] widths)
        {
            _columnWidths = widths;
            return this;
        }

        /// <summary>Writes a row of column headings using the bold header style.</summary>
        public XlsxSheet AddHeaderRow(params string[] headers)
        {
            var row = new Row(NextRowIndex);
            int col = 1;
            foreach (string header in headers ?? Array.Empty<string>())
            {
                row.Cells.Add(new Cell(col, header ?? string.Empty, XlsxCellStyle.Header));
                col++;
            }
            _rows.Add(row);
            return this;
        }

        /// <summary>
        /// Writes a row of values. Supported element types are <see cref="string"/>, the integer types,
        /// <see cref="double"/>/<see cref="float"/>/<see cref="decimal"/>, <see cref="bool"/>,
        /// <see cref="DateTime"/>/<see cref="DateTimeOffset"/> and <c>null</c> (an empty cell). Any other
        /// type is rendered via its invariant string form.
        ///
        /// An element may also be an <see cref="XlsxCell"/> to force a specific format for that one cell
        /// (a thousands separator, a percentage, wrapped text) - this is why the parameter is
        /// <c>object[]</c> rather than a stricter type: it keeps one obvious call shape while still allowing
        /// per-cell styling. Dates without an explicit style are given the date format automatically, since
        /// a bare date serial rendered as a number is never what the caller meant.
        /// </summary>
        public XlsxSheet AddRow(params object[] values)
        {
            var row = new Row(NextRowIndex);
            int col = 1;
            foreach (object value in values ?? Array.Empty<object>())
            {
                if (value is XlsxCell styled)
                {
                    row.Cells.Add(new Cell(col, styled.Value, styled.Style));
                }
                else
                {
                    XlsxCellStyle style = (value is DateTime || value is DateTimeOffset)
                        ? XlsxCellStyle.Date
                        : XlsxCellStyle.Default;
                    row.Cells.Add(new Cell(col, value, style));
                }
                col++;
            }
            _rows.Add(row);
            return this;
        }

        /// <summary>
        /// Writes a large bold title row. The title cell is merged across the full used width of the sheet
        /// (computed at save time) so it reads as a banner rather than a lone value in column A.
        /// </summary>
        public XlsxSheet AddTitle(string text)
        {
            var row = new Row(NextRowIndex) { MergeAcrossUsedWidth = true };
            row.Cells.Add(new Cell(1, text ?? string.Empty, XlsxCellStyle.Title));
            _rows.Add(row);
            return this;
        }

        /// <summary>Writes an empty row, e.g. to separate a title from a table or two tables from each other.</summary>
        public XlsxSheet AddBlankRow()
        {
            _rows.Add(new Row(NextRowIndex));
            return this;
        }

        /// <summary>
        /// Anchors a native, still-editable Excel chart on this sheet. The chart references cell ranges
        /// (its own or another sheet's), so it stays live when the user opens the workbook rather than
        /// being flattened to a picture.
        /// </summary>
        public XlsxSheet AddChart(XlsxChart chart)
        {
            if (chart == null) throw new ArgumentNullException(nameof(chart));
            _charts.Add(chart);
            return this;
        }

        /// <summary>
        /// Freezes the top <paramref name="rows"/> rows so they stay visible while the user scrolls -
        /// almost always used as <c>FreezeTopRows(1)</c> to pin a header row.
        /// </summary>
        public void FreezeTopRows(int rows)
        {
            _freezeRows = Math.Max(0, rows);
        }

        /// <summary>
        /// Adds an auto-filter over the given 1-based, inclusive cell rectangle. The filter drop-downs
        /// appear on <paramref name="firstRow"/>; pass the full data range (header plus body) as Excel
        /// expects the filtered region, not just the header, in the reference.
        /// </summary>
        public void AddAutoFilter(int firstRow, int lastRow, int firstCol, int lastCol)
        {
            _autoFilter = new AutoFilterRange(firstRow, lastRow, firstCol, lastCol);
        }

        /// <summary>An unqualified A1-style reference for a 1-based cell, e.g. <c>(2, 3)</c> -&gt; <c>C2</c>.</summary>
        public string CellReference(int row, int col)
        {
            return ColumnName(col) + row.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A fully-qualified, absolute range reference suitable for a chart series, e.g.
        /// <c>'KPIs'!$A$2:$B$8</c>. The sheet name is quoted and its apostrophes doubled, so a sheet called
        /// <c>Bob's data</c> still produces a valid formula reference.
        /// </summary>
        public string RangeReference(int firstRow, int firstCol, int lastRow, int lastCol)
        {
            string sheetRef = "'" + Name.Replace("'", "''") + "'";
            string start = "$" + ColumnName(firstCol) + "$" + firstRow.ToString(CultureInfo.InvariantCulture);
            string end = "$" + ColumnName(lastCol) + "$" + lastRow.ToString(CultureInfo.InvariantCulture);
            return sheetRef + "!" + start + ":" + end;
        }

        /// <summary>Converts a 1-based column index into its letters, e.g. 1 -&gt; A, 27 -&gt; AA.</summary>
        public static string ColumnName(int col)
        {
            if (col < 1) col = 1;
            var sb = new StringBuilder();
            while (col > 0)
            {
                int rem = (col - 1) % 26;
                sb.Insert(0, (char)('A' + rem));
                col = (col - 1) / 26;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Parses an A1 cell token (<c>$B$4</c>, <c>B4</c>) into 1-based row and column. Dollar signs are
        /// ignored; the token must be letters followed by digits or it is rejected.
        /// </summary>
        internal static bool TryParseA1(string token, out int row, out int col)
        {
            row = 0;
            col = 0;
            if (string.IsNullOrEmpty(token)) return false;

            int i = 0;
            int colValue = 0;
            bool sawLetter = false;
            for (; i < token.Length; i++)
            {
                char ch = token[i];
                if (ch == '$') continue;
                char upper = char.ToUpperInvariant(ch);
                if (upper < 'A' || upper > 'Z') break;
                colValue = colValue * 26 + (upper - 'A' + 1);
                sawLetter = true;
            }

            if (!sawLetter) return false;

            int rowValue = 0;
            bool sawDigit = false;
            for (; i < token.Length; i++)
            {
                char ch = token[i];
                if (ch == '$') continue;
                if (ch < '0' || ch > '9') return false;
                rowValue = rowValue * 10 + (ch - '0');
                sawDigit = true;
            }

            if (!sawDigit) return false;

            row = rowValue;
            col = colValue;
            return true;
        }

        /// <summary>
        /// Reads a value from the stored grid by 1-based coordinates, for chart-cache resolution. Returns
        /// false (with a null value) for any cell that was never written, which the caller treats as a gap.
        /// </summary>
        internal bool TryGetCellValue(int row, int col, out object value)
        {
            value = null;
            if (row < 1 || row > _rows.Count) return false;

            Row storedRow = _rows[row - 1];
            foreach (Cell cell in storedRow.Cells)
            {
                if (cell.Column == col)
                {
                    value = cell.Value;
                    return value != null;
                }
            }
            return false;
        }

        private int NextRowIndex => _rows.Count + 1;

        /// <summary>
        /// Renders this sheet as a worksheet part. The child elements are emitted in the exact order the
        /// worksheet schema (CT_Worksheet) demands - dimension, sheetViews, sheetFormatPr, cols, sheetData,
        /// autoFilter, mergeCells, then finally the drawing reference - because Excel rejects a worksheet
        /// whose children are out of sequence even when every element is individually valid.
        /// </summary>
        internal string BuildWorksheetXml(bool isFirstSheet, bool hasDrawing, string drawingRelId)
        {
            int usedColumns = ComputeUsedColumnCount();
            int usedRows = _rows.Count;

            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ")
              .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");

            string lastCell = ColumnName(Math.Max(usedColumns, 1)) + Math.Max(usedRows, 1).ToString(CultureInfo.InvariantCulture);
            sb.Append("<dimension ref=\"A1:").Append(lastCell).Append("\"/>");

            AppendSheetViews(sb, isFirstSheet);

            sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");

            AppendColumns(sb);

            AppendSheetData(sb);

            if (_autoFilter != null)
            {
                sb.Append("<autoFilter ref=\"")
                  .Append(_autoFilter.ToReference())
                  .Append("\"/>");
            }

            AppendMergeCells(sb, usedColumns);

            if (hasDrawing)
            {
                sb.Append("<drawing r:id=\"").Append(XlsxXml.Attr(drawingRelId)).Append("\"/>");
            }

            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private void AppendSheetViews(StringBuilder sb, bool isFirstSheet)
        {
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"");
            if (isFirstSheet) sb.Append(" tabSelected=\"1\"");
            sb.Append(">");

            if (_freezeRows > 0)
            {
                string topLeft = "A" + (_freezeRows + 1).ToString(CultureInfo.InvariantCulture);
                sb.Append("<pane ySplit=\"").Append(XlsxXml.Int(_freezeRows))
                  .Append("\" topLeftCell=\"").Append(topLeft)
                  .Append("\" activePane=\"bottomLeft\" state=\"frozen\"/>");
                sb.Append("<selection pane=\"bottomLeft\" activeCell=\"").Append(topLeft)
                  .Append("\" sqref=\"").Append(topLeft).Append("\"/>");
            }

            sb.Append("</sheetView></sheetViews>");
        }

        private void AppendColumns(StringBuilder sb)
        {
            if (_columnWidths == null || _columnWidths.Length == 0) return;

            sb.Append("<cols>");
            for (int i = 0; i < _columnWidths.Length; i++)
            {
                double width = _columnWidths[i];
                if (width <= 0) continue;
                int columnIndex = i + 1;
                sb.Append("<col min=\"").Append(XlsxXml.Int(columnIndex))
                  .Append("\" max=\"").Append(XlsxXml.Int(columnIndex))
                  .Append("\" width=\"").Append(XlsxXml.Num(width))
                  .Append("\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
        }

        private void AppendSheetData(StringBuilder sb)
        {
            sb.Append("<sheetData>");
            foreach (Row row in _rows)
            {
                if (row.Cells.Count == 0)
                {
                    sb.Append("<row r=\"").Append(XlsxXml.Int(row.Index)).Append("\"/>");
                    continue;
                }

                sb.Append("<row r=\"").Append(XlsxXml.Int(row.Index)).Append("\">");
                foreach (Cell cell in row.Cells)
                {
                    AppendCell(sb, cell, row.Index);
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
        }

        /// <summary>
        /// Writes one <c>&lt;c&gt;</c> element. Strings are emitted as inline strings (<c>t="inlineStr"</c>)
        /// rather than via a shared-string table: it costs a few bytes on repeated text but removes an
        /// entire part and its bookkeeping, and it is fully valid OOXML. Dates are written as their Excel
        /// serial (via <see cref="DateTime.ToOADate"/>, which already accounts for the 1900 leap-year
        /// quirk) with the date number format, so they display as dates rather than five-digit numbers.
        /// </summary>
        private static void AppendCell(StringBuilder sb, Cell cell, int rowIndex)
        {
            object value = cell.Value;
            if (value is XlsxCell nested)
            {
                // Defensive: an XlsxCell handed in as a plain value should still round-trip.
                value = nested.Value;
            }

            if (value == null) return;

            string reference = ColumnName(cell.Column) + rowIndex.ToString(CultureInfo.InvariantCulture);
            XlsxCellStyle style = cell.Style;

            // A stray date with no explicit style would render as a number; force the date format.
            if ((value is DateTime || value is DateTimeOffset) && style == XlsxCellStyle.Default)
            {
                style = XlsxCellStyle.Date;
            }

            string styleAttr = style == XlsxCellStyle.Default
                ? string.Empty
                : " s=\"" + ((int)style).ToString(CultureInfo.InvariantCulture) + "\"";

            switch (value)
            {
                case bool b:
                    sb.Append("<c r=\"").Append(reference).Append("\"").Append(styleAttr)
                      .Append(" t=\"b\"><v>").Append(b ? "1" : "0").Append("</v></c>");
                    break;

                case string s:
                    sb.Append("<c r=\"").Append(reference).Append("\"").Append(styleAttr)
                      .Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(XlsxXml.Text(s)).Append("</t></is></c>");
                    break;

                case DateTime dt:
                    AppendNumericCell(sb, reference, styleAttr, dt.ToOADate());
                    break;

                case DateTimeOffset dto:
                    AppendNumericCell(sb, reference, styleAttr, dto.DateTime.ToOADate());
                    break;

                case sbyte _:
                case byte _:
                case short _:
                case ushort _:
                case int _:
                case uint _:
                case long _:
                case ulong _:
                    sb.Append("<c r=\"").Append(reference).Append("\"").Append(styleAttr)
                      .Append("><v>").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("</v></c>");
                    break;

                case float f:
                    AppendNumericCell(sb, reference, styleAttr, f);
                    break;

                case double d:
                    AppendNumericCell(sb, reference, styleAttr, d);
                    break;

                case decimal m:
                    sb.Append("<c r=\"").Append(reference).Append("\"").Append(styleAttr)
                      .Append("><v>").Append(m.ToString(CultureInfo.InvariantCulture)).Append("</v></c>");
                    break;

                default:
                    // Guids, enums, anything else: render as text so nothing is silently lost. IFormattable
                    // types (rare here) still get invariant formatting.
                    string text = value is IFormattable formattable
                        ? formattable.ToString(null, CultureInfo.InvariantCulture)
                        : value.ToString();
                    sb.Append("<c r=\"").Append(reference).Append("\"").Append(styleAttr)
                      .Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(XlsxXml.Text(text)).Append("</t></is></c>");
                    break;
            }
        }

        private static void AppendNumericCell(StringBuilder sb, string reference, string styleAttr, double value)
        {
            sb.Append("<c r=\"").Append(reference).Append("\"").Append(styleAttr)
              .Append("><v>").Append(XlsxXml.Num(value)).Append("</v></c>");
        }

        private void AppendMergeCells(StringBuilder sb, int usedColumns)
        {
            var merges = new List<string>();
            foreach (Row row in _rows)
            {
                if (row.MergeAcrossUsedWidth && usedColumns > 1)
                {
                    string reference = "A" + row.Index.ToString(CultureInfo.InvariantCulture)
                        + ":" + ColumnName(usedColumns) + row.Index.ToString(CultureInfo.InvariantCulture);
                    merges.Add(reference);
                }
            }

            if (merges.Count == 0) return;

            sb.Append("<mergeCells count=\"").Append(XlsxXml.Int(merges.Count)).Append("\">");
            foreach (string reference in merges)
            {
                sb.Append("<mergeCell ref=\"").Append(reference).Append("\"/>");
            }
            sb.Append("</mergeCells>");
        }

        private int ComputeUsedColumnCount()
        {
            int max = 0;
            foreach (Row row in _rows)
            {
                foreach (Cell cell in row.Cells)
                {
                    if (cell.Column > max) max = cell.Column;
                }
            }
            if (_columnWidths != null && _columnWidths.Length > max)
            {
                max = _columnWidths.Length;
            }
            return max;
        }

        /// <summary>One stored row: its 1-based index and its cells, plus whether it is a merged title banner.</summary>
        private sealed class Row
        {
            internal Row(int index)
            {
                Index = index;
            }

            internal int Index { get; }
            internal List<Cell> Cells { get; } = new List<Cell>();
            internal bool MergeAcrossUsedWidth { get; set; }
        }

        /// <summary>One stored cell: its 1-based column, its value and the style to render it with.</summary>
        private sealed class Cell
        {
            internal Cell(int column, object value, XlsxCellStyle style)
            {
                Column = column;
                Value = value;
                Style = style;
            }

            internal int Column { get; }
            internal object Value { get; }
            internal XlsxCellStyle Style { get; }
        }

        /// <summary>An auto-filter's 1-based inclusive bounds, and its A1 reference.</summary>
        private sealed class AutoFilterRange
        {
            private readonly int _firstRow;
            private readonly int _lastRow;
            private readonly int _firstCol;
            private readonly int _lastCol;

            internal AutoFilterRange(int firstRow, int lastRow, int firstCol, int lastCol)
            {
                _firstRow = Math.Min(firstRow, lastRow);
                _lastRow = Math.Max(firstRow, lastRow);
                _firstCol = Math.Min(firstCol, lastCol);
                _lastCol = Math.Max(firstCol, lastCol);
            }

            internal string ToReference()
            {
                return ColumnName(_firstCol) + _firstRow.ToString(CultureInfo.InvariantCulture)
                    + ":" + ColumnName(_lastCol) + _lastRow.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
