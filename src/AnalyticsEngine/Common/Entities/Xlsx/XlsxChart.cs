using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Common.Entities.Xlsx
{
    /// <summary>The chart shapes this writer can emit. Each maps to a specific DrawingML chart element.</summary>
    public enum XlsxChartType
    {
        /// <summary>Vertical clustered bars.</summary>
        Column,
        /// <summary>Vertical bars stacked per category.</summary>
        StackedColumn,
        /// <summary>Horizontal clustered bars.</summary>
        Bar,
        /// <summary>Line with markers.</summary>
        Line,
        /// <summary>Single-series pie.</summary>
        Pie,
        /// <summary>Single-series doughnut (pie with a hole).</summary>
        Doughnut,
        /// <summary>Filled area.</summary>
        Area,
        /// <summary>Filled area stacked per category.</summary>
        StackedArea,
        /// <summary>X/Y scatter: the category range supplies X values, each series' range the Y values.</summary>
        Scatter
    }

    /// <summary>
    /// One plotted series: a name and the range of values to plot. A series references cells rather than
    /// holding literal numbers so the chart stays live - editing the source cells in Excel re-plots it.
    /// </summary>
    public class XlsxChartSeries
    {
        public XlsxChartSeries()
        {
        }

        public XlsxChartSeries(string name, string valueRange)
        {
            Name = name;
            ValueRange = valueRange;
        }

        /// <summary>
        /// The literal series label. Used directly when <see cref="NameRange"/> is not set, and as the
        /// cached display value when it is.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Optional cell reference whose value is the series name, e.g. <c>'KPIs'!$B$1</c>. When set, the
        /// name stays linked to that cell; <see cref="Name"/> is used only to seed the cache.
        /// </summary>
        public string NameRange { get; set; }

        /// <summary>
        /// The range of numeric values to plot, e.g. <c>'KPIs'!$B$2:$B$8</c>. For a scatter chart these are
        /// the Y values.
        /// </summary>
        public string ValueRange { get; set; }
    }

    /// <summary>
    /// Describes a native, still-editable Excel chart bound to worksheet ranges, rather than a rendered
    /// picture. Populate it and hand it to <see cref="XlsxSheet.AddChart"/>.
    ///
    /// The value caches Excel stores inside a chart are populated from the referenced ranges at save time
    /// (see <see cref="BuildChartXml"/>). Strictly, Excel recomputes them on open and could do without
    /// them - but several other spreadsheet viewers render a chart <i>blank</i> when the cache is missing,
    /// so the caches are always written. Because the caches are derived from the live ranges the chart is
    /// still fully editable: change a source cell and Excel re-plots it.
    ///
    /// Sizing is expressed in whole cells (<see cref="WidthCells"/> / <see cref="HeightCells"/>) rather
    /// than EMUs: report authors think in "about eight columns wide", and a cell-anchored size follows the
    /// sheet's own column widths instead of guessing a pixel-to-EMU conversion.
    /// </summary>
    public class XlsxChart
    {
        // These axis ids only need to be unique within a single chart part, and every chart is its own
        // chartSpace, so a fixed pair is safe and keeps the cross-references trivially correct.
        private const string AxIdPrimary = "111111111";
        private const string AxIdSecondary = "222222222";

        /// <summary>The chart shape.</summary>
        public XlsxChartType Type { get; set; }

        /// <summary>The chart title. When null or empty the chart is emitted with no title.</summary>
        public string Title { get; set; }

        /// <summary>
        /// The category (X) range shared by every series, e.g. <c>'KPIs'!$A$2:$A$8</c>. For scatter charts
        /// this supplies the X values.
        /// </summary>
        public string CategoryRange { get; set; }

        /// <summary>The plotted series. Pie and doughnut charts use only the first.</summary>
        public List<XlsxChartSeries> Series { get; set; } = new List<XlsxChartSeries>();

        /// <summary>The top-left cell the chart is anchored to, e.g. <c>E2</c>. Defaults to <c>A1</c>.</summary>
        public string AnchorCell { get; set; } = "A1";

        /// <summary>Chart width in whole columns.</summary>
        public int WidthCells { get; set; } = 8;

        /// <summary>Chart height in whole rows.</summary>
        public int HeightCells { get; set; } = 15;

        /// <summary>Whether to label each data point with its value.</summary>
        public bool ShowDataLabels { get; set; }

        /// <summary>Whether to show the legend (below the plot). Defaults to true.</summary>
        public bool ShowLegend { get; set; } = true;

        /// <summary>Fluent helper to append a series from a name and a value range.</summary>
        public XlsxChart AddSeries(string name, string valueRange)
        {
            Series.Add(new XlsxChartSeries(name, valueRange));
            return this;
        }

        /// <summary>
        /// Renders the drawing part that hosts every chart anchored on one sheet. A sheet has a single
        /// drawing part; each chart becomes a <c>twoCellAnchor</c> inside it, referencing its own chart
        /// part by a local relationship id (<c>rId1</c>, <c>rId2</c>, ... in anchor order).
        /// </summary>
        internal static string BuildDrawingXml(IReadOnlyList<XlsxChart> charts)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" ")
              .Append("xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">");
            for (int i = 0; i < charts.Count; i++)
            {
                charts[i].AppendAnchor(sb, i);
            }
            sb.Append("</xdr:wsDr>");
            return sb.ToString();
        }

        /// <summary>Writes one chart's anchor. <paramref name="ordinal"/> is its 0-based position on the sheet.</summary>
        private void AppendAnchor(StringBuilder sb, int ordinal)
        {
            ParseAnchor(AnchorCell, out int fromCol, out int fromRow);
            int toCol = fromCol + Math.Max(1, WidthCells);
            int toRow = fromRow + Math.Max(1, HeightCells);
            string relId = "rId" + XlsxXml.Int(ordinal + 1);
            // cNvPr ids must be unique and non-zero within the drawing; start at 2.
            string frameId = XlsxXml.Int(ordinal + 2);

            // editAs="oneCell" keeps the chart the same size when columns are resized but lets it move with
            // its anchor cell - the least surprising behaviour for a report chart.
            sb.Append("<xdr:twoCellAnchor editAs=\"oneCell\">");
            sb.Append("<xdr:from><xdr:col>").Append(XlsxXml.Int(fromCol)).Append("</xdr:col><xdr:colOff>0</xdr:colOff>")
              .Append("<xdr:row>").Append(XlsxXml.Int(fromRow)).Append("</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>");
            sb.Append("<xdr:to><xdr:col>").Append(XlsxXml.Int(toCol)).Append("</xdr:col><xdr:colOff>0</xdr:colOff>")
              .Append("<xdr:row>").Append(XlsxXml.Int(toRow)).Append("</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>");
            sb.Append("<xdr:graphicFrame macro=\"\">");
            sb.Append("<xdr:nvGraphicFramePr>");
            sb.Append("<xdr:cNvPr id=\"").Append(frameId).Append("\" name=\"Chart ").Append(XlsxXml.Int(ordinal + 1)).Append("\"/>");
            sb.Append("<xdr:cNvGraphicFramePr/>");
            sb.Append("</xdr:nvGraphicFramePr>");
            sb.Append("<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm>");
            sb.Append("<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">");
            sb.Append("<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" ")
              .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"")
              .Append(XlsxXml.Attr(relId)).Append("\"/>");
            sb.Append("</a:graphicData></a:graphic>");
            sb.Append("</xdr:graphicFrame>");
            sb.Append("<xdr:clientData/>");
            sb.Append("</xdr:twoCellAnchor>");
        }

        /// <summary>
        /// Renders the chart part. <paramref name="resolve"/> maps a range reference to its current cell
        /// values so the data caches can be populated. Child elements follow the strict order the chart
        /// schema (CT_Chart / CT_PlotArea) demands - title, plot area (plot element then axes), legend -
        /// because Excel rejects an out-of-order chart even when each element is valid on its own.
        /// </summary>
        internal string BuildChartXml(Func<string, List<object>> resolve)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" ")
              .Append("xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" ")
              .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<c:chart>");

            AppendTitle(sb);

            sb.Append("<c:plotArea><c:layout/>");
            AppendPlotElement(sb, resolve);
            AppendAxes(sb);
            sb.Append("</c:plotArea>");

            AppendLegend(sb);

            sb.Append("<c:plotVisOnly val=\"1\"/>");
            sb.Append("<c:dispBlanksAs val=\"gap\"/>");

            sb.Append("</c:chart>");
            sb.Append("</c:chartSpace>");
            return sb.ToString();
        }

        private void AppendTitle(StringBuilder sb)
        {
            if (string.IsNullOrEmpty(Title))
            {
                sb.Append("<c:autoTitleDeleted val=\"1\"/>");
                return;
            }

            sb.Append("<c:title><c:tx><c:rich>");
            sb.Append("<a:bodyPr rot=\"0\" spcFirstLastPara=\"1\" vertOverflow=\"ellipsis\" vert=\"horz\" wrap=\"square\" anchor=\"ctr\" anchorCtr=\"1\"/>");
            sb.Append("<a:lstStyle/>");
            sb.Append("<a:p><a:pPr><a:defRPr/></a:pPr><a:r><a:rPr lang=\"en-US\"/><a:t>")
              .Append(XlsxXml.Text(Title)).Append("</a:t></a:r></a:p>");
            sb.Append("</c:rich></c:tx><c:overlay val=\"0\"/></c:title>");
            sb.Append("<c:autoTitleDeleted val=\"0\"/>");
        }

        private void AppendPlotElement(StringBuilder sb, Func<string, List<object>> resolve)
        {
            switch (Type)
            {
                case XlsxChartType.Column:
                case XlsxChartType.StackedColumn:
                case XlsxChartType.Bar:
                    AppendBarChart(sb, resolve);
                    break;
                case XlsxChartType.Line:
                    AppendLineChart(sb, resolve);
                    break;
                case XlsxChartType.Area:
                case XlsxChartType.StackedArea:
                    AppendAreaChart(sb, resolve);
                    break;
                case XlsxChartType.Pie:
                    AppendPieChart(sb, resolve);
                    break;
                case XlsxChartType.Doughnut:
                    AppendDoughnutChart(sb, resolve);
                    break;
                case XlsxChartType.Scatter:
                    AppendScatterChart(sb, resolve);
                    break;
            }
        }

        private void AppendBarChart(StringBuilder sb, Func<string, List<object>> resolve)
        {
            bool stacked = Type == XlsxChartType.StackedColumn;
            string barDir = Type == XlsxChartType.Bar ? "bar" : "col";
            string grouping = stacked ? "stacked" : "clustered";

            sb.Append("<c:barChart>");
            sb.Append("<c:barDir val=\"").Append(barDir).Append("\"/>");
            sb.Append("<c:grouping val=\"").Append(grouping).Append("\"/>");
            sb.Append("<c:varyColors val=\"0\"/>");
            AppendCategoryValueSeries(sb, resolve, includeSmooth: false);
            AppendDataLabels(sb);
            sb.Append("<c:gapWidth val=\"150\"/>");
            if (stacked) sb.Append("<c:overlap val=\"100\"/>");
            AppendAxisIds(sb);
            sb.Append("</c:barChart>");
        }

        private void AppendLineChart(StringBuilder sb, Func<string, List<object>> resolve)
        {
            sb.Append("<c:lineChart>");
            sb.Append("<c:grouping val=\"standard\"/>");
            sb.Append("<c:varyColors val=\"0\"/>");
            AppendCategoryValueSeries(sb, resolve, includeSmooth: true);
            AppendDataLabels(sb);
            sb.Append("<c:marker val=\"1\"/>");
            AppendAxisIds(sb);
            sb.Append("</c:lineChart>");
        }

        private void AppendAreaChart(StringBuilder sb, Func<string, List<object>> resolve)
        {
            bool stacked = Type == XlsxChartType.StackedArea;
            sb.Append("<c:areaChart>");
            sb.Append("<c:grouping val=\"").Append(stacked ? "stacked" : "standard").Append("\"/>");
            sb.Append("<c:varyColors val=\"0\"/>");
            AppendCategoryValueSeries(sb, resolve, includeSmooth: false);
            AppendDataLabels(sb);
            AppendAxisIds(sb);
            sb.Append("</c:areaChart>");
        }

        private void AppendPieChart(StringBuilder sb, Func<string, List<object>> resolve)
        {
            sb.Append("<c:pieChart>");
            sb.Append("<c:varyColors val=\"1\"/>");
            AppendCategoryValueSeries(sb, resolve, includeSmooth: false);
            AppendDataLabels(sb);
            sb.Append("<c:firstSliceAng val=\"0\"/>");
            sb.Append("</c:pieChart>");
        }

        private void AppendDoughnutChart(StringBuilder sb, Func<string, List<object>> resolve)
        {
            sb.Append("<c:doughnutChart>");
            sb.Append("<c:varyColors val=\"1\"/>");
            AppendCategoryValueSeries(sb, resolve, includeSmooth: false);
            AppendDataLabels(sb);
            sb.Append("<c:firstSliceAng val=\"0\"/>");
            sb.Append("<c:holeSize val=\"50\"/>");
            sb.Append("</c:doughnutChart>");
        }

        private void AppendScatterChart(StringBuilder sb, Func<string, List<object>> resolve)
        {
            sb.Append("<c:scatterChart>");
            sb.Append("<c:scatterStyle val=\"lineMarker\"/>");
            sb.Append("<c:varyColors val=\"0\"/>");
            for (int i = 0; i < Series.Count; i++)
            {
                XlsxChartSeries series = Series[i];
                sb.Append("<c:ser>");
                sb.Append("<c:idx val=\"").Append(XlsxXml.Int(i)).Append("\"/>");
                sb.Append("<c:order val=\"").Append(XlsxXml.Int(i)).Append("\"/>");
                AppendSeriesName(sb, series, resolve);
                sb.Append("<c:xVal>");
                AppendNumRef(sb, CategoryRange, resolve(CategoryRange));
                sb.Append("</c:xVal>");
                sb.Append("<c:yVal>");
                AppendNumRef(sb, series.ValueRange, resolve(series.ValueRange));
                sb.Append("</c:yVal>");
                sb.Append("<c:smooth val=\"0\"/>");
                sb.Append("</c:ser>");
            }
            AppendDataLabels(sb);
            AppendAxisIds(sb);
            sb.Append("</c:scatterChart>");
        }

        /// <summary>
        /// Writes the series shared by the bar, line, area, pie and doughnut shapes: a name, the shared
        /// category range (as a string reference - categories are labels) and the series' own numeric value
        /// range. Line series additionally carry a <c>smooth</c> flag.
        /// </summary>
        private void AppendCategoryValueSeries(StringBuilder sb, Func<string, List<object>> resolve, bool includeSmooth)
        {
            for (int i = 0; i < Series.Count; i++)
            {
                XlsxChartSeries series = Series[i];
                sb.Append("<c:ser>");
                sb.Append("<c:idx val=\"").Append(XlsxXml.Int(i)).Append("\"/>");
                sb.Append("<c:order val=\"").Append(XlsxXml.Int(i)).Append("\"/>");
                AppendSeriesName(sb, series, resolve);

                if (!string.IsNullOrEmpty(CategoryRange))
                {
                    sb.Append("<c:cat>");
                    AppendStrRef(sb, CategoryRange, resolve(CategoryRange));
                    sb.Append("</c:cat>");
                }

                sb.Append("<c:val>");
                AppendNumRef(sb, series.ValueRange, resolve(series.ValueRange));
                sb.Append("</c:val>");

                if (includeSmooth) sb.Append("<c:smooth val=\"0\"/>");
                sb.Append("</c:ser>");
            }
        }

        private static void AppendSeriesName(StringBuilder sb, XlsxChartSeries series, Func<string, List<object>> resolve)
        {
            if (!string.IsNullOrEmpty(series.NameRange))
            {
                sb.Append("<c:tx>");
                List<object> resolved = resolve(series.NameRange);
                var cache = new List<object>(1);
                if (resolved.Count > 0 && resolved[0] != null) cache.Add(resolved[0]);
                else cache.Add(series.Name);
                AppendStrRef(sb, series.NameRange, cache);
                sb.Append("</c:tx>");
            }
            else if (!string.IsNullOrEmpty(series.Name))
            {
                sb.Append("<c:tx><c:v>").Append(XlsxXml.Text(series.Name)).Append("</c:v></c:tx>");
            }
        }

        private void AppendAxisIds(StringBuilder sb)
        {
            sb.Append("<c:axId val=\"").Append(AxIdPrimary).Append("\"/>");
            sb.Append("<c:axId val=\"").Append(AxIdSecondary).Append("\"/>");
        }

        private void AppendAxes(StringBuilder sb)
        {
            // Pie and doughnut have no axes.
            if (Type == XlsxChartType.Pie || Type == XlsxChartType.Doughnut) return;

            if (Type == XlsxChartType.Scatter)
            {
                // Two value axes: X along the bottom, Y up the left.
                AppendValueAxis(sb, AxIdPrimary, "b", AxIdSecondary);
                AppendValueAxis(sb, AxIdSecondary, "l", AxIdPrimary);
                return;
            }

            // A horizontal bar chart swaps which edge each axis sits on relative to a column/line/area chart.
            bool horizontal = Type == XlsxChartType.Bar;
            string categoryPos = horizontal ? "l" : "b";
            string valuePos = horizontal ? "b" : "l";
            AppendCategoryAxis(sb, AxIdPrimary, categoryPos, AxIdSecondary);
            AppendValueAxis(sb, AxIdSecondary, valuePos, AxIdPrimary);
        }

        private static void AppendCategoryAxis(StringBuilder sb, string axId, string position, string crossAxId)
        {
            sb.Append("<c:catAx>");
            sb.Append("<c:axId val=\"").Append(axId).Append("\"/>");
            sb.Append("<c:scaling><c:orientation val=\"minMax\"/></c:scaling>");
            sb.Append("<c:delete val=\"0\"/>");
            sb.Append("<c:axPos val=\"").Append(position).Append("\"/>");
            sb.Append("<c:crossAx val=\"").Append(crossAxId).Append("\"/>");
            sb.Append("</c:catAx>");
        }

        private static void AppendValueAxis(StringBuilder sb, string axId, string position, string crossAxId)
        {
            sb.Append("<c:valAx>");
            sb.Append("<c:axId val=\"").Append(axId).Append("\"/>");
            sb.Append("<c:scaling><c:orientation val=\"minMax\"/></c:scaling>");
            sb.Append("<c:delete val=\"0\"/>");
            sb.Append("<c:axPos val=\"").Append(position).Append("\"/>");
            sb.Append("<c:crossAx val=\"").Append(crossAxId).Append("\"/>");
            sb.Append("</c:valAx>");
        }

        private void AppendLegend(StringBuilder sb)
        {
            if (!ShowLegend) return;
            sb.Append("<c:legend><c:legendPos val=\"b\"/><c:overlay val=\"0\"/></c:legend>");
        }

        private void AppendDataLabels(StringBuilder sb)
        {
            if (!ShowDataLabels) return;
            sb.Append("<c:dLbls>");
            sb.Append("<c:showLegendKey val=\"0\"/>");
            sb.Append("<c:showVal val=\"1\"/>");
            sb.Append("<c:showCatName val=\"0\"/>");
            sb.Append("<c:showSerName val=\"0\"/>");
            sb.Append("<c:showPercent val=\"0\"/>");
            sb.Append("<c:showBubbleSize val=\"0\"/>");
            sb.Append("</c:dLbls>");
        }

        /// <summary>
        /// Writes a string reference and its cache. Used for categories and cell-linked series names, where
        /// the plotted labels are text.
        /// </summary>
        private static void AppendStrRef(StringBuilder sb, string formula, List<object> values)
        {
            sb.Append("<c:strRef><c:f>").Append(XlsxXml.Text(formula ?? string.Empty)).Append("</c:f>");
            sb.Append("<c:strCache><c:ptCount val=\"").Append(XlsxXml.Int(values.Count)).Append("\"/>");
            for (int i = 0; i < values.Count; i++)
            {
                string text = RenderCacheString(values[i]);
                if (text == null) continue;
                sb.Append("<c:pt idx=\"").Append(XlsxXml.Int(i)).Append("\"><c:v>")
                  .Append(XlsxXml.Text(text)).Append("</c:v></c:pt>");
            }
            sb.Append("</c:strCache></c:strRef>");
        }

        /// <summary>
        /// Writes a numeric reference and its cache. Non-numeric or missing cells are skipped as points (the
        /// gap still counts towards <c>ptCount</c>), which is how Excel represents a blank in a value range.
        /// </summary>
        private static void AppendNumRef(StringBuilder sb, string formula, List<object> values)
        {
            sb.Append("<c:numRef><c:f>").Append(XlsxXml.Text(formula ?? string.Empty)).Append("</c:f>");
            sb.Append("<c:numCache><c:formatCode>General</c:formatCode><c:ptCount val=\"")
              .Append(XlsxXml.Int(values.Count)).Append("\"/>");
            for (int i = 0; i < values.Count; i++)
            {
                if (TryToDouble(values[i], out double number))
                {
                    sb.Append("<c:pt idx=\"").Append(XlsxXml.Int(i)).Append("\"><c:v>")
                      .Append(XlsxXml.Num(number)).Append("</c:v></c:pt>");
                }
            }
            sb.Append("</c:numCache></c:numRef>");
        }

        /// <summary>Renders a cell value as a chart-cache label. Returns null only for a missing cell.</summary>
        private static string RenderCacheString(object value)
        {
            switch (value)
            {
                case null:
                    return null;
                case string s:
                    return s;
                case DateTime dt:
                    return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case DateTimeOffset dto:
                    return dto.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                case bool b:
                    return b ? "TRUE" : "FALSE";
                case IFormattable formattable:
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return value.ToString();
            }
        }

        /// <summary>Coerces a cell value to a double for a numeric cache, invariantly for any string values.</summary>
        private static bool TryToDouble(object value, out double result)
        {
            switch (value)
            {
                case null:
                    result = 0;
                    return false;
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case decimal m:
                    result = (double)m;
                    return true;
                case sbyte n:
                    result = n;
                    return true;
                case byte n:
                    result = n;
                    return true;
                case short n:
                    result = n;
                    return true;
                case ushort n:
                    result = n;
                    return true;
                case int n:
                    result = n;
                    return true;
                case uint n:
                    result = n;
                    return true;
                case long n:
                    result = n;
                    return true;
                case ulong n:
                    result = n;
                    return true;
                case DateTime dt:
                    result = dt.ToOADate();
                    return true;
                case DateTimeOffset dto:
                    result = dto.DateTime.ToOADate();
                    return true;
                case string s:
                    return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
                default:
                    result = 0;
                    return false;
            }
        }

        private static void ParseAnchor(string anchorCell, out int col0Based, out int row0Based)
        {
            col0Based = 0;
            row0Based = 0;
            if (XlsxSheet.TryParseA1(anchorCell, out int row1Based, out int col1Based))
            {
                col0Based = col1Based - 1;
                row0Based = row1Based - 1;
            }
        }
    }
}
