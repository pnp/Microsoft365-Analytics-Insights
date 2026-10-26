using Newtonsoft.Json;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    public class PageableGraphResponse<T>
    {
        private List<T> _pageResults = new List<T>();

        public PageableGraphResponse()
        {
        }
        public PageableGraphResponse(IEnumerable<T> results)
        {
            this.PageResults.AddRange(results);
            HasValueCollection = true;
        }

        [JsonProperty("@odata.nextLink")]
        public string OdataNextLink { get; set; }

        /// <summary>The page's rows. Never null: a page that came without them reads as empty - see <see cref="HasValueCollection"/>.</summary>
        /// <remarks>
        /// <see cref="ObjectCreationHandling.Replace"/> makes Newtonsoft assign a new list through the setter. With the
        /// default it fills the list the initialiser created and never calls the setter, so a missing <c>value</c> and an
        /// empty one would look the same.
        /// </remarks>
        [JsonProperty("value", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<T> PageResults
        {
            get { return _pageResults; }
            set
            {
                HasValueCollection = value != null;
                _pageResults = value ?? new List<T>();
            }
        }

        /// <summary>
        /// False when the response had no <c>value</c> collection, or <c>"value": null</c>. Such a page is not an empty
        /// page: its rows are unknown, so a pager must treat it as a failed page rather than step over it.
        /// </summary>
        [JsonIgnore]
        public bool HasValueCollection { get; private set; }
    }

    public class PageableGraphResponseWithDelta<T> : PageableGraphResponse<T>
    {
        [JsonProperty("@odata.deltaLink")]
        public string DeltaLink { get; set; }
    }

}
