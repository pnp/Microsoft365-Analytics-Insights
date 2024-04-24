using Newtonsoft.Json;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models
{
    public class AuthTeamRequest
    {

        [JsonProperty("teamIdsToAuth")]
        public List<string> TeamIdsToAuth { get; set; }

        [JsonProperty("teamIdsToDeauth")]
        public List<string> TeamIdsToDeauth { get; set; }

        [JsonProperty("token")]
        public string Token { get; set; }
    }
}