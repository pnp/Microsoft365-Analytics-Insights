using Microsoft.Graph;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.User
{
    // Root myDeserializedClass = JsonConvert.DeserializeObject<Root>(myJsonResponse);
    public class UserTeamAppResponse
    {
        [JsonProperty("@odata.context")]
        public string OdataContext { get; set; }

        [JsonProperty("@odata.count")]
        public int OdataCount { get; set; }

        [JsonProperty("value")]
        public List<UserTeamApp> Apps { get; set; }
    }

    public class UserTeamApp
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("teamsAppDefinition")]
        public TeamsAppDefinition TeamsAppDefinition { get; set; }

        [JsonProperty("teamsApp")]
        public TeamsApp TeamsApp { get; set; }
    }


}
