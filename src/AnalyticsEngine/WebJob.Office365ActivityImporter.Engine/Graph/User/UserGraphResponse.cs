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

        private List<ManagerInfo> _managerInfo = new List<ManagerInfo>();
        private bool _managerInfoAssigned;

        /// <summary>
        /// The <c>manager@delta</c> annotation. Assigning it - as the JSON reader does whenever the entry carries the
        /// annotation, even as an empty array - marks the manager as reported (see <see cref="ManagerReported"/>).
        /// </summary>
        [JsonProperty("manager@delta", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<ManagerInfo> ManagerInfo
        {
            get => _managerInfo;
            set
            {
                _managerInfo = value ?? new List<ManagerInfo>();
                _managerInfoAssigned = true;
            }
        }

        /// <summary>
        /// Whether this entry says anything about the user's manager, so their stored manager may be changed from it.
        /// </summary>
        /// <remarks>
        /// <c>/users/delta</c> only guarantees the <c>manager@delta</c> annotation in the initial round of a read (see
        /// https://learn.microsoft.com/graph/delta-query-overview, "Resource representation in the delta query response").
        /// An incremental entry for a user whose only change was, say, <c>accountEnabled</c> can leave it out, and reading
        /// that as "no manager" cleared the stored manager of every leaver. So the annotation's absence from an incremental
        /// entry means "unchanged"; in the initial round the loader marks every entry reported
        /// (<see cref="UserDeltaReadRules.Classify"/>), because there absence does mean "no manager".
        /// </remarks>
        [JsonIgnore]
        public bool ManagerReported
        {
            get => _managerInfoAssigned || (_managerInfo != null && _managerInfo.Count > 0);
            set => _managerInfoAssigned = value;
        }

        /// <summary>Keeps an entry that never reported a manager from coming back from a stored copy as "no manager".</summary>
        public bool ShouldSerializeManagerInfo() => ManagerReported;

        /// <summary>
        /// The <c>@removed</c> annotation <c>/users/delta</c> puts on a deleted user. Such an entry carries nothing
        /// else but <see cref="Id"/> (the annotation's <c>reason</c> is <c>changed</c> for a soft delete that can still
        /// be restored, <c>deleted</c> for a permanent one).
        /// </summary>
        /// <remarks>
        /// A <see cref="JToken"/> so that no shape Graph sends can fail the page it arrives on. Modelled, rather than
        /// left in <see cref="AdditionalProperties"/>, so a deletion is recognised by one typed check.
        /// </remarks>
        [JsonProperty("@removed")]
        public JToken Removed { get; set; }

        /// <summary>True when <c>/users/delta</c> reported this user as deleted. See <see cref="Removed"/>.</summary>
        [JsonIgnore]
        public bool IsRemoved => Removed != null && Removed.Type != JTokenType.Null;

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

        /// <summary>The manager the entry names, ignoring a link it reports as removed; null when there is none.</summary>
        [JsonIgnore]
        public ManagerInfo DefaultManagerInfo => ManagerInfo?.FirstOrDefault(m => m != null && !m.IsRemoved && !string.IsNullOrEmpty(m.Id));

        /// <summary>
        /// A shallow copy with <see cref="AccountEnabled"/> replaced: an object's profile can come from an earlier entry in
        /// a delta read than its account status. The entries themselves are never modified.
        /// </summary>
        internal GraphUser WithAccountEnabled(bool? accountEnabled)
        {
            var copy = (GraphUser)MemberwiseClone();
            copy.AccountEnabled = accountEnabled;
            return copy;
        }

        /// <summary>
        /// A shallow copy whose manager is the one <paramref name="managerSource"/> reported, or - when it is null -
        /// explicitly none. The entries themselves are never modified.
        /// </summary>
        internal GraphUser WithManagerFrom(GraphUser managerSource)
        {
            var copy = (GraphUser)MemberwiseClone();
            copy.ManagerInfo = managerSource == null ? new List<ManagerInfo>() : new List<ManagerInfo>(managerSource.ManagerInfo);
            return copy;
        }

    }
    public class ManagerInfo
    {
        [JsonProperty("@odata.type")]
        public string OdataType { get; set; }

        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>
        /// The <c>@removed</c> annotation on a relationship link that no longer exists. A <see cref="JToken"/> for the same
        /// reason as <see cref="GraphUser.Removed"/>.
        /// </summary>
        [JsonProperty("@removed", NullValueHandling = NullValueHandling.Ignore)]
        public JToken Removed { get; set; }

        /// <summary>True when the link is reported as removed: the user no longer has this manager.</summary>
        [JsonIgnore]
        public bool IsRemoved => Removed != null && Removed.Type != JTokenType.Null;
    }
}
