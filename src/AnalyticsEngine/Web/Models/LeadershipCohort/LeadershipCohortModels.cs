using Common.Entities.Config;
using Common.Entities.LeadershipCohort;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Threading;
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
        internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private readonly LeadershipCohortStore _store;
        private readonly Action _invalidate;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _requestTimeout;

        public LeadershipCohortService(LeadershipCohortStore store, Action invalidate, Func<DateTime> utcNow = null, TimeSpan? requestTimeout = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _invalidate = invalidate ?? (() => { });
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _requestTimeout = requestTimeout ?? RequestTimeout;
            if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > RequestTimeout)
                throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        public static LeadershipCohortService ForThisDeployment()
        {
            var config = new AppConfig();
            var store = LeadershipComparisonProvider.OpenStore(config);
            return new LeadershipCohortService(
                store,
                LeadershipComparisonProvider.Default.Invalidate);
        }

        public Task<LeadershipCohortStatusModel> GetStatusAsync(CancellationToken cancellationToken = default) =>
            WithinDeadlineAsync(GetStatusCoreAsync, cancellationToken);

        private async Task<LeadershipCohortStatusModel> GetStatusCoreAsync(CancellationToken cancellationToken)
        {
            var settings = await _store.GetSettingsAsync(cancellationToken);
            var snapshot = settings == null ? null : await _store.GetSnapshotAsync(cancellationToken);
            var request = settings == null ? null : await _store.GetRefreshRequestAsync(cancellationToken);
            if (snapshot != null && !string.Equals(snapshot.SettingsRevision, settings.Revision, StringComparison.Ordinal)) snapshot = null;
            if (LeadershipCohortRefresher.IsPending(settings, snapshot, request)) snapshot = null;

            return Status(settings, snapshot);
        }

        private LeadershipCohortStatusModel Status(LeadershipCohortSettings settings, LeadershipCohortSnapshot snapshot = null)
        {
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
        /// Saves the group (or clears it). A new durable revision requests an importer refresh: IsDue compares it with
        /// the snapshot revision, so a restart cannot lose the request. Success means settings saved, not members read.
        /// Never starts Graph/SQL work in the HTTP request; its retries can exceed App Service's HTTP deadline.
        /// </summary>
        public Task<LeadershipCohortStatusModel> SaveAsync(LeadershipCohortSaveRequest request, CancellationToken cancellationToken = default) =>
            WithinDeadlineAsync(token => SaveCoreAsync(request, token), cancellationToken);

        private async Task<LeadershipCohortStatusModel> SaveCoreAsync(LeadershipCohortSaveRequest request, CancellationToken cancellationToken)
        {
            if (!_store.IsDurable) throw new LeadershipCohortRequestException(HttpStatusCode.Conflict, LeadershipCohortErrorCodes.StateNotDurable);

            var raw = request?.GroupId?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                await _store.SaveSettingsAsync(null, cancellationToken);
                _invalidate();
                return Status(null);
            }

            if (!Guid.TryParse(raw, out var groupId) || groupId == Guid.Empty)
                throw new LeadershipCohortRequestException(HttpStatusCode.BadRequest, LeadershipCohortErrorCodes.InvalidGroupId);

            var settings = new LeadershipCohortSettings
            {
                GroupId = groupId.ToString("D"),
                Revision = Guid.NewGuid().ToString("N"),
                UpdatedUtc = _utcNow(),
            };
            await _store.SaveSettingsAsync(settings, cancellationToken);
            _invalidate();

            // Return the acknowledged write without another storage read that could turn a saved setting into an error.
            return Status(settings);
        }

        public Task<LeadershipCohortStatusModel> RefreshAsync(CancellationToken cancellationToken = default) =>
            WithinDeadlineAsync(RefreshCoreAsync, cancellationToken);

        private async Task<LeadershipCohortStatusModel> RefreshCoreAsync(CancellationToken cancellationToken)
        {
            var settings = await _store.GetSettingsAsync(cancellationToken);
            if (settings == null)
                throw new LeadershipCohortRequestException(HttpStatusCode.Conflict, LeadershipCohortErrorCodes.NotConfigured);
            if (!_store.IsDurable)
                throw new LeadershipCohortRequestException(HttpStatusCode.Conflict, LeadershipCohortErrorCodes.StateNotDurable);

            await _store.RequestRefreshAsync(new LeadershipCohortRefreshRequest
            {
                Id = Guid.NewGuid().ToString("N"),
            }, cancellationToken);
            _invalidate();
            return Status(settings);
        }

        private async Task<LeadershipCohortStatusModel> WithinDeadlineAsync(
            Func<CancellationToken, Task<LeadershipCohortStatusModel>> action, CancellationToken cancellationToken)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(_requestTimeout);
                try { return await action(deadline.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    throw new LeadershipCohortRequestException(HttpStatusCode.ServiceUnavailable, LeadershipCohortErrorCodes.StateUnavailable);
                }
            }
        }
    }
}
