using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

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

        [JsonProperty("error")]
        public JObject Error { get; set; }

        public AppInsightsTable DefaultTable
        {
            get
            {
                if (Error != null)
                {
                    throw new InvalidDataException("Application Insights returned an error or partial query results. The import is halted to avoid losing data; automatic query-window splitting is not implemented.");
                }
                if (Tables == null)
                {
                    throw new InvalidOperationException("App Insights query response did not contain a 'tables' element.");
                }
                if (Tables.Count == 0)
                {
                    throw new InvalidOperationException("App Insights query response 'tables' element was empty.");
                }
                return Tables[0];
            }
        }
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
