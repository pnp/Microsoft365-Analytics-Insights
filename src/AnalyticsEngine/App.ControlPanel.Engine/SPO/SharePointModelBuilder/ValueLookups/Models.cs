using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace App.ControlPanel.Engine.SharePointModelBuilder.ValueLookups
{
    public class JsonObjectToStringParams
    {
        [JsonPropertyName("jsonPayLoad")]
        public JsonObject PayLoad { get; set; }

        [JsonIgnore]
        public bool IsValid => PayLoad != null;

    }
}
