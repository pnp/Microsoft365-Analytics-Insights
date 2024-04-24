using Newtonsoft.Json;
using WebJob.AppInsightsImporter.Engine.ApiImporter;

namespace WebJob.AppInsightsImporter.Engine
{
    public class AppInsightsColumn : NamedObjectAppInsightsObject
    {
        [JsonProperty("type")]
        public string TypeName { get; set; }
    }

}
