using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>
    /// Which "lite report" areas are available for this deployment, based on the enabled imports
    /// (<c>config.ImportJobSettings</c>). The SPA renders a sub-tab per area, but only for the
    /// areas flagged <c>true</c> here - there's no point charting Copilot usage on a deployment
    /// that never imports Copilot data, for example.
    /// </summary>
    public class ReportAreasModel
    {
        /// <summary>Microsoft 365 Copilot interactions (Audit.General import).</summary>
        [JsonProperty("copilot")]
        public bool Copilot { get; set; }

        /// <summary>Graph interaction history, which feeds prompt categories independently of the audit import.</summary>
        [JsonProperty("copilotInteractionHistory")]
        public bool CopilotInteractionHistory { get; set; }

        /// <summary>Microsoft 365 usage reports (Graph usage-report import: Teams/Outlook/OneDrive/SharePoint/Viva Engage).</summary>
        [JsonProperty("usage")]
        public bool Usage { get; set; }

        /// <summary>SharePoint &amp; OneDrive file activity (Audit.SharePoint import).</summary>
        [JsonProperty("spoAudit")]
        public bool SpoAudit { get; set; }

        /// <summary>Website traffic captured by the SharePoint page tracker (WebTraffic import).</summary>
        [JsonProperty("webTraffic")]
        public bool WebTraffic { get; set; }

        /// <summary>Teams calls (call-records import).</summary>
        [JsonProperty("calls")]
        public bool Calls { get; set; }

        /// <summary>Sent emails (mailbox import).</summary>
        [JsonProperty("emails")]
        public bool Emails { get; set; }

        /// <summary>
        /// Microsoft 365 apps (Word/Excel/PowerPoint/Outlook/OneNote/Teams) and the platforms they
        /// are used on, from the Graph <c>getM365AppUserDetail</c> report.
        /// </summary>
        /// <remarks>
        /// Gated by the SAME import as <see cref="Usage"/> (<c>GraphUsageReports</c>) because both
        /// are fed by the Graph usage-report loaders. The Copilot overlay charts in this area need
        /// the separate Copilot usage-report import as well, which is reported per-chart rather than
        /// hiding the whole area - the app and platform charts are useful without it.
        /// </remarks>
        [JsonProperty("officeApps")]
        public bool OfficeApps { get; set; }
    }

    /// <summary>One cell of a <c>matrix</c> chart: a row, a column and the value where they meet.</summary>
    public class ReportMatrixCell
    {
        [JsonProperty("row")]
        public string Row { get; set; }

        [JsonProperty("column")]
        public string Column { get; set; }

        /// <summary>Null when <see cref="Withheld"/>.</summary>
        [JsonProperty("value")]
        public double? Value { get; set; }

        /// <summary>
        /// True when, for a reader without See PII, <see cref="Value"/> is null: one to four people are behind the cell, so
        /// it is withheld rather than shown - and never shown as zero, which is what an absent cell means (#844). Sent only
        /// when true.
        /// </summary>
        [JsonProperty("withheld", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Withheld { get; set; }
    }

    /// <summary>
    /// A two-dimensional categorical chart - "which app, in which department" - rendered as a
    /// shaded grid.
    /// </summary>
    /// <remarks>
    /// This exists because the questions this report area answers are genuinely 2-D, and flattening
    /// them into a bar chart destroys the finding. "Excel is the most used app" and "Excel is the
    /// most used app <em>in Finance, but barely used in Field Operations</em>" are different
    /// statements, and only the second one tells an adoption analyst where to go. Encoding the
    /// second dimension as a combined "Finance - Excel" bar label technically fits a bar chart, but
    /// with 6 apps and 20 departments that is 120 bars in one ranked list, which no reader can
    /// scan by either dimension.
    /// <para>
    /// <see cref="Rows"/> and <see cref="Columns"/> are sent explicitly rather than inferred from
    /// <see cref="Cells"/> so the grid keeps a deliberate order (apps in a fixed order, departments
    /// ranked by size) and so a row or column that is genuinely all-zero still renders. Inferring
    /// them from the cells would silently drop exactly the empty row an analyst is looking for.
    /// </para>
    /// </remarks>
    public class ReportMatrix
    {
        /// <summary>What the rows are, e.g. "App". Used as the grid's corner heading.</summary>
        [JsonProperty("rowLabel")]
        public string RowLabel { get; set; }

        /// <summary>What the columns are, e.g. "Department".</summary>
        [JsonProperty("columnLabel")]
        public string ColumnLabel { get; set; }

        /// <summary>Row headings, in display order.</summary>
        [JsonProperty("rows")]
        public List<string> Rows { get; set; } = new List<string>();

        /// <summary>Column headings, in display order.</summary>
        [JsonProperty("columns")]
        public List<string> Columns { get; set; } = new List<string>();

        /// <summary>
        /// The populated cells. Cells absent from this list are rendered as zero, so a sparse
        /// matrix does not have to send a value for every intersection.
        /// </summary>
        [JsonProperty("cells")]
        public List<ReportMatrixCell> Cells { get; set; } = new List<ReportMatrixCell>();

        /// <summary>
        /// When true the UI shades each cell against the maximum of its OWN ROW rather than the
        /// whole grid.
        /// </summary>
        /// <remarks>
        /// Needed whenever the rows have wildly different magnitudes. Outlook is used by nearly
        /// everyone and OneNote by a small minority, so shading the whole grid on one scale leaves
        /// the entire OneNote row blank and hides which departments use OneNote most - which is the
        /// only thing that row is there to say.
        /// </remarks>
        [JsonProperty("shadeByRow")]
        public bool ShadeByRow { get; set; }

        /// <summary>
        /// The distinct people behind each column, where the query counts them (the department and email-domain grids);
        /// null otherwise. Only decides what a reader without See PII is shown; internal, so never sent.
        /// </summary>
        internal Dictionary<string, int> ColumnPeople { get; set; }

        /// <summary>A shallow copy, so a reader's copy can replace the columns and cells without editing the cached grid.</summary>
        internal ReportMatrix Copy() => (ReportMatrix)MemberwiseClone();
    }

    /// <summary>
    /// One point of a weekly time series: the (Monday) start of the week and its value.
    /// <see cref="Value"/> is null when the week's value is genuinely unknown rather than zero -
    /// e.g. a Microsoft 365 usage week whose activity report never arrived. Charting those weeks as
    /// zero would draw a sharp (and false) drop, so they are rendered as a gap in the line instead.
    /// </summary>
    public class ReportTimePoint
    {
        [JsonProperty("weekStart")]
        public DateTime WeekStart { get; set; }

        [JsonProperty("value")]
        public double? Value { get; set; }

        /// <summary>
        /// True when the week's source is missing part of the week - a Microsoft 365 usage week with at least one
        /// day's report not imported. <see cref="Value"/> then counts only the days that were imported, so it is a
        /// floor rather than the week's figure. Sent only when true.
        /// </summary>
        [JsonProperty("incomplete", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool Incomplete { get; set; }
    }

    /// <summary>A named line in a time-series chart (e.g. one workload, or "Page views").</summary>
    public class ReportSeries
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("points")]
        public List<ReportTimePoint> Points { get; set; } = new List<ReportTimePoint>();
    }

    /// <summary>Structured reason for a workload series that could not be fully charted.</summary>
    public class ReportSeriesWarning
    {
        [JsonProperty("series")]
        public string Series { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("week")]
        public DateTime? Week { get; set; }

        /// <summary>How many weeks the warning covers, for <c>incompleteWeeks</c>; null otherwise.</summary>
        [JsonProperty("count", NullValueHandling = NullValueHandling.Ignore)]
        public int? Count { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        /// <summary>For <c>loadFailed</c>: the failure's stable reason (<see cref="Common.Entities.ReportQueryFailure.Reason"/>).</summary>
        [JsonProperty("errorReason", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorReason { get; set; }

        /// <summary>For <c>loadFailed</c>: the error reference the server log carries.</summary>
        [JsonProperty("errorReference", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorReference { get; set; }
    }

    /// <summary>One bar of a categorical chart (e.g. an app host, or an operation type).</summary>
    public class ReportCategory
    {
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("value")]
        public double Value { get; set; }

        /// <summary>
        /// The distinct people behind the bar, where the query counts them (the prompt key phrases and languages); null
        /// otherwise. Only decides what a reader without See PII is shown; internal, so never sent.
        /// </summary>
        internal int? People { get; set; }
    }

    /// <summary>
    /// A single chart in a report area. It is either a weekly <c>timeseries</c> (populated
    /// <see cref="Series"/>) or a categorical <c>bar</c> chart (populated <see cref="Categories"/>).
    /// <see cref="Sql"/> is the query behind it (shown in the same SQL popover the other admin
    /// pages use) and <see cref="Error"/> is set when the query failed or timed out, so one heavy
    /// chart degrades to a message rather than failing the whole area (mirrors ProfilingStatus).
    /// </summary>
    public class ReportChart
    {
        /// <summary>Stable identifier (used as a React key).</summary>
        [JsonProperty("key")]
        public string Key { get; set; }

        /// <summary>Chart heading shown to the admin.</summary>
        [JsonProperty("title")]
        public string Title { get; set; }

        /// <summary>Short description of what the chart shows.</summary>
        [JsonProperty("description")]
        public string Description { get; set; }

        /// <summary><c>timeseries</c>, <c>bar</c>, <c>wordcloud</c> or <c>matrix</c>.</summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>Unit label for the value axis / tooltip, e.g. "Interactions".</summary>
        [JsonProperty("valueLabel")]
        public string ValueLabel { get; set; }

        /// <summary>The series (one or more) for a <c>timeseries</c> chart; null for a bar chart.</summary>
        [JsonProperty("series")]
        public List<ReportSeries> Series { get; set; }

        /// <summary>The bars for a <c>bar</c> chart; null for a time-series chart.</summary>
        [JsonProperty("categories")]
        public List<ReportCategory> Categories { get; set; }

        /// <summary>The grid for a <c>matrix</c> chart; null for every other type.</summary>
        [JsonProperty("matrix")]
        public ReportMatrix Matrix { get; set; }

        /// <summary>
        /// For a <c>bar</c> chart, whether each bar's share of the total is meaningful and should be
        /// shown next to it.
        /// </summary>
        /// <remarks>
        /// Off by default because it is only true when the bars are parts of one whole. "Users per
        /// app" bars are NOT: a person who uses both Word and Excel is counted in both, so the bars
        /// sum to more than the number of people and a share would be nonsense. Rates (adoption %
        /// by department) are not parts of a whole either.
        /// </remarks>
        [JsonProperty("showShare")]
        public bool ShowShare { get; set; }

        /// <summary>
        /// Unit suffix appended to each value when rendered, e.g. "%". Null for a plain count.
        /// </summary>
        [JsonProperty("valueSuffix")]
        public string ValueSuffix { get; set; }

        /// <summary>The SQL that produced this chart, for the admin to copy and run.</summary>
        [JsonProperty("sql")]
        public string Sql { get; set; }

        /// <summary>
        /// Set when the query failed/timed out; the chart data is then empty. For a query failure it is the reason and an
        /// error reference in English (<see cref="Common.Entities.ReportQueryFailure"/>), never the exception's text.
        /// </summary>
        [JsonProperty("error")]
        public string Error { get; set; }

        /// <summary>Stable key for a chart-level error the SPA can translate.</summary>
        [JsonProperty("errorKey")]
        public string ErrorKey { get; set; }

        /// <summary>For <see cref="ErrorKey"/> <c>queryFailed</c>: the failure's stable reason, which the SPA words.</summary>
        [JsonProperty("errorReason", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorReason { get; set; }

        /// <summary>For <see cref="ErrorKey"/> <c>queryFailed</c>: the error reference the server log carries.</summary>
        [JsonProperty("errorReference", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorReference { get; set; }

        /// <summary>Set when part of a chart could not load but other series remain usable.</summary>
        [JsonProperty("warning")]
        public string Warning { get; set; }

        /// <summary>
        /// Stable key for a <see cref="Warning"/> built from <see cref="SeriesWarnings"/>, which the SPA words:
        /// <c>seriesUnavailable</c>, <c>incompleteWeeks</c> (every series is charted, but some weeks are missing a
        /// day of reports) or <c>seriesUnavailableAndIncompleteWeeks</c>. Absent for any other warning.
        /// </summary>
        [JsonProperty("warningKey", NullValueHandling = NullValueHandling.Ignore)]
        public string WarningKey { get; set; }

        /// <summary>Structured partial-data warnings behind <see cref="Warning"/>.</summary>
        [JsonProperty("seriesWarnings")]
        public List<ReportSeriesWarning> SeriesWarnings { get; set; }

        /// <summary>
        /// For a reader without See PII, the fewest people a bar, word or matrix column and cell of this chart describes
        /// (#844); null when nothing is held back for the reader. The SPA says so beside the chart.
        /// </summary>
        [JsonProperty("minimumGroupSize", NullValueHandling = NullValueHandling.Ignore)]
        public int? MinimumGroupSize { get; set; }

        /// <summary>
        /// How many bars or words (or, for a matrix, columns) were left out for a reader without See PII. Sent only when
        /// some were.
        /// </summary>
        [JsonProperty("withheldItems", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int WithheldItems { get; set; }

        /// <summary>
        /// This chart for a reader who does not hold the portal's See PII permission, when it counts the people behind its
        /// parts (#844): a bar or word fewer than <paramref name="minimumPeople"/> people are behind is left out; a matrix
        /// column of fewer people is left out, and a cell of one to four people withheld. A chart that counts nobody is
        /// returned as it is. A copy: areas are cached and shared between readers.
        /// </summary>
        internal ReportChart WithoutSmallGroups(int minimumPeople)
        {
            if (Matrix?.ColumnPeople != null)
            {
                var columns = Matrix.Columns
                    .Where(c => Matrix.ColumnPeople.TryGetValue(c, out var people) && people >= minimumPeople)
                    .ToList();
                var shown = new HashSet<string>(columns, StringComparer.Ordinal);
                var matrix = Matrix.Copy();
                matrix.Columns = columns;
                matrix.Cells = Matrix.Cells
                    .Where(cell => shown.Contains(cell.Column))
                    .Select(cell => cell.Value > 0 && cell.Value < minimumPeople
                        ? new ReportMatrixCell { Row = cell.Row, Column = cell.Column, Value = null, Withheld = true }
                        : cell)
                    .ToList();

                var copy = (ReportChart)MemberwiseClone();
                copy.Matrix = matrix;
                copy.MinimumGroupSize = minimumPeople;
                copy.WithheldItems = Matrix.Columns.Count - columns.Count;
                return copy;
            }

            if (Categories != null && Categories.Any(c => c.People.HasValue))
            {
                var copy = (ReportChart)MemberwiseClone();
                copy.Categories = Categories.Where(c => (c.People ?? 0) >= minimumPeople).ToList();
                copy.MinimumGroupSize = minimumPeople;
                copy.WithheldItems = Categories.Count - copy.Categories.Count;
                return copy;
            }

            return this;
        }
    }

    /// <summary>The set of charts for one report area over the requested window.</summary>
    public class ReportAreaData
    {
        /// <summary>The area key, e.g. "copilot".</summary>
        [JsonProperty("area")]
        public string Area { get; set; }

        /// <summary>The window in months these charts cover.</summary>
        [JsonProperty("months")]
        public int Months { get; set; }

        /// <summary>The (Monday) start of the earliest week charted.</summary>
        [JsonProperty("fromWeek")]
        public DateTime FromWeek { get; set; }

        [JsonProperty("charts")]
        public List<ReportChart> Charts { get; set; } = new List<ReportChart>();

        /// <summary>
        /// Whether the app has a usable Azure AI Language (cognitive) configuration.
        /// <para>
        /// Only meaningful for the "copilot" area. The three prompt-insight charts (key phrases,
        /// prompt sentiment, prompt language) are built from cognitive enrichment, so without a
        /// configuration they are omitted rather than shown permanently empty. Sending the flag lets
        /// the UI say *why* they are missing instead of leaving the admin to wonder - issue #312.
        /// </para>
        /// </summary>
        [JsonProperty("cognitiveConfigured")]
        public bool CognitiveConfigured { get; set; }

        /// <summary>
        /// This area for a reader who does not hold the portal's See PII permission (#844): every chart that counts the
        /// people behind its parts held to <paramref name="minimumPeople"/> (see <see cref="ReportChart.WithoutSmallGroups"/>).
        /// A copy: the area is cached and shared between readers, so the instance itself is never edited for one of them.
        /// </summary>
        internal ReportAreaData WithoutSmallGroups(int minimumPeople)
        {
            var copy = (ReportAreaData)MemberwiseClone();
            copy.Charts = (Charts ?? new List<ReportChart>()).Select(c => c?.WithoutSmallGroups(minimumPeople)).ToList();
            return copy;
        }
    }
}
