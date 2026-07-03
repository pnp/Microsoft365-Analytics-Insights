using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>
    /// Profiling state shown on the SPA's Profiling tab: how fresh the profiling data is. Admins
    /// use this to tell whether the profiling runbooks have run and how up to date each table is.
    /// </summary>
    public class ProfilingStatusModel
    {
        /// <summary>The compiled profiling output tables (built by the profiling runbooks).</summary>
        [JsonProperty("compiledProfiling")]
        public List<DateRangeStatModel> CompiledProfiling { get; set; } = new List<DateRangeStatModel>();

        /// <summary>The raw activity-log tables that feed the profiling compile.</summary>
        [JsonProperty("activityTables")]
        public List<DateRangeStatModel> ActivityTables { get; set; } = new List<DateRangeStatModel>();
    }

    /// <summary>
    /// The earliest and latest date held in one table, plus the SQL behind it so an admin can run
    /// it themselves. <see cref="Error"/> is set (and From/To left null) when the query failed - e.g.
    /// the profiling schema doesn't exist yet because the runbooks have never run.
    /// </summary>
    public class DateRangeStatModel
    {
        /// <summary>Stable identifier for the row (used as a React key).</summary>
        [JsonProperty("key")]
        public string Key { get; set; }

        /// <summary>Friendly name shown to the admin.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary>Fully-qualified table name, e.g. <c>profiling.ActivitiesWeekly</c>.</summary>
        [JsonProperty("table")]
        public string Table { get; set; }

        /// <summary>Earliest date in the table, or null when empty / unavailable.</summary>
        [JsonProperty("from")]
        public DateTime? From { get; set; }

        /// <summary>Latest date in the table, or null when empty / unavailable.</summary>
        [JsonProperty("to")]
        public DateTime? To { get; set; }

        /// <summary>The SQL that produced From/To, for the admin to copy and run.</summary>
        [JsonProperty("sql")]
        public string Sql { get; set; }

        /// <summary>Set when the query failed (e.g. table doesn't exist); From/To are then null.</summary>
        [JsonProperty("error")]
        public string Error { get; set; }
    }

    /// <summary>One row of <c>profiling.TraceLogs</c> - a trace line written by the profiling runbooks.</summary>
    public class TraceLogEntryModel
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("datetime")]
        public DateTime Datetime { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    /// <summary>A page of <c>profiling.TraceLogs</c> rows (newest first), with paging metadata.</summary>
    public class TraceLogPageModel
    {
        [JsonProperty("rows")]
        public List<TraceLogEntryModel> Rows { get; set; } = new List<TraceLogEntryModel>();

        /// <summary>Total number of trace rows in the table (for paging UI).</summary>
        [JsonProperty("totalCount")]
        public int TotalCount { get; set; }

        /// <summary>Zero-based page index returned.</summary>
        [JsonProperty("page")]
        public int Page { get; set; }

        /// <summary>Page size used for this response.</summary>
        [JsonProperty("pageSize")]
        public int PageSize { get; set; }

        /// <summary>Set when the trace logs couldn't be read (e.g. the profiling schema doesn't exist yet).</summary>
        [JsonProperty("error")]
        public string Error { get; set; }
    }
}
