using Newtonsoft.Json;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{
    public abstract class BaseGraphSerialisationClass
    {

        [JsonProperty("id")]
        public string GraphCallID { get; set; }
    }
}
