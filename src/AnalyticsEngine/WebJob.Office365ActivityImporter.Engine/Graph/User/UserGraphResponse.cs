using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    public class GraphUser
    {
        [JsonProperty("accountEnabled")]
        public bool? AccountEnabled { get; set; }

        [JsonProperty("createdDateTime")]
        public DateTime? CreatedDateTime { get; set; }

        [JsonProperty("officeLocation")]
        public string OfficeLocation { get; set; }

        [JsonProperty("usageLocation")]
        public string UsageLocation { get; set; }

        [JsonProperty("jobTitle")]
        public string JobTitle { get; set; }

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("mail")]
        public string Mail { get; set; }

        [JsonProperty("companyName")]
        public string CompanyName { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("postalCode")]
        public string PostalCode { get; set; }

        [JsonProperty("country")]
        public string Country { get; set; }

        [JsonProperty("state")]
        public string State { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("manager@delta")]
        public List<ManagerInfo> ManagerInfo { get; set; } = new List<ManagerInfo>();

        /// <summary>
        /// Every property Graph returned that has no typed property above - which is exactly where the
        /// admin-configured org attributes land.
        /// </summary>
        /// <remarks>
        /// A catch-all rather than a property per attribute, because which properties arrive is decided
        /// at runtime by the org types an administrator has configured: <c>onPremisesExtensionAttributes</c>,
        /// <c>employeeOrgData</c>, <c>employeeType</c>, <c>employeeId</c>, a directory extension whose
        /// name embeds the owning application's id, or a schema extension. None of those can be modelled
        /// statically.
        ///
        /// Deliberately does NOT shadow the typed properties above: <see cref="JsonExtensionData"/> only
        /// captures what is left over, so <c>department</c> and friends keep flowing through their
        /// existing mapping untouched. That is also why the built-in properties offered as org sources
        /// are restricted to ones this class does not already model.
        /// </remarks>
        [JsonExtensionData]
        public IDictionary<string, JToken> AdditionalProperties { get; set; } = new Dictionary<string, JToken>();

        [JsonIgnore]
        public ManagerInfo DefaultManagerInfo => ManagerInfo?.FirstOrDefault();

    }
    public class ManagerInfo
    {
        [JsonProperty("@odata.type")]
        public string OdataType { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }
    }
}
