using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate
{
    public abstract class BaseAggregateItemStats
    {
        const string DATE_FORMAT = "yyyy-MM-dd";
        [JsonProperty("reportRefreshDate")]
        public string ReportRefreshDateString { get; set; }
        public DateTime ReportRefreshDate { get => DateTime.ParseExact(ReportRefreshDateString, DATE_FORMAT, null); set => ReportRefreshDateString = value.ToString(DATE_FORMAT); }
        public abstract string OfficeUniqueIdField { get; }
    }

    public class AggregateResourceUsageDetail<T> where T : BaseAggregateItemStats
    {
        [JsonProperty("value")]
        public IEnumerable<T> Stats { get; set; }

        [JsonProperty("@odata.nextLink")]
        public string NextLink { get; set; }

        public bool HasNextLink => !string.IsNullOrEmpty(NextLink);
    }
}
