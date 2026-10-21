using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Common.Entities.UserDiagnostics
{
    // The contract the portal's per-user diagnostics read. Every value an administrator reads as a sentence is a
    // stable key here - the portal writes the sentence in the reader's language - and every timestamp says what it
    // measures, because "last updated" has been read as "last checked" before and that is how this feature started.

    /// <summary>Stored facts about one user, plus the evidence an administrator needs to judge them.</summary>
    public class UserDiagnosticsModel
    {
        /// <summary>The UPN the administrator looked up, normalised.</summary>
        [JsonProperty("lookedUpUpn")]
        public string LookedUpUpn { get; set; }

        /// <summary>The <c>dbo.users</c> row, or null when the import has never stored this person.</summary>
        [JsonProperty("storedUser")]
        public StoredUserModel StoredUser { get; set; }

        [JsonProperty("scope")]
        public UserScopeDecisionModel Scope { get; set; }

        /// <summary>The user's current stored licence assignments (<c>dbo.user_license_type_lookups</c>).</summary>
        [JsonProperty("licences")]
        public List<StoredLicenceModel> Licences { get; set; } = new List<StoredLicenceModel>();

        [JsonProperty("history")]
        public LicenceHistoryModel History { get; set; } = new LicenceHistoryModel();

        [JsonProperty("freshness")]
        public LicenceFreshnessModel Freshness { get; set; } = new LicenceFreshnessModel();

        [JsonProperty("catalogue")]
        public ClassificationCatalogueModel Catalogue { get; set; } = new ClassificationCatalogueModel();

        /// <summary>What the evidence suggests, as stable keys with their facts. Never a verdict on the source of truth.</summary>
        [JsonProperty("findings")]
        public List<DiagnosticFindingModel> Findings { get; set; } = new List<DiagnosticFindingModel>();

        /// <summary>Whether a one-person refresh can be requested, and this person's recent requests.</summary>
        [JsonProperty("refresh")]
        public UserRefreshCapabilityModel Refresh { get; set; } = new UserRefreshCapabilityModel();

        [JsonProperty("loadedAtUtc")]
        public DateTime LoadedAtUtc { get; set; }
    }

    public class UserRefreshCapabilityModel
    {
        [JsonProperty("available")]
        public bool Available { get; set; }

        /// <summary>Why not, when <see cref="Available"/> is false: one of <see cref="UserRefreshRejections"/>.</summary>
        [JsonProperty("reason")]
        public string Reason { get; set; }

        /// <summary>The request still waiting or running for this person, if any. A new request returns this one.</summary>
        [JsonProperty("active")]
        public UserRefreshRequest Active { get; set; }

        /// <summary>This person's most recent requests, newest first.</summary>
        [JsonProperty("recent")]
        public List<UserRefreshRequest> Recent { get; set; } = new List<UserRefreshRequest>();
    }

    public class UserRefreshSubmitRequest
    {
        [JsonProperty("upn")]
        public string Upn { get; set; }
    }

    public class UserRefreshSubmitModel
    {
        [JsonProperty("accepted")]
        public bool Accepted { get; set; }

        /// <summary>One of <see cref="UserRefreshRejections"/> when not accepted.</summary>
        [JsonProperty("rejection")]
        public string Rejection { get; set; }

        /// <summary>The queued request - or the one already queued or running for this person.</summary>
        [JsonProperty("request")]
        public UserRefreshRequest Request { get; set; }
    }

    public class StoredUserModel
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("mail")]
        public string Mail { get; set; }

        [JsonProperty("azureAdId")]
        public string AzureAdId { get; set; }

        [JsonProperty("accountEnabled")]
        public bool? AccountEnabled { get; set; }

        /// <summary>
        /// When the user import last WROTE this row's metadata (the existing <c>users.last_updated</c>). Not when the
        /// person or their licences were last checked: a delta import does not rewrite an unchanged user, and the
        /// licence refresh does not touch this column at all. Pre-#426 rows may carry the old host-local value.
        /// </summary>
        [JsonProperty("metadataLastWrittenUtc")]
        public DateTime? MetadataLastWrittenUtc { get; set; }
    }

    /// <summary>One licence the user holds in SQL, explained.</summary>
    public class StoredLicenceModel
    {
        [JsonProperty("licenceTypeId")]
        public int LicenceTypeId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary><c>license_types.sku_id</c>: the SKU part number of the first SKU filed under this name.</summary>
        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        [JsonProperty("classification")]
        public ClassificationExplanationModel Classification { get; set; }

        /// <summary>Whether this build's licensing catalogue lists the SKU part number at all.</summary>
        [JsonProperty("catalogueListed")]
        public bool CatalogueListed { get; set; }

        /// <summary>Whether the catalogue lists the Microsoft 365 Copilot seat service plans in this SKU.</summary>
        [JsonProperty("catalogueCarriesCopilotSeat")]
        public bool CatalogueCarriesCopilotSeat { get; set; }

        /// <summary>When the open history row for this assignment starts, if history is recorded.</summary>
        [JsonProperty("heldSinceUtc")]
        public DateTime? HeldSinceUtc { get; set; }

        /// <summary>
        /// <c>observed</c> (first seen by a licence refresh at <see cref="HeldSinceUtc"/>; the real assignment is
        /// somewhere between the previous refresh and then), <c>beforeTrackingBegan</c> (already held when history
        /// started - no assignment date is known) or <c>unknown</c> (no open history row).
        /// </summary>
        [JsonProperty("heldSinceKind")]
        public string HeldSinceKind { get; set; }
    }

    public static class HeldSinceKinds
    {
        public const string Observed = "observed";
        public const string BeforeTrackingBegan = "beforeTrackingBegan";
        public const string Unknown = "unknown";
    }

    public class ClassificationExplanationModel
    {
        [JsonProperty("isCopilotSeat")]
        public bool IsCopilotSeat { get; set; }

        /// <summary>One of <see cref="CopilotAdoption.CopilotSeatClassificationReasons"/>.</summary>
        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("matchedRule")]
        public string MatchedRule { get; set; }
    }

    public class LicenceHistoryModel
    {
        /// <summary>False when the database has no licence history tables yet (an older schema).</summary>
        [JsonProperty("available")]
        public bool Available { get; set; }

        /// <summary>The first licence refresh that recorded history; nothing before it is known.</summary>
        [JsonProperty("trackingBeganUtc")]
        public DateTime? TrackingBeganUtc { get; set; }

        [JsonProperty("rows")]
        public List<LicenceHistoryRowModel> Rows { get; set; } = new List<LicenceHistoryRowModel>();

        /// <summary>True when the user has more history rows than were returned.</summary>
        [JsonProperty("truncated")]
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// One observed holding. Both bounds are when a licence refresh OBSERVED the change, not when Entra made it: the
    /// real change happened after the previous refresh and before the observing one.
    /// </summary>
    public class LicenceHistoryRowModel
    {
        [JsonProperty("licenceTypeId")]
        public int LicenceTypeId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        [JsonProperty("validFromUtc")]
        public DateTime ValidFromUtc { get; set; }

        /// <summary>The refresh before the one that first saw the holding - the earliest the assignment could have been.</summary>
        [JsonProperty("validFromPreviousRefreshUtc")]
        public DateTime? ValidFromPreviousRefreshUtc { get; set; }

        [JsonProperty("validToUtc")]
        public DateTime? ValidToUtc { get; set; }

        [JsonProperty("validToPreviousRefreshUtc")]
        public DateTime? ValidToPreviousRefreshUtc { get; set; }

        /// <summary>True for a row seeded when history began: the person already held the licence then.</summary>
        [JsonProperty("heldBeforeTrackingBegan")]
        public bool HeldBeforeTrackingBegan { get; set; }

        /// <summary>
        /// True when an administrator's one-person refresh saw the change, rather than the tenant-wide refresh. Such a
        /// row has no earlier bound: the change happened at some unknown time before it was seen.
        /// </summary>
        [JsonProperty("recordedByUserRefresh")]
        public bool RecordedByUserRefresh { get; set; }
    }

    /// <summary>When licences were last refreshed, and how much that says about this user.</summary>
    public class LicenceFreshnessModel
    {
        /// <summary>Whether the user metadata and licence import is switched on. Null when it can't be read.</summary>
        [JsonProperty("userImportEnabled")]
        public bool? UserImportEnabled { get; set; }

        /// <summary>The configured minimum hours between user imports.</summary>
        [JsonProperty("userImportIntervalHours")]
        public int UserImportIntervalHours { get; set; }

        /// <summary>
        /// When the user import last completed tenant-wide (its schedule stamp in Azure Table storage). Says the
        /// import finished; not that this person was in it.
        /// </summary>
        [JsonProperty("userImportLastCompletedUtc")]
        public DateTime? UserImportLastCompletedUtc { get; set; }

        /// <summary>One of <see cref="StampStates"/>.</summary>
        [JsonProperty("userImportStampState")]
        public string UserImportStampState { get; set; }

        /// <summary>The last licence refresh recorded in <c>dbo.license_refresh_runs</c>, tenant-wide.</summary>
        [JsonProperty("lastTenantLicenceRefreshUtc")]
        public DateTime? LastTenantLicenceRefreshUtc { get; set; }

        /// <summary>
        /// Whether there is evidence that a licence check covered THIS user, and when. <c>notRecorded</c> until
        /// per-user verification is recorded; the tenant-wide times above are context only.
        /// </summary>
        [JsonProperty("userVerification")]
        public string UserVerification { get; set; } = UserVerificationStates.NotRecorded;

        [JsonProperty("userVerifiedUtc")]
        public DateTime? UserVerifiedUtc { get; set; }
    }

    public static class UserVerificationStates
    {
        public const string NotRecorded = "notRecorded";
        public const string Verified = "verified";
    }

    /// <summary>Whether a completion stamp in the runtime state table could be read.</summary>
    public static class StampStates
    {
        public const string Recorded = "recorded";

        /// <summary>The store was read and holds no stamp: the section has not completed since stamps were kept.</summary>
        public const string NotRecorded = "notRecorded";

        /// <summary>No Storage connection string, so nothing is stamped durably.</summary>
        public const string StorageNotConfigured = "storageNotConfigured";

        public const string StorageUnavailable = "storageUnavailable";

        /// <summary>The source has no completion stamp at all; its liveness is on the Health page.</summary>
        public const string NoStampForSource = "noStampForSource";
    }

    /// <summary>The Copilot seat rules, and how every licence type in the tenant fares against them.</summary>
    public class ClassificationCatalogueModel
    {
        [JsonProperty("ruleSetVersion")]
        public string RuleSetVersion { get; set; }

        /// <summary>The build whose embedded licensing catalogue and rules produced this.</summary>
        [JsonProperty("buildLabel")]
        public string BuildLabel { get; set; }

        /// <summary>Identifies this build's copy of Microsoft's licensing catalogue. It carries no publication date.</summary>
        [JsonProperty("catalogueContentHash")]
        public string CatalogueContentHash { get; set; }

        [JsonProperty("catalogueSkuCount")]
        public int CatalogueSkuCount { get; set; }

        [JsonProperty("seatSkuPrefixes")]
        public List<string> SeatSkuPrefixes { get; set; } = new List<string>();

        [JsonProperty("excludedSkuPrefixes")]
        public List<string> ExcludedSkuPrefixes { get; set; } = new List<string>();

        [JsonProperty("copilotSeatServicePlans")]
        public List<string> CopilotSeatServicePlans { get; set; } = new List<string>();

        [JsonProperty("tenantLicenceTypes")]
        public List<TenantLicenceTypeModel> TenantLicenceTypes { get; set; } = new List<TenantLicenceTypeModel>();
    }

    public class TenantLicenceTypeModel
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        [JsonProperty("assignedUsers")]
        public int AssignedUsers { get; set; }

        [JsonProperty("classification")]
        public ClassificationExplanationModel Classification { get; set; }

        [JsonProperty("catalogueListed")]
        public bool CatalogueListed { get; set; }

        [JsonProperty("catalogueCarriesCopilotSeat")]
        public bool CatalogueCarriesCopilotSeat { get; set; }

        /// <summary>
        /// The catalogue and the rules disagree about whether this SKU is a Copilot seat. The rules are not changed by
        /// this; an administrator can test a what-if seat selection on the Copilot Adoption report.
        /// </summary>
        [JsonProperty("possibleRuleGap")]
        public bool PossibleRuleGap { get; set; }
    }

    /// <summary>A stable finding key, its severity and the facts its sentence needs.</summary>
    public class DiagnosticFindingModel
    {
        public DiagnosticFindingModel() { }

        public DiagnosticFindingModel(string key, string severity, Dictionary<string, object> values = null)
        {
            Key = key;
            Severity = severity;
            Values = values;
        }

        [JsonProperty("key")]
        public string Key { get; set; }

        /// <summary><c>info</c> or <c>warning</c>.</summary>
        [JsonProperty("severity")]
        public string Severity { get; set; }

        [JsonProperty("values", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> Values { get; set; }
    }

    public static class FindingSeverities
    {
        public const string Info = "info";
        public const string Warning = "warning";
    }

    /// <summary>Stable finding keys. The portal maps each to a sentence; renaming one shows the raw key.</summary>
    public static class DiagnosticFindingKeys
    {
        public const string NoStoredUser = "noStoredUser";
        public const string NoStoredLicences = "noStoredLicences";
        public const string NoStoredCopilotSeat = "noStoredCopilotSeat";
        public const string AccountDisabled = "accountDisabled";
        public const string ScopeExcluded = "scopeExcluded";
        public const string ScopeUnknown = "scopeUnknown";
        public const string UserImportDisabled = "userImportDisabled";
        public const string UserImportNotRecorded = "userImportNotRecorded";
        public const string UserImportOverdue = "userImportOverdue";
        public const string LicenceHistoryUnavailable = "licenceHistoryUnavailable";
        public const string CatalogueMissingSku = "catalogueMissingSku";
        public const string PossibleCopilotRuleGap = "possibleCopilotRuleGap";

        public static readonly string[] All =
        {
            NoStoredUser, NoStoredLicences, NoStoredCopilotSeat, AccountDisabled, ScopeExcluded, ScopeUnknown,
            UserImportDisabled, UserImportNotRecorded, UserImportOverdue, LicenceHistoryUnavailable,
            CatalogueMissingSku, PossibleCopilotRuleGap,
        };
    }

    /// <summary>Whether data about this person may be imported - and so whether live directory reads are allowed.</summary>
    public class UserScopeDecisionModel
    {
        /// <summary>One of <see cref="UserScopeDecisions"/>.</summary>
        [JsonProperty("decision")]
        public string Decision { get; set; }

        /// <summary>
        /// True only for <c>unfiltered</c> and <c>inScope</c>. An administrator cannot read live directory data about
        /// someone the deployment has been told not to collect data about, or whose scope cannot be established.
        /// </summary>
        [JsonProperty("liveComparisonAllowed")]
        public bool LiveComparisonAllowed { get; set; }

        [JsonProperty("resolvedUtc")]
        public DateTime? ResolvedUtc { get; set; }

        [JsonProperty("matchedGroupCount")]
        public int? MatchedGroupCount { get; set; }

        [JsonProperty("memberCount")]
        public int? MemberCount { get; set; }

        /// <summary>Why scope is unknown, when it is: the same keys the User scope page uses.</summary>
        [JsonProperty("failureKind")]
        public string FailureKind { get; set; }
    }

    public static class UserScopeDecisions
    {
        /// <summary>No UserGroupsFilter: everyone is in scope.</summary>
        public const string Unfiltered = "unfiltered";

        /// <summary>A direct member of a group the filter resolved to.</summary>
        public const string InScope = "inScope";

        /// <summary>The filter resolved completely and this person is not in it.</summary>
        public const string Excluded = "excluded";

        /// <summary>The filter could not be resolved completely, so membership is not proven either way.</summary>
        public const string Unknown = "unknown";
    }
}
