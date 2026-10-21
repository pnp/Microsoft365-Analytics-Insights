using Common.Entities;
using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.Licences;
using Common.Entities.State;
using Common.Entities.UserDiagnostics;
using Common.Entities.UserScope;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.UserDataLookup;
using Web.AnalyticsWeb.Models.UserScope;

namespace Web.AnalyticsWeb.Models.UserDiagnostics
{
    /// <summary>
    /// The per-user diagnostics behind Administration &gt; User lookup: the evidence an administrator needs to explain
    /// why a person shows as licensed or unlicensed, and how fresh each kind of data held about them is. Read-only:
    /// nothing here changes SQL, the directory or any import's state.
    /// </summary>
    /// <remarks>
    /// Live directory reads are refused unless the person is confirmed in the import scope (<c>UserGroupsFilter</c>):
    /// a deployment told not to collect data about someone must not offer a page that reads it on demand.
    /// </remarks>
    public sealed class UserDiagnosticsService
    {
        /// <summary>History rows returned per user. Enough for years of normal churn; the response says when more exist.</summary>
        public const int HistoryTake = 100;

        /// <summary>Hours past the configured interval before a missing user import is reported as overdue.</summary>
        public const int OverdueGraceHours = 24;

        private readonly IUserDiagnosticsQuery _query;
        private readonly Func<Task<UserImportScopeResolution>> _resolveScope;
        private readonly ILiveDirectoryReader _liveReader;
        private readonly IImportStampReader _stamps;
        private readonly Func<ImportTaskSettings> _importSettings;
        private readonly int _userImportIntervalHours;
        private readonly LicensingCatalogue _catalogue;
        private readonly string _buildLabel;
        private readonly Func<DateTime> _utcNow;
        private readonly UserRefreshStore _refreshStore;

        /// <param name="refreshStore">The one-person refresh queue; null without a Storage account (no refresh offered).</param>
        public UserDiagnosticsService(
            IUserDiagnosticsQuery query,
            Func<Task<UserImportScopeResolution>> resolveScope,
            ILiveDirectoryReader liveReader,
            IImportStampReader stamps,
            Func<ImportTaskSettings> importSettings,
            int userImportIntervalHours,
            LicensingCatalogue catalogue = null,
            string buildLabel = null,
            Func<DateTime> utcNow = null,
            UserRefreshStore refreshStore = null)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _resolveScope = resolveScope ?? throw new ArgumentNullException(nameof(resolveScope));
            _liveReader = liveReader ?? throw new ArgumentNullException(nameof(liveReader));
            _stamps = stamps ?? throw new ArgumentNullException(nameof(stamps));
            _importSettings = importSettings ?? (() => null);
            _userImportIntervalHours = userImportIntervalHours;
            _catalogue = catalogue ?? LicensingCatalogue.Embedded;
            _buildLabel = buildLabel ?? BuildConstants.BuildLabel;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _refreshStore = refreshStore;
        }

        /// <summary>The service for this deployment, reading the same configuration and scope the importer uses.</summary>
        public static UserDiagnosticsService ForThisDeployment()
        {
            var config = new AppConfig();
            return new UserDiagnosticsService(
                new SqlUserDiagnosticsQuery(),
                () => UserScopeService.ProductionProvider.Value.GetResolutionAsync(),
                GraphLiveDirectoryReader.ForConfig(config, UserScopeService.ProductionLogger.Value),
                TableImportStampReader.ForThisDeployment(),
                () => config.ImportJobSettings,
                config.GraphMetadataImportIntervalHours,
                refreshStore: UserRefreshStore.TryOpen(config, UserScopeService.ProductionLogger.Value));
        }

