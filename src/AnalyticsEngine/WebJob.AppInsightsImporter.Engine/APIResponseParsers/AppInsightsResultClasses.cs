using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace WebJob.AppInsightsImporter.Engine.ApiImporter
{

    public abstract class NamedObjectAppInsightsObject
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }
    public class AppInsightsQueryResult
    {
        [JsonProperty("tables")]
        public List<AppInsightsTable> Tables { get; set; }

        public AppInsightsTable DefaultTable => Tables?.Count > 0 ? Tables[0] : throw new ArgumentOutOfRangeException();
    }
    public class AppInsightsTable : NamedObjectAppInsightsObject
    {

        [JsonProperty("columns")]
        public List<Column> Columns { get; set; }

        [JsonProperty("rows")]
        public List<List<object>> Rows { get; set; }
    }

    public class Column : NamedObjectAppInsightsObject
    {
        [JsonProperty("type")]
        public string TypeName { get; set; }
    }

}
