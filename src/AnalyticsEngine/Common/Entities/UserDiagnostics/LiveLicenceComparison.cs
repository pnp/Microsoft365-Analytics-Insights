using Common.Entities.CopilotAdoption;
using Common.Entities.Licences;
using DataUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserDiagnostics
{
    /// <summary>
    /// A read-only comparison of what SQL holds for one user's licences against what Microsoft Graph reports now.
    /// It never changes either side.
    /// </summary>
    public class LiveLicenceComparisonModel
    {
        /// <summary>One of <see cref="LiveComparisonStatuses"/>.</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        /// <summary>Why the comparison was not made or failed: one of <see cref="LiveComparisonReasons"/>.</summary>
        [JsonProperty("reason")]
        public string Reason { get; set; }

        [JsonProperty("httpStatus")]
        public int? HttpStatus { get; set; }

        [JsonProperty("observedAtUtc")]
        public DateTime? ObservedAtUtc { get; set; }

        [JsonProperty("directory")]
        public LiveDirectoryUserModel Directory { get; set; }

        /// <summary>
        /// True when Graph returned the user's complete assignment list and every SKU in it was identified, so a
        /// licence missing from it is genuinely not assigned. False means "stored only" cannot be concluded.
        /// </summary>
        [JsonProperty("complete")]
        public bool Complete { get; set; }

        /// <summary>Where SKU part numbers came from: <c>licenseDetails</c> (Graph) or <c>catalogue</c> (this build's file).</summary>
        [JsonProperty("partNumberSource")]
        public string PartNumberSource { get; set; }

        [JsonProperty("rows")]
        public List<LiveLicenceRowModel> Rows { get; set; } = new List<LiveLicenceRowModel>();
    }

    public class LiveDirectoryUserModel
    {
        [JsonProperty("objectId")]
        public string ObjectId { get; set; }

        [JsonProperty("userPrincipalName")]
        public string UserPrincipalName { get; set; }

        [JsonProperty("accountEnabled")]
        public bool? AccountEnabled { get; set; }

        /// <summary>Null when SQL holds no object id to compare.</summary>
        [JsonProperty("objectIdMatchesStored")]
        public bool? ObjectIdMatchesStored { get; set; }

        /// <summary>True when the directory's UPN differs from the stored one: the person was renamed.</summary>
        [JsonProperty("userPrincipalNameDiffersFromStored")]
        public bool UserPrincipalNameDiffersFromStored { get; set; }
    }

    /// <summary>One licence type (as the importer files it), or one SKU the importer has never filed.</summary>
    public class LiveLicenceRowModel
    {
        /// <summary>The stored licence type, or null for a live SKU no licence type exists for yet.</summary>
        [JsonProperty("licenceTypeId")]
        public int? LicenceTypeId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        [JsonProperty("stored")]
        public bool Stored { get; set; }

        [JsonProperty("live")]
        public bool Live { get; set; }

        [JsonProperty("liveSkus")]
        public List<LiveSkuModel> LiveSkus { get; set; } = new List<LiveSkuModel>();

        /// <summary>One of <see cref="SkuMappingStates"/>.</summary>
        [JsonProperty("mapping")]
        public string Mapping { get; set; }

        /// <summary>One of <see cref="LiveComparisonOutcomes"/>.</summary>
        [JsonProperty("comparison")]
        public string Comparison { get; set; }

        [JsonProperty("classification")]
        public ClassificationExplanationModel Classification { get; set; }
    }

    public class LiveSkuModel
    {
        [JsonProperty("skuId")]
        public string SkuId { get; set; }

        [JsonProperty("skuPartNumber")]
        public string SkuPartNumber { get; set; }

        /// <summary>The name this build's catalogue gives the SKU - the licence type the importer files it under.</summary>
        [JsonProperty("catalogueName")]
        public string CatalogueName { get; set; }

        /// <summary><c>direct</c> and/or <c>group</c>.</summary>
        [JsonProperty("assignmentPaths")]
        public List<string> AssignmentPaths { get; set; } = new List<string>();

        /// <summary>Entra's assignment state (e.g. Active, ActiveWithError, Error), when reported.</summary>
        [JsonProperty("state")]
        public string State { get; set; }

        /// <summary>Entra's assignment error code (e.g. CountViolation), when reported.</summary>
        [JsonProperty("error")]
        public string Error { get; set; }

        /// <summary>One of <see cref="CopilotPlanStates"/>: whether this SKU's Copilot seat service plans are switched on for the user.</summary>
        [JsonProperty("copilotSeatPlans")]
        public string CopilotSeatPlans { get; set; }
    }

    public static class LiveComparisonStatuses
    {
        public const string Ok = "ok";
        public const string NotAllowed = "notAllowed";
        public const string Failed = "failed";
    }

    public static class LiveComparisonReasons
    {
        public const string ScopeExcluded = "scopeExcluded";
        public const string ScopeUnknown = "scopeUnknown";
        public const string UserNotFoundInDirectory = "userNotFoundInDirectory";
        public const string PermissionDenied = "permissionDenied";
        public const string GraphUnavailable = "graphUnavailable";
        public const string CredentialsUnavailable = "credentialsUnavailable";
        public const string Timeout = "timeout";
        public const string IdentityMismatch = "identityMismatch";
    }

    public static class SkuMappingStates
    {
        /// <summary>The SKU is filed under a licence type of its own.</summary>
        public const string Unique = "unique";

        /// <summary>Two or more SKUs share this licence type (Microsoft gives them one product name), so SQL cannot tell them apart.</summary>
        public const string Collapsed = "collapsed";

        /// <summary>No licence type exists for this SKU yet: the importer has not filed it.</summary>
        public const string Unmapped = "unmapped";

        /// <summary>The stored licence type was named from an older catalogue; the next import refiles it under the current name.</summary>
        public const string CatalogueDrift = "catalogueDrift";

        /// <summary>A stored licence with nothing live to map.</summary>
        public const string NotApplicable = "notApplicable";
    }

    public static class LiveComparisonOutcomes
    {
        public const string Match = "match";
        public const string StoredOnly = "storedOnly";
        public const string LiveOnly = "liveOnly";

        /// <summary>The evidence is incomplete, so neither "match" nor a difference can be claimed.</summary>
        public const string Indeterminate = "indeterminate";
    }

    public static class CopilotPlanStates
    {
        public const string Enabled = "enabled";
        public const string Disabled = "disabled";
        public const string Pending = "pending";
        public const string NotIncluded = "notIncluded";
        public const string Unknown = "unknown";
    }

    /// <summary>What Graph reported for one user, before any comparison.</summary>
    public sealed class LiveDirectoryObservation
    {
        public string ObjectId { get; set; }
        public string UserPrincipalName { get; set; }
        public bool? AccountEnabled { get; set; }

        /// <summary>
        /// True only when the user response carried an <c>assignedLicenses</c> array. A response without it is not
        /// "no licences" - it is an answer to a different question - so it can never make anything "stored only".
        /// </summary>
        public bool AssignedLicencesPresent { get; set; }

        public List<Guid> AssignedSkuIds { get; set; } = new List<Guid>();

        public List<LiveAssignmentState> AssignmentStates { get; set; } = new List<LiveAssignmentState>();

        /// <summary>Per-SKU details from <c>licenseDetails</c>, or null when that read failed.</summary>
        public List<LiveLicenceDetail> LicenceDetails { get; set; }

        public DateTime ObservedAtUtc { get; set; }
    }

    public sealed class LiveAssignmentState
    {
        public Guid SkuId { get; set; }

        /// <summary>The group the licence is inherited from, or null for a direct assignment.</summary>
        public string AssignedByGroup { get; set; }

        public string State { get; set; }
        public string Error { get; set; }
    }

    public sealed class LiveLicenceDetail
    {
        public Guid SkuId { get; set; }
        public string SkuPartNumber { get; set; }

        /// <summary>Service plan name to provisioning status (Success, Disabled, PendingActivation...).</summary>
        public Dictionary<string, string> ServicePlanStatus { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A stored user's licence assignment, as the comparison needs it.</summary>
    public sealed class StoredLicenceType
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string SkuPartNumber { get; set; }
    }

    /// <summary>
    /// Compares stored and live licences at the granularity SQL actually has: the licence type, which the importer
    /// keys by product display name. Pure, so every rule below is unit tested without Graph or SQL.
    /// </summary>
    public static class LiveLicenceComparer
    {
        /// <summary>Width of <c>license_types.name</c>; the importer truncates longer product names to it.</summary>
        public const int LicenceTypeNameMaxLength = 100;

        /// <param name="tenantLicenceTypes">Every row of <c>dbo.license_types</c>.</param>
        /// <param name="storedLicenceTypeIds">The licence types the user holds in SQL.</param>
        public static LiveLicenceComparisonModel Compare(
            IReadOnlyCollection<StoredLicenceType> tenantLicenceTypes,
            IReadOnlyCollection<int> storedLicenceTypeIds,
            LiveDirectoryObservation live,
            LicensingCatalogue catalogue,
            string storedObjectId,
            string storedUserPrincipalName)
        {
            if (live == null) throw new ArgumentNullException(nameof(live));
            tenantLicenceTypes = tenantLicenceTypes ?? new List<StoredLicenceType>();
            var stored = new HashSet<int>(storedLicenceTypeIds ?? new List<int>());
            catalogue = catalogue ?? LicensingCatalogue.Embedded;

            var model = new LiveLicenceComparisonModel
            {
                Status = LiveComparisonStatuses.Ok,
                ObservedAtUtc = DateTime.SpecifyKind(live.ObservedAtUtc, DateTimeKind.Utc),
                PartNumberSource = live.LicenceDetails != null ? "licenseDetails" : "catalogue",
                Directory = new LiveDirectoryUserModel
                {
                    ObjectId = live.ObjectId,
                    UserPrincipalName = live.UserPrincipalName,
                    AccountEnabled = live.AccountEnabled,
                    ObjectIdMatchesStored = string.IsNullOrWhiteSpace(storedObjectId)
                        ? (bool?)null
                        : string.Equals(storedObjectId.Trim(), live.ObjectId?.Trim(), StringComparison.OrdinalIgnoreCase),
                    UserPrincipalNameDiffersFromStored = !string.IsNullOrWhiteSpace(storedUserPrincipalName)
                        && !string.Equals(storedUserPrincipalName.Trim(), live.UserPrincipalName?.Trim(), StringComparison.OrdinalIgnoreCase),
                },
            };

            var detailsBySku = (live.LicenceDetails ?? new List<LiveLicenceDetail>())
                .GroupBy(d => d.SkuId)
                .ToDictionary(g => g.Key, g => g.First());

            // Map each live SKU to the licence type the importer files it under today.
            var liveSkus = new List<(LiveSkuModel Sku, StoredLicenceType Type, string Mapping)>();
            var allIdentified = true;
            foreach (var skuId in live.AssignedSkuIds.Distinct())
            {
                detailsBySku.TryGetValue(skuId, out var detail);
                var catalogueSku = catalogue.FindByGuid(skuId);
                var partNumber = !string.IsNullOrWhiteSpace(detail?.SkuPartNumber) ? detail.SkuPartNumber.Trim() : catalogueSku?.PartNumber;
                if (catalogueSku == null && !string.IsNullOrEmpty(partNumber))
                {
                    catalogueSku = catalogue.FindByPartNumber(partNumber);
                }

                var skuModel = new LiveSkuModel
                {
                    SkuId = skuId.ToString(),
                    SkuPartNumber = partNumber,
                    CatalogueName = catalogueSku?.DisplayName,
                    CopilotSeatPlans = CopilotPlans(detail, live.LicenceDetails != null),
                };

                foreach (var state in live.AssignmentStates.Where(s => s.SkuId == skuId))
                {
                    var path = string.IsNullOrWhiteSpace(state.AssignedByGroup) ? "direct" : "group";
                    if (!skuModel.AssignmentPaths.Contains(path)) skuModel.AssignmentPaths.Add(path);
                    if (!string.IsNullOrWhiteSpace(state.State) && (skuModel.State == null || !string.Equals(state.State, "Active", StringComparison.OrdinalIgnoreCase)))
                    {
                        skuModel.State = state.State;
                    }
                    if (!string.IsNullOrWhiteSpace(state.Error) && !string.Equals(state.Error, "None", StringComparison.OrdinalIgnoreCase))
                    {
                        skuModel.Error = state.Error;
                    }
                }

                if (string.IsNullOrEmpty(partNumber))
                {
                    allIdentified = false;
                    liveSkus.Add((skuModel, null, SkuMappingStates.Unmapped));
                    continue;
                }

                var expectedName = StringUtils.EnsureMaxLength(catalogueSku?.DisplayName ?? partNumber, LicenceTypeNameMaxLength);
                var byName = tenantLicenceTypes.FirstOrDefault(t => string.Equals(t.Name?.Trim(), expectedName, StringComparison.OrdinalIgnoreCase));
                var bySku = tenantLicenceTypes.FirstOrDefault(t => string.Equals(t.SkuPartNumber?.Trim(), partNumber, StringComparison.OrdinalIgnoreCase));

                if (byName != null)
                {
                    var mapping = string.Equals(byName.SkuPartNumber?.Trim(), partNumber, StringComparison.OrdinalIgnoreCase)
                        ? SkuMappingStates.Unique
                        : SkuMappingStates.Collapsed;
                    liveSkus.Add((skuModel, byName, mapping));
                }
                else if (bySku != null)
                {
                    liveSkus.Add((skuModel, bySku, SkuMappingStates.CatalogueDrift));
                }
                else
                {
                    liveSkus.Add((skuModel, null, SkuMappingStates.Unmapped));
                }
            }

            model.Complete = live.AssignedLicencesPresent && allIdentified;

            // Two live SKUs on one type are indistinguishable in SQL, whatever each mapped as on its own.
            var typesWithSeveralSkus = new HashSet<int>(liveSkus
                .Where(s => s.Type != null)
                .GroupBy(s => s.Type.Id)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key));

            var typeIds = new List<int>(stored);
            foreach (var mapped in liveSkus.Where(s => s.Type != null))
            {
                if (!typeIds.Contains(mapped.Type.Id)) typeIds.Add(mapped.Type.Id);
            }

            var typesById = tenantLicenceTypes.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var typeId in typeIds)
            {
                typesById.TryGetValue(typeId, out var type);
                var mappedSkus = liveSkus.Where(s => s.Type != null && s.Type.Id == typeId).ToList();
                var isStored = stored.Contains(typeId);
                var isLive = mappedSkus.Count > 0;

                string mapping;
                if (!isLive) mapping = SkuMappingStates.NotApplicable;
                else if (typesWithSeveralSkus.Contains(typeId) || mappedSkus.Any(s => s.Mapping == SkuMappingStates.Collapsed)) mapping = SkuMappingStates.Collapsed;
                else mapping = mappedSkus[0].Mapping;

                string comparison;
                if (isStored && isLive) comparison = LiveComparisonOutcomes.Match;
                else if (isLive) comparison = LiveComparisonOutcomes.LiveOnly;
                else comparison = model.Complete ? LiveComparisonOutcomes.StoredOnly : LiveComparisonOutcomes.Indeterminate;

                var explanation = CopilotLicenceClassifier.Explain(type?.SkuPartNumber, type?.Name);
                model.Rows.Add(new LiveLicenceRowModel
                {
                    LicenceTypeId = typeId,
                    Name = type?.Name,
                    SkuPartNumber = type?.SkuPartNumber,
                    Stored = isStored,
                    Live = isLive,
                    LiveSkus = mappedSkus.Select(s => s.Sku).ToList(),
                    Mapping = mapping,
                    Comparison = comparison,
                    Classification = ToModel(explanation),
                });
            }

            foreach (var unmapped in liveSkus.Where(s => s.Type == null))
            {
                var explanation = CopilotLicenceClassifier.Explain(unmapped.Sku.SkuPartNumber, unmapped.Sku.CatalogueName);
                model.Rows.Add(new LiveLicenceRowModel
                {
                    LicenceTypeId = null,
                    Name = unmapped.Sku.CatalogueName,
                    SkuPartNumber = unmapped.Sku.SkuPartNumber,
                    Stored = false,
                    Live = true,
                    LiveSkus = new List<LiveSkuModel> { unmapped.Sku },
                    Mapping = SkuMappingStates.Unmapped,
                    Comparison = LiveComparisonOutcomes.LiveOnly,
                    Classification = ToModel(explanation),
                });
            }

            model.Rows = model.Rows
                .OrderBy(r => r.Comparison == LiveComparisonOutcomes.Match ? 1 : 0)
                .ThenBy(r => r.Name ?? r.SkuPartNumber, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return model;
        }

        public static ClassificationExplanationModel ToModel(CopilotSeatClassification explanation)
        {
            return new ClassificationExplanationModel
            {
                IsCopilotSeat = explanation.IsCopilotSeat,
                Reason = explanation.Reason,
                MatchedRule = explanation.MatchedRule,
            };
        }

        /// <summary>
        /// Whether a SKU's Copilot seat service plans are switched on for this user. A seat that is assigned with those
        /// plans disabled is a licence on paper only - a common cause of "licensed but no Copilot" reports.
        /// </summary>
        internal static string CopilotPlans(LiveLicenceDetail detail, bool detailsRead)
        {
            if (!detailsRead || detail == null) return CopilotPlanStates.Unknown;

            var statuses = LicensingCatalogue.CopilotSeatServicePlans
                .Select(plan => detail.ServicePlanStatus.TryGetValue(plan, out var status) ? status : null)
                .ToList();
            if (statuses.Any(s => s == null)) return CopilotPlanStates.NotIncluded;
            if (statuses.All(s => string.Equals(s, "Success", StringComparison.OrdinalIgnoreCase))) return CopilotPlanStates.Enabled;
            if (statuses.Any(s => string.Equals(s, "Disabled", StringComparison.OrdinalIgnoreCase))) return CopilotPlanStates.Disabled;
            return CopilotPlanStates.Pending;
        }
    }
}