        public async Task<UserDataLookupResult<UserDiagnosticsModel>> GetDiagnosticsAsync(string upn)
        {
            upn = UserDataLookupRules.Normalise(upn);
            if (string.IsNullOrEmpty(upn))
            {
                return UserDataLookupResult<UserDiagnosticsModel>.BadRequest("A 'upn' query parameter is required.");
            }

            var now = _utcNow();
            var model = new UserDiagnosticsModel { LookedUpUpn = upn, LoadedAtUtc = now };

            var user = await _query.FindUserAsync(upn);
            var licenceTypes = await _query.LicenceTypesAsync() ?? new List<LicenceTypeRow>();
            model.Catalogue = BuildCatalogue(licenceTypes);
            model.Scope = DecideScope(await SafeResolveScopeAsync(), upn, user?.Mail, user?.AzureAdId);

            var settings = SafeSettings();
            var stamps = await _stamps.ReadAsync(new[] { ImportScheduleKeys.GraphUsersMetadata });
            stamps.TryGetValue(ImportScheduleKeys.GraphUsersMetadata, out var userImportStamp);
            model.Freshness = new LicenceFreshnessModel
            {
                UserImportEnabled = settings?.GraphUsersMetadata,
                UserImportIntervalHours = _userImportIntervalHours,
                UserImportLastCompletedUtc = userImportStamp?.LastSuccessUtc,
                UserImportStampState = userImportStamp?.State ?? StampStates.NotRecorded,
            };

            if (user != null)
            {
                model.StoredUser = new StoredUserModel
                {
                    UserId = user.Id,
                    UserPrincipalName = user.UserPrincipalName,
                    Mail = user.Mail,
                    AzureAdId = user.AzureAdId,
                    AccountEnabled = user.AccountEnabled,
                    MetadataLastWrittenUtc = user.LastUpdated.HasValue ? DateTime.SpecifyKind(user.LastUpdated.Value, DateTimeKind.Utc) : (DateTime?)null,
                };

                var history = await _query.HistoryAsync(user.Id, HistoryTake);
                model.History = new LicenceHistoryModel
                {
                    Available = history.Available,
                    TrackingBeganUtc = history.TrackingBeganUtc,
                    Rows = history.Rows,
                    Truncated = history.Truncated,
                };
                model.Freshness.LastTenantLicenceRefreshUtc = history.LastRefreshUtc;

                var stored = await _query.StoredLicenceTypesForUserAsync(user.Id) ?? new List<StoredLicenceType>();
                model.Licences = stored
                    .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(l => DescribeStoredLicence(l, history))
                    .ToList();
            }

            model.Refresh = await DescribeRefreshAsync(user, model.Scope, settings, model.Freshness);
            model.Findings = BuildFindings(model, now);
            return UserDataLookupResult<UserDiagnosticsModel>.Ok(model);
        }

        /// <summary>
        /// Queues a one-person refresh of the profile and licences, for the importer to carry out. Refused - with a
        /// stable reason, not an error - for a person who is not stored or not confirmed in the import scope; the
        /// importer checks both again before it reads anything.
        /// </summary>
        public async Task<UserDataLookupResult<UserRefreshSubmitModel>> SubmitRefreshAsync(string upn, string requestedBy)
        {
            upn = UserDataLookupRules.Normalise(upn);
            if (string.IsNullOrEmpty(upn))
            {
                return UserDataLookupResult<UserRefreshSubmitModel>.BadRequest("A 'upn' query parameter is required.");
            }

            var user = await _query.FindUserAsync(upn);
            var scope = DecideScope(await SafeResolveScopeAsync(), upn, user?.Mail, user?.AzureAdId);
            var blocked = RefreshBlockedReason(user, scope, SafeSettings());
            if (blocked != null)
            {
                return UserDataLookupResult<UserRefreshSubmitModel>.Ok(new UserRefreshSubmitModel { Rejection = blocked });
            }

            try
            {
                var (request, rejection) = await _refreshStore.SubmitAsync(user.Id, user.UserPrincipalName, user.AzureAdId, requestedBy);
                return UserDataLookupResult<UserRefreshSubmitModel>.Ok(new UserRefreshSubmitModel
                {
                    Accepted = request != null,
                    Request = request,
                    Rejection = rejection,
                });
            }
            catch (UserRefreshStoreUnavailableException)
            {
                return UserDataLookupResult<UserRefreshSubmitModel>.Ok(new UserRefreshSubmitModel { Rejection = UserRefreshRejections.StorageUnavailable });
            }
        }

