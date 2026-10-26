using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WebJob.AppInsightsImporter.Engine.ApiImporter
{

    public abstract class NamedObjectAppInsightsObject
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }
    public class AppInsightsQueryResult
    {
        /// <summary>The error code App Insights reports when it truncated a result at its row or size limit.</summary>
        public const string PartialErrorCode = "PartialError";

        [JsonProperty("tables")]
        public List<AppInsightsTable> Tables { get; set; }

        [JsonProperty("error")]
        public JObject Error { get; set; }

        /// <summary>
        /// App Insights stopped the result at its row or response-size limit (500,000 rows / ~104 MB) and returned
        /// only part of it, which it reports as a top-level error with code <see cref="PartialErrorCode"/>
        /// alongside the rows it did return. Those rows must never be imported as if complete: see
        /// <c>AppInsightsAPIClient</c>, which queries a smaller window instead.
        /// </summary>
        public bool IsPartialResult => string.Equals(Error?.Value<string>("code"), PartialErrorCode, StringComparison.OrdinalIgnoreCase);

        public AppInsightsTable DefaultTable
        {
            get
            {
                if (Error != null)
                {
                    throw new InvalidDataException($"Application Insights returned an error or partial query results ({Error.Value<string>("code") ?? "no error code"}). The import is halted to avoid losing data.");
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

        /// <summary>
        /// One table holding the rows of <paramref name="tables"/> in the order given - the results of consecutive
        /// query windows, so the rows stay in time order. Every table must have the same columns, as results of
        /// one projected query do.
        /// </summary>
        internal static AppInsightsTable Concatenate(IReadOnlyList<AppInsightsTable> tables)
        {
            if (tables == null || tables.Count == 0)
            {
                throw new ArgumentException("At least one table is needed.", nameof(tables));
            }

            var first = tables[0];
            var columnNames = (first.Columns ?? new List<Column>()).Select(c => c.Name).ToList();
            var rows = new List<List<object>>();
            foreach (var table in tables)
            {
                if (!(table.Columns ?? new List<Column>()).Select(c => c.Name).SequenceEqual(columnNames))
                {
                    throw new InvalidDataException("Application Insights returned different columns for consecutive windows of the same query.");
                }
                if (table.Rows != null)
                {
                    rows.AddRange(table.Rows);
                }
            }
            return new AppInsightsTable { Name = first.Name, Columns = first.Columns, Rows = rows };
        }
    }

    public class Column : NamedObjectAppInsightsObject
    {
        [JsonProperty("type")]
        public string TypeName { get; set; }
    }

}
