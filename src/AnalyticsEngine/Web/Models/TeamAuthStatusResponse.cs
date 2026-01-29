using Newtonsoft.Json;

namespace Web.AnalyticsWeb.Models
{
    /// <summary>
    /// Used to list access tokens for Teams
    /// </summary>
    public class TeamAuthStatusResponse
    {
        [JsonProperty("teamId")]
        public string TeamId { get; set; }

        [JsonProperty("hasAuthToken")]
        public bool HasAuthToken { get; set; }
    }
}