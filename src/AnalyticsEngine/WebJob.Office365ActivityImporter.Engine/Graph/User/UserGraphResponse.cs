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
        /// The licence arrays, accepted and immediately discarded.
        /// </summary>
        /// <remarks>
        /// These exist only to keep <see cref="AdditionalProperties"/> from retaining them.
        /// <c>assignedLicenses</c> and <c>assignedPlans</c> are in the delta query's
        /// <c>$select</c> as defence-in-depth for change detection - nothing reads their contents -
        /// and before extension data existed Newtonsoft simply dropped them. Left unmodelled they
        /// would now be kept as <c>JToken</c> trees on every <see cref="GraphUser"/>, and the loader
        /// holds the whole enumeration until the organisation merge near the end of the cycle. At
        /// 200,000 users, where a single E5 mailbox carries scores of service plans, that is millions
        /// of retained objects - enough to turn a working user import into an out-of-memory failure,
        /// on every deployment, including those with no organisation types configured at all.
        ///
        /// A set-only property matches the JSON name, so the value never reaches extension data, and
        /// discarding it in the setter means nothing survives the parse.
        ///
        /// <b>Any future addition to <c>GraphUserDeltaQuery.Select</c> that organisations do not need
        /// must be modelled the same way</b>, or it silently comes back.
        /// </remarks>
        [JsonProperty("assignedLicenses")]
        public JToken AssignedLicensesSink { set { } }

        /// <inheritdoc cref="AssignedLicensesSink"/>
        [JsonProperty("assignedPlans")]
        public JToken AssignedPlansSink { set { } }

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
        /// are restricted to ones this class does not already model - and why every selected property
        /// that is NOT an organisation source has to be modelled, even when nothing reads it. See
        /// <see cref="AssignedLicensesSink"/> for what happens otherwise.
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
