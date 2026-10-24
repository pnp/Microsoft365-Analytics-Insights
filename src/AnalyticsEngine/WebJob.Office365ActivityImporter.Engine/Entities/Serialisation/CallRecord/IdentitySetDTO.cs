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

    /// <summary>
    /// <c>callRecord.organizer_v2</c> (<c>microsoft.graph.callRecords.organizer</c>), which replaces the identitySet in the
    /// retired <c>callRecord.organizer</c>. Its <c>identity.user</c> has the same <c>{id, tenantId}</c> shape.
    /// </summary>
    public class OrganizerV2DTO
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("identity")]
        public IdentitySetDTO Identity { get; set; }
    }

    /// <summary>
    /// <c>participantEndpoint.associatedIdentity</c> (<c>microsoft.graph.identity</c>), which replaces the identitySet in the
    /// retired <c>participantEndpoint.identity</c>. It is the user itself, not a set that holds one.
    /// </summary>
    public class AssociatedIdentityDTO : UserDTO
    {
    }

}