        /// <summary>One refresh request, or null when there is no such request (or no queue).</summary>
        public async Task<UserRefreshRequest> GetRefreshAsync(string id)
        {
            if (_refreshStore == null) return null;
            try
            {
                return await _refreshStore.GetAsync(id);
            }
            catch (UserRefreshStoreUnavailableException)
            {
                return null;
            }
        }

        private async Task<UserRefreshCapabilityModel> DescribeRefreshAsync(StoredUserRow user, UserScopeDecisionModel scope, ImportTaskSettings settings, LicenceFreshnessModel freshness)
        {
            var model = new UserRefreshCapabilityModel { Reason = RefreshBlockedReason(user, scope, settings) };
            model.Available = model.Reason == null;
            if (_refreshStore == null || user == null) return model;

            try
            {
                var verification = await _refreshStore.GetVerificationAsync(user.Id);
                if (verification != null)
                {
                    freshness.UserVerification = UserVerificationStates.Verified;
                    freshness.UserVerifiedUtc = DateTime.SpecifyKind(verification.VerifiedUtc, DateTimeKind.Utc);
                }

                model.Recent = await _refreshStore.RecentForUserAsync(user.Id);
                model.Active = model.Recent.FirstOrDefault(r => !r.IsFinished);
            }
            catch (UserRefreshStoreUnavailableException)
            {
                model.Available = false;
                model.Reason = UserRefreshRejections.StorageUnavailable;
            }
            return model;
        }

        /// <summary>
        /// Why a refresh cannot be requested for this person, or null when it can. Unknown scope blocks it, as it blocks
        /// live reads: a refresh reads the directory.
        /// </summary>
        private string RefreshBlockedReason(StoredUserRow user, UserScopeDecisionModel scope, ImportTaskSettings settings)
        {
            if (_refreshStore == null) return UserRefreshRejections.StorageNotConfigured;
            if (settings?.GraphUsersMetadata == false) return UserRefreshRejections.ImportDisabled;
            if (user == null) return UserRefreshRejections.NotStored;
            if (scope?.Decision == UserScopeDecisions.Excluded) return UserRefreshRejections.ScopeExcluded;
            if (scope == null || scope.Decision == UserScopeDecisions.Unknown) return UserRefreshRejections.ScopeUnknown;
            return null;
        }

        /// <summary>
        /// Reads the person's licences from Microsoft Graph now and compares them with SQL. Refused - before any Graph
        /// call - unless the person is confirmed in the import scope.
        /// </summary>
        public async Task<UserDataLookupResult<LiveLicenceComparisonModel>> CompareLiveAsync(string upn, CancellationToken cancellationToken)
        {
            upn = UserDataLookupRules.Normalise(upn);
            if (string.IsNullOrEmpty(upn))
            {
                return UserDataLookupResult<LiveLicenceComparisonModel>.BadRequest("A 'upn' query parameter is required.");
            }

            var user = await _query.FindUserAsync(upn);
            var scope = DecideScope(await SafeResolveScopeAsync(), upn, user?.Mail, user?.AzureAdId);
            if (!scope.LiveComparisonAllowed)
            {
                return UserDataLookupResult<LiveLicenceComparisonModel>.Ok(new LiveLicenceComparisonModel
                {
                    Status = LiveComparisonStatuses.NotAllowed,
                    Reason = scope.Decision == UserScopeDecisions.Excluded ? LiveComparisonReasons.ScopeExcluded : LiveComparisonReasons.ScopeUnknown,
                });
            }

            // The object id survives a rename, so it is the key whenever SQL has one.
            var key = !string.IsNullOrWhiteSpace(user?.AzureAdId) ? user.AzureAdId.Trim() : upn;
            var read = await _liveReader.ReadAsync(key, cancellationToken);
            if (read.Observation == null)
            {
                return UserDataLookupResult<LiveLicenceComparisonModel>.Ok(new LiveLicenceComparisonModel
                {
                    Status = LiveComparisonStatuses.Failed,
                    Reason = read.FailureReason ?? LiveComparisonReasons.GraphUnavailable,
                    HttpStatus = read.HttpStatus,
                });
            }

            // Looked up by UPN with no stored row: the directory's answer must still be the person asked about, and a
            // filtered scope must still contain that object - a UPN can be reassigned to someone else.
            if (user == null && !string.Equals(read.Observation.UserPrincipalName?.Trim(), upn, StringComparison.OrdinalIgnoreCase))
            {
                return UserDataLookupResult<LiveLicenceComparisonModel>.Ok(new LiveLicenceComparisonModel
                {
                    Status = LiveComparisonStatuses.Failed,
                    Reason = LiveComparisonReasons.IdentityMismatch,
                });
            }

            var tenantTypes = (await _query.LicenceTypesAsync() ?? new List<LicenceTypeRow>())
                .Select(t => new StoredLicenceType { Id = t.Id, Name = t.Name, SkuPartNumber = t.SkuPartNumber })
                .ToList();
            var stored = user == null
                ? new List<int>()
                : (await _query.StoredLicenceTypesForUserAsync(user.Id) ?? new List<StoredLicenceType>()).Select(l => l.Id).ToList();

            var comparison = LiveLicenceComparer.Compare(tenantTypes, stored, read.Observation, _catalogue, user?.AzureAdId, user?.UserPrincipalName);
            return UserDataLookupResult<LiveLicenceComparisonModel>.Ok(comparison);
        }

