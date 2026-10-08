using Common.Entities.Config;
using Common.Entities.LeadershipCohort;
using DataUtils;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.LeadershipCohort
{
    /// <summary>What the Administration page shows about the leadership cohort (#654). Never the membership.</summary>
    public sealed class LeadershipCohortStatusModel
    {
        /// <summary>False without a Storage connection string: settings could not survive a restart, so saving is refused.</summary>
        [JsonProperty("stateDurable")]
        public bool StateDurable { get; set; }

        [JsonProperty("configured")]
        public bool Configured { get; set; }

        [JsonProperty("groupId")]
        public string GroupId { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime? UpdatedUtc { get; set; }

        [JsonProperty("minimumCohort")]
        public int MinimumCohort { get; set; }

        [JsonProperty("maxMembers")]
        public int MaxMembers { get; set; }

        [JsonProperty("refreshAfterSuccessHours")]
        public double RefreshAfterSuccessHours { get; set; }

        [JsonProperty("staleAfterHours")]
        public double StaleAfterHours { get; set; }

        /// <summary>The last refresh of the configured group, or null when it has not been refreshed yet.</summary>
        [JsonProperty("refresh")]
        public LeadershipCohortRefreshModel Refresh { get; set; }
    }

    public sealed class LeadershipCohortRefreshModel
    {
        /// <summary>One of <see cref="LeadershipCohortRefreshStatuses"/>.</summary>
        [JsonProperty("status")]
        public string Status { get; set; }

        /// <summary>For a failed refresh, one of <see cref="LeadershipCohortFailureKinds"/>.</summary>
        [JsonProperty("failureKind")]
        public string FailureKind { get; set; }

        [JsonProperty("httpStatus")]
        public int? HttpStatus { get; set; }

        /// <summary>Tenant data, shown to the administrator as Entra ID returned it and never translated.</summary>
        [JsonProperty("groupDisplayName")]
        public string GroupDisplayName { get; set; }

        [JsonProperty("attemptedUtc")]
        public DateTime AttemptedUtc { get; set; }

        [JsonProperty("refreshedUtc")]
        public DateTime? RefreshedUtc { get; set; }

        [JsonProperty("directMembers")]
        public int DirectMembers { get; set; }

        [JsonProperty("matchedUsers")]
        public int MatchedUsers { get; set; }

        [JsonProperty("stale")]
        public bool Stale { get; set; }
    }

    public sealed class LeadershipCohortSaveRequest
    {
        /// <summary>The Entra ID group object id. Empty turns the comparison off.</summary>
        [JsonProperty("groupId")]
        public string GroupId { get; set; }
    }

    public sealed class LeadershipCohortError
    {
        [JsonProperty("code")]
        public string Code { get; set; }
    }

    /// <summary>Stable error codes; the portal turns each into a sentence. A public contract with the portal.</summary>
    public static class LeadershipCohortErrorCodes
    {
        public const string InvalidGroupId = "invalidGroupId";
        public const string StateNotDurable = "stateNotDurable";
        public const string StateUnavailable = "stateUnavailable";
        public const string NotConfigured = "notConfigured";
        public const string RefreshInProgress = "refreshInProgress";
    }

    public sealed class LeadershipCohortRequestException : Exception
    {
        public LeadershipCohortRequestException(HttpStatusCode status, string code) : base(code)
        {
            Status = status;
            Code = code;
        }

        public HttpStatusCode Status { get; }
        public string Code { get; }
    }

    internal sealed class LeadershipCohortService
    {
        private static readonly Lazy<AnalyticsLogger> ProductionLogger = new Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "LeadershipCohort"));

        private readonly LeadershipCohortStore _store;
        private readonly Func<LeadershipCohortRefresher> _createRefresher;
        private readonly Action _invalidate;
        private readonly Func<DateTime> _utcNow;

        public LeadershipCohortService(LeadershipCohortStore store, Func<LeadershipCohortRefresher> createRefresher, Action invalidate, Func<DateTime> utcNow = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _createRefresher = createRefresher ?? throw new ArgumentNullException(nameof(createRefresher));
            _invalidate = invalidate ?? (() => { });
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public static LeadershipCohortService ForThisDeployment()
        {
            var config = new AppConfig();
            var store = LeadershipComparisonProvider.OpenStore(config);
            return new LeadershipCohortService(
                store,
                () => LeadershipCohortRefresher.ForGraph(store, config, ProductionLogger.Value),
                LeadershipComparisonProvider.Default.Invalidate);
        }

        public async Task<LeadershipCohortStatusModel> GetStatusAsync()
        {
            var settings = await _store.GetSettingsAsync();
            var snapshot = settings == null ? null : await _store.GetSnapshotAsync();
            if (snapshot != null && !string.Equals(snapshot.SettingsRevision, settings.Revision, StringComparison.Ordinal)) snapshot = null;

            return new LeadershipCohortStatusModel
            {
                StateDurable = _store.IsDurable,
                Configured = settings != null,
                GroupId = settings?.GroupId,
                UpdatedUtc = settings?.UpdatedUtc,
                MinimumCohort = LeadershipAdoptionCalculator.MinimumCohort,
                MaxMembers = LeadershipCohortStore.MaxMembers,
                RefreshAfterSuccessHours = LeadershipCohortRefresher.RefreshAfterSuccess.TotalHours,
                StaleAfterHours = LeadershipAdoptionCalculator.StaleAfter.TotalHours,
                Refresh = snapshot == null ? null : new LeadershipCohortRefreshModel
                {
                    Status = snapshot.Status,
                    FailureKind = snapshot.FailureKind,
                    HttpStatus = snapshot.HttpStatus,
                    GroupDisplayName = snapshot.GroupDisplayName,
                    AttemptedUtc = snapshot.AttemptedUtc,
                    RefreshedUtc = snapshot.RefreshedUtc,
                    DirectMembers = snapshot.DirectMembers,
                    MatchedUsers = snapshot.MatchedUsers,
                    Stale = snapshot.RefreshedUtc.HasValue && _utcNow() - snapshot.RefreshedUtc.Value > LeadershipAdoptionCalculator.StaleAfter,
                },
            };
        }

        /// <summary>
        /// Saves the group (or clears it) and, when one is set, reads its membership straight away so the administrator
        /// sees at once whether the group was found and the app may read it. The read is bounded (about eleven Graph
        /// calls at most) and an unsuccessful one is a status, not an error.
        /// </summary>
        public async Task<LeadershipCohortStatusModel> SaveAsync(LeadershipCohortSaveRequest request)
        {
            if (!_store.IsDurable) throw new LeadershipCohortRequestException(HttpStatusCode.Conflict, LeadershipCohortErrorCodes.StateNotDurable);

            var raw = request?.GroupId?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                await _store.SaveSettingsAsync(null);
                _invalidate();
                return await GetStatusAsync();
            }

            if (!Guid.TryParse(raw, out var groupId) || groupId == Guid.Empty)
                throw new LeadershipCohortRequestException(HttpStatusCode.BadRequest, LeadershipCohortErrorCodes.InvalidGroupId);

            await _store.SaveSettingsAsync(new LeadershipCohortSettings
            {
                GroupId = groupId.ToString("D"),
                Revision = Guid.NewGuid().ToString("N"),
                UpdatedUtc = _utcNow(),
            });
            _invalidate();

            // A refresh already running finishes for the previous revision; the importer's next cycle picks this one up.
            await _createRefresher().RefreshAsync();
            _invalidate();
            return await GetStatusAsync();
        }

        public async Task<LeadershipCohortStatusModel> RefreshAsync()
        {
            if (await _store.GetSettingsAsync() == null)
                throw new LeadershipCohortRequestException(HttpStatusCode.Conflict, LeadershipCohortErrorCodes.NotConfigured);

            var result = await _createRefresher().RefreshAsync();
            if (result == null)
                throw new LeadershipCohortRequestException(HttpStatusCode.Conflict, LeadershipCohortErrorCodes.RefreshInProgress);
            _invalidate();
            return await GetStatusAsync();
        }
    }
}
