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

        /// <summary><c>timeseries</c> or <c>bar</c>.</summary>
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
    }
}
