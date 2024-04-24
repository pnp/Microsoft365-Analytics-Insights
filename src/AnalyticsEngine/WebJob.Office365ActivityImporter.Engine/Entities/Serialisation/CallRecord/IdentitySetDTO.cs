using Newtonsoft.Json;

namespace WebJob.Office365ActivityImporter.Engine.Entities.Serialisation
{

    public class UserDTO
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        [JsonProperty("tenantId")]
        public string TenantId { get; set; }

        [JsonProperty("registrantId")]
        public object RegistrantId { get; set; }
    }

    public class IdentitySetDTO
    {
        [JsonProperty("user")]
        public UserDTO User { get; set; }
    }

}