        /// <summary>Why a report counts this person as licensed or unlicensed, recomputed with the report's own rule.</summary>
        public async Task<UserDataLookupResult<ReportLicenceVerdictModel>> GetReportVerdictAsync(
            string upn, string report, int windowDays, string from, string to, IReadOnlyCollection<int> licenceTypeIds)
        {
            upn = UserDataLookupRules.Normalise(upn);
            report = UserDataLookupRules.Normalise(report);
            if (string.IsNullOrEmpty(upn))
            {
                return UserDataLookupResult<ReportLicenceVerdictModel>.BadRequest("A 'upn' query parameter is required.");
            }

            var user = await _query.FindUserAsync(upn);
            if (user == null)
            {
                return UserDataLookupResult<ReportLicenceVerdictModel>.UserNotFound($"No user found with UPN '{upn}'.");
            }

            var now = _utcNow();
            if (string.Equals(report, ReportVerdictReports.LicenceActivity, StringComparison.OrdinalIgnoreCase))
            {
                return UserDataLookupResult<ReportLicenceVerdictModel>.Ok(
                    await ReportLicenceVerdictBuilder.ForLicenceActivityAsync(_query, user.Id, licenceTypeIds, now));
            }

            if (!string.IsNullOrEmpty(report) && !string.Equals(report, ReportVerdictReports.CopilotAdoption, StringComparison.OrdinalIgnoreCase))
            {
                return UserDataLookupResult<ReportLicenceVerdictModel>.BadRequest($"Unknown report '{report}'.");
            }

            CopilotAdoptionDateRange range;
            try
            {
                range = CopilotAdoptionDateRange.Create(windowDays, string.IsNullOrWhiteSpace(from) ? null : from, string.IsNullOrWhiteSpace(to) ? null : to, now);
            }
            catch (ArgumentException ex)
            {
                return UserDataLookupResult<ReportLicenceVerdictModel>.BadRequest(ex.Message);
            }

            return UserDataLookupResult<ReportLicenceVerdictModel>.Ok(
                await ReportLicenceVerdictBuilder.ForCopilotAdoptionAsync(_query, user.Id, user.UserPrincipalName, range, licenceTypeIds, now));
        }

