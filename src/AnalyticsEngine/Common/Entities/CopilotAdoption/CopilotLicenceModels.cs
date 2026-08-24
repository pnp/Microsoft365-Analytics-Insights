using Newtonsoft.Json;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>A row of <c>dbo.license_types</c> plus how many users hold it.</summary>
    public class LicenceTypeRow
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string SkuPartNumber { get; set; }

        public int AssignedUsers { get; set; }
    }

    /// <summary>A licence type and whether the tool counted it as a Microsoft 365 Copilot seat.</summary>
    public class LicenceTypeClassification
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        [JsonProperty("assignedUsers")]
        public int AssignedUsers { get; set; }

        [JsonProperty("isCopilotSeat")]
        public bool IsCopilotSeat { get; set; }
    }
}
