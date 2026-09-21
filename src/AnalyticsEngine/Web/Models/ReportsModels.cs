using Newtonsoft.Json;
using System;
using System.Collections.Generic;

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

        [JsonProperty("value")]
        public double Value { get; set; }
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
    }

    /// <summary>A named line in a time-series chart (e.g. one workload, or "Page views").</summary>
    public class ReportSeries
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("points")]
        public List<ReportTimePoint> Points { get; set; } = new List<ReportTimePoint>();
    }

    /// <summary>One bar of a categorical chart (e.g. an app host, or an operation type).</summary>
    public class ReportCategory
    {
        [JsonProperty("label")]
        public string Label { get; set; }

        [JsonProperty("value")]
        public double Value { get; set; }
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

        /// <summary>Set when the query failed/timed out; the chart data is then empty.</summary>
        [JsonProperty("error")]
        public string Error { get; set; }

        /// <summary>Set when part of a chart could not load but other series remain usable.</summary>
        [JsonProperty("warning")]
        public string Warning { get; set; }
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
    }
}