        /// <summary>The newest stored row of every category, with what is known about each feeding import.</summary>
        public async Task<UserDataLookupResult<UserCoverageModel>> GetCoverageAsync(string upn)
        {
            upn = UserDataLookupRules.Normalise(upn);
            if (string.IsNullOrEmpty(upn))
            {
                return UserDataLookupResult<UserCoverageModel>.BadRequest("A 'upn' query parameter is required.");
            }

            var user = await _query.FindUserAsync(upn);
            if (user == null)
            {
                return UserDataLookupResult<UserCoverageModel>.UserNotFound($"No user found with UPN '{upn}'.");
            }

            var settings = SafeSettings();
            var stampKeys = UserCoverageRules.CategoryKeys.Select(UserCoverageRules.StampKey).Where(k => k != null).Distinct().ToList();
            var stamps = await _stamps.ReadAsync(stampKeys);

            var model = new UserCoverageModel { LoadedAtUtc = _utcNow() };
            foreach (var key in UserCoverageRules.CategoryKeys)
            {
                var workloads = UserCoverageRules.Workloads(key).ToList();
                var category = new CoverageCategoryModel
                {
                    Key = key,
                    Workloads = workloads,
                    WorkloadsEnabled = workloads.Any(w => UserDataLookupRules.WorkloadEnabled(settings, w)),
                    LatestKind = UserCoverageRules.LatestKind(key),
                    PerUserReplay = UserCoverageRules.PerUserReplay(key),
                };

                var stampKey = UserCoverageRules.StampKey(key);
                if (stampKey == null)
                {
                    category.SourceStampState = StampStates.NoStampForSource;
                }
                else if (stamps.TryGetValue(stampKey, out var stamp))
                {
                    category.SourceStampState = stamp.State;
                    category.SourceLastSuccessUtc = stamp.LastSuccessUtc;
                }

                try
                {
                    var latest = await _query.LatestAsync(user.Id, key);
                    category.LatestUtc = latest?.LatestUtc;
                    category.LastActivityDateUtc = latest?.LastActivityDateUtc;
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    // One slow or failing table costs its own row, not the whole matrix.
                    category.Error = true;
                }

                model.Categories.Add(category);
            }

            return UserDataLookupResult<UserCoverageModel>.Ok(model);
        }

        /// <summary>
        /// Whether data about this person may be imported - see <see cref="UserScopeDecider"/>, which the importer's
        /// one-person refresh applies too.
        /// </summary>
        internal static UserScopeDecisionModel DecideScope(UserImportScopeResolution resolution, params string[] identifiers)
            => UserScopeDecider.Decide(resolution, identifiers);

        private async Task<UserImportScopeResolution> SafeResolveScopeAsync()
        {
            try
            {
                return await _resolveScope();
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return null;
            }
        }

