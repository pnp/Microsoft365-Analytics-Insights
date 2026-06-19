using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Aggregate
{
    public abstract class BaseAggregateItemStats
    {
        const string DATE_FORMAT = "yyyy-MM-dd";
        [JsonProperty("reportRefreshDate")]
        public string ReportRefreshDateString { get; set; }
        public DateTime ReportRefreshDate { get => DateTime.ParseExact(ReportRefreshDateString, DATE_FORMAT, CultureInfo.InvariantCulture); set => ReportRefreshDateString = value.ToString(DATE_FORMAT, CultureInfo.InvariantCulture); }
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
