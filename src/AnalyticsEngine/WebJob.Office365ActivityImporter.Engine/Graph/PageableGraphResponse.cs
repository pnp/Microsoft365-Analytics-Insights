using Newtonsoft.Json;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    public class PageableGraphResponse<T>
    {
        public PageableGraphResponse()
        {
        }
        public PageableGraphResponse(IEnumerable<T> results)
        {
            this.PageResults.AddRange(results);
        }

        [JsonProperty("@odata.nextLink")]
        public string OdataNextLink { get; set; }

        [JsonProperty("value")]
        public List<T> PageResults { get; set; } = new List<T>();
    }

    public class PageableGraphResponseWithDelta<T> : PageableGraphResponse<T>
    {
        [JsonProperty("@odata.deltaLink")]
        public string DeltaLink { get; set; }
    }

}