        private ImportTaskSettings SafeSettings()
        {
            try
            {
                return _importSettings();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private StoredLicenceModel DescribeStoredLicence(StoredLicenceType licence, LicenceHistoryRead history)
        {
            var catalogueSku = _catalogue.FindByPartNumber(licence.SkuPartNumber);
            var open = history.Rows.FirstOrDefault(r => r.LicenceTypeId == licence.Id && r.ValidToUtc == null);
            return new StoredLicenceModel
            {
                LicenceTypeId = licence.Id,
                Name = licence.Name,
                SkuPartNumber = licence.SkuPartNumber,
                Classification = LiveLicenceComparer.ToModel(CopilotLicenceClassifier.Explain(licence.SkuPartNumber, licence.Name)),
                CatalogueListed = catalogueSku != null,
                CatalogueCarriesCopilotSeat = catalogueSku?.CarriesCopilotSeatServicePlans == true,
                HeldSinceUtc = open?.ValidFromUtc,
                HeldSinceKind = open == null
                    ? HeldSinceKinds.Unknown
                    : open.HeldBeforeTrackingBegan ? HeldSinceKinds.BeforeTrackingBegan : HeldSinceKinds.Observed,
            };
        }

        private ClassificationCatalogueModel BuildCatalogue(IEnumerable<LicenceTypeRow> licenceTypes)
        {
            var model = new ClassificationCatalogueModel
            {
                RuleSetVersion = CopilotLicenceClassifier.RuleSetVersion,
                BuildLabel = _buildLabel,
                CatalogueContentHash = _catalogue.ContentHash,
                CatalogueSkuCount = _catalogue.SkuCount,
                SeatSkuPrefixes = CopilotLicenceClassifier.SeatSkuPrefixes.ToList(),
                ExcludedSkuPrefixes = CopilotLicenceClassifier.ExcludedSkuPrefixes.ToList(),
                CopilotSeatServicePlans = LicensingCatalogue.CopilotSeatServicePlans.ToList(),
            };

            foreach (var type in licenceTypes.OrderByDescending(t => t.AssignedUsers).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            {
                var explanation = CopilotLicenceClassifier.Explain(type.SkuPartNumber, type.Name);
                var catalogueSku = _catalogue.FindByPartNumber(type.SkuPartNumber);
                var carriesSeat = catalogueSku?.CarriesCopilotSeatServicePlans == true;
                model.TenantLicenceTypes.Add(new TenantLicenceTypeModel
                {
                    Id = type.Id,
                    Name = type.Name,
                    SkuPartNumber = type.SkuPartNumber,
                    AssignedUsers = type.AssignedUsers,
                    Classification = LiveLicenceComparer.ToModel(explanation),
                    CatalogueListed = catalogueSku != null,
                    CatalogueCarriesCopilotSeat = carriesSeat,
                    PossibleRuleGap = catalogueSku != null && carriesSeat != explanation.IsCopilotSeat,
                });
            }
            return model;
        }

        private List<DiagnosticFindingModel> BuildFindings(UserDiagnosticsModel model, DateTime nowUtc)
        {
            var findings = new List<DiagnosticFindingModel>();
            void Add(string key, string severity, Dictionary<string, object> values = null) => findings.Add(new DiagnosticFindingModel(key, severity, values));

            if (model.Scope.Decision == UserScopeDecisions.Excluded) Add(DiagnosticFindingKeys.ScopeExcluded, FindingSeverities.Warning);
            if (model.Scope.Decision == UserScopeDecisions.Unknown) Add(DiagnosticFindingKeys.ScopeUnknown, FindingSeverities.Warning);

            if (model.StoredUser == null)
            {
                Add(DiagnosticFindingKeys.NoStoredUser, FindingSeverities.Warning);
            }
            else
            {
                if (model.StoredUser.AccountEnabled == false) Add(DiagnosticFindingKeys.AccountDisabled, FindingSeverities.Info);
                if (model.Licences.Count == 0) Add(DiagnosticFindingKeys.NoStoredLicences, FindingSeverities.Warning);
                else if (!model.Licences.Any(l => l.Classification.IsCopilotSeat)) Add(DiagnosticFindingKeys.NoStoredCopilotSeat, FindingSeverities.Info);
                if (!model.History.Available) Add(DiagnosticFindingKeys.LicenceHistoryUnavailable, FindingSeverities.Info);
            }

            if (model.Freshness.UserImportEnabled == false)
            {
                Add(DiagnosticFindingKeys.UserImportDisabled, FindingSeverities.Warning);
            }
            else if (model.Freshness.UserImportStampState != StampStates.Recorded)
            {
                Add(DiagnosticFindingKeys.UserImportNotRecorded, FindingSeverities.Info,
                    new Dictionary<string, object> { { "state", model.Freshness.UserImportStampState } });
            }
            else if (model.Freshness.UserImportLastCompletedUtc.HasValue
                && nowUtc - model.Freshness.UserImportLastCompletedUtc.Value > TimeSpan.FromHours(Math.Max(0, _userImportIntervalHours) + OverdueGraceHours))
            {
                Add(DiagnosticFindingKeys.UserImportOverdue, FindingSeverities.Warning,
                    new Dictionary<string, object> { { "lastCompletedUtc", model.Freshness.UserImportLastCompletedUtc.Value } });
            }

            foreach (var licence in model.Licences.Where(l => !l.CatalogueListed && !string.IsNullOrWhiteSpace(l.SkuPartNumber)))
            {
                Add(DiagnosticFindingKeys.CatalogueMissingSku, FindingSeverities.Info, new Dictionary<string, object> { { "sku", licence.SkuPartNumber } });
            }

            foreach (var gap in model.Catalogue.TenantLicenceTypes.Where(t => t.PossibleRuleGap))
            {
                Add(DiagnosticFindingKeys.PossibleCopilotRuleGap, FindingSeverities.Warning, new Dictionary<string, object>
                {
                    { "name", gap.Name ?? string.Empty },
                    { "sku", gap.SkuPartNumber ?? string.Empty },
                    { "counted", gap.Classification.IsCopilotSeat },
                });
            }

            return findings;
        }
    }
}
