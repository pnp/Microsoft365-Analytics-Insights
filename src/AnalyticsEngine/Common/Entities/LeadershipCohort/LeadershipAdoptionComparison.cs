using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Common.Entities.LeadershipCohort
{
    /// <summary>
    /// How the configured leadership cohort's Copilot adoption compares with the whole tenant's (#654). Aggregates only:
    /// no names, ids or membership, ever, and no figures at all below <see cref="MinimumCohort"/> licensed leaders.
    /// </summary>
    /// <remarks>
    /// Text is never sent: <see cref="Status"/> and <see cref="Reason"/> are stable keys that the portal and the workbook
    /// turn into sentences. The group's display name is not included either - the report reader learns that a leadership
    /// comparison exists, not which group defines it.
    /// </remarks>
    public sealed class LeadershipAdoptionComparison
    {
        [JsonProperty("status")]
        public string Status { get; set; }

        /// <summary>For <see cref="LeadershipComparisonStatuses.Unavailable"/>: one of <see cref="LeadershipComparisonReasons"/>.</summary>
        [JsonProperty("reason")]
        public string Reason { get; set; }

        /// <summary>The fewest licensed leaders for which any figure is shown.</summary>
        [JsonProperty("minimumCohort")]
        public int MinimumCohort { get; set; }

        [JsonProperty("licensedLeaders")]
        public int? LicensedLeaders { get; set; }

        [JsonProperty("activeLeaders")]
        public int? ActiveLeaders { get; set; }

        [JsonProperty("habitualLeaders")]
        public int? HabitualLeaders { get; set; }

        [JsonProperty("leaderAdoptionRatePct")]
        public double? LeaderAdoptionRatePct { get; set; }

        [JsonProperty("leaderHabitRatePct")]
        public double? LeaderHabitRatePct { get; set; }

        [JsonProperty("leaderAverageScore")]
        public double? LeaderAverageScore { get; set; }

        [JsonProperty("tenantAdoptionRatePct")]
        public double? TenantAdoptionRatePct { get; set; }

        [JsonProperty("tenantHabitRatePct")]
        public double? TenantHabitRatePct { get; set; }

        [JsonProperty("tenantAverageScore")]
        public double? TenantAverageScore { get; set; }

        /// <summary>Leader adoption rate minus the tenant's, in percentage points.</summary>
        [JsonProperty("adoptionGapPts")]
        public double? AdoptionGapPts { get; set; }

        /// <summary>Leader habit rate minus the tenant's, in percentage points.</summary>
        [JsonProperty("habitGapPts")]
        public double? HabitGapPts { get; set; }

        /// <summary>Leader average adoption score minus the tenant's, in score points.</summary>
        [JsonProperty("scoreGap")]
        public double? ScoreGap { get; set; }

        /// <summary>When the membership compared was read from Entra ID.</summary>
        [JsonProperty("membershipRefreshedUtc")]
        public DateTime? MembershipRefreshedUtc { get; set; }

        /// <summary>
        /// True when the licensed-user detail was capped, so some leaders may not have been scored. The figures describe
        /// the leaders that were.
        /// </summary>
        [JsonProperty("figuresIncomplete")]
        public bool FiguresIncomplete { get; set; }

        public static LeadershipAdoptionComparison WithStatus(string status, string reason = null) => new LeadershipAdoptionComparison
        {
            Status = status,
            Reason = reason,
            MinimumCohort = LeadershipAdoptionCalculator.MinimumCohort,
        };
    }

    /// <summary>Values of <see cref="LeadershipAdoptionComparison.Status"/>. A public contract with the portal.</summary>
    public static class LeadershipComparisonStatuses
    {
        /// <summary>No leadership group is configured (the default).</summary>
        public const string NotConfigured = "notConfigured";

        /// <summary>Figures are present.</summary>
        public const string Ok = "ok";

        /// <summary>Fewer licensed leaders than the minimum: no figures, deliberately not even the count.</summary>
        public const string Suppressed = "suppressed";

        /// <summary>The group was configured or changed and its membership has not been read yet.</summary>
        public const string PendingRefresh = "pendingRefresh";

        /// <summary>The membership cannot be used - see <see cref="LeadershipAdoptionComparison.Reason"/>.</summary>
        public const string Unavailable = "unavailable";

        /// <summary>The last successful membership read is older than <see cref="LeadershipAdoptionCalculator.StaleAfter"/>.</summary>
        public const string Stale = "stale";

        /// <summary>
        /// The report is narrowed (a domain, a filter or a global filter). The cohort is compared with the whole tenant
        /// only: inside a narrowed view a handful of leaders could be singled out.
        /// </summary>
        public const string ScopedView = "scopedView";
    }

    /// <summary>Values of <see cref="LeadershipAdoptionComparison.Reason"/>. A public contract with the portal.</summary>
    public static class LeadershipComparisonReasons
    {
        public const string GroupNotFound = "groupNotFound";
        public const string PermissionMissing = "permissionMissing";
        public const string TooLarge = "tooLarge";
        public const string RefreshFailed = "refreshFailed";
        public const string StateUnavailable = "stateUnavailable";

        /// <summary>A refresh was being written while the members were read. Clears on the next request.</summary>
        public const string MembershipChanging = "membershipChanging";

        public static string ForRefreshStatus(string status)
        {
            switch (status)
            {
                case LeadershipCohortRefreshStatuses.GroupNotFound: return GroupNotFound;
                case LeadershipCohortRefreshStatuses.PermissionMissing: return PermissionMissing;
                case LeadershipCohortRefreshStatuses.TooLarge: return TooLarge;
                default: return RefreshFailed;
            }
        }
    }

    /// <summary>The leadership comparison's arithmetic, kept free of storage so it can be tested exhaustively.</summary>
    public static class LeadershipAdoptionCalculator
    {
        /// <summary>
        /// The fewest licensed leaders for which figures are shown, to every reader including See PII holders. Twice the
        /// default department minimum (<see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/>, 5) and not configurable
        /// downwards: a leadership team is small, well known and senior, so a rate over five of them is close to naming
        /// them. Raising <c>MinSeatsPerSegment</c> above 10 raises this too.
        /// </summary>
        public const int MinimumCohort = 10;

        /// <summary>Membership older than this is not compared: someone may have joined or left the leadership team.</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(72);

        public static int EffectiveMinimum(CopilotAdoptionOptions options) => Math.Max(MinimumCohort, options?.MinSeatsPerSegment ?? 0);

        /// <summary>
        /// Compares <paramref name="leaders"/> (SQL user ids) with the analysis's whole population. O(licensed users).
        /// </summary>
        public static LeadershipAdoptionComparison Compare(CopilotAdoptionAnalysis analysis, IReadOnlyCollection<int> leaders, DateTime? membershipRefreshedUtc)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            var summary = analysis.Summary ?? new CopilotAdoptionSummary();
            var minimum = EffectiveMinimum(summary.Options);
            var members = leaders as ISet<int> ?? new HashSet<int>(leaders ?? Array.Empty<int>());

            var count = 0;
            var active = 0;
            var habitual = 0;
            var scoreTotal = 0d;
            foreach (var user in analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>())
            {
                if (user == null || !members.Contains(user.UserId)) continue;
                count++;
                if (user.Band > AdoptionBand.Dormant) active++;
                if (CopilotAdoptionScoring.IsHabitual(user.Band)) habitual++;
                scoreTotal += user.AdoptionScore;
            }

            if (count < minimum)
            {
                var suppressed = LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.Suppressed);
                suppressed.MinimumCohort = minimum;
                suppressed.MembershipRefreshedUtc = membershipRefreshedUtc;
                return suppressed;
            }

            var leaderAdoption = CopilotAdoptionScoring.Percentage(active, count);
            var leaderHabit = CopilotAdoptionScoring.Percentage(habitual, count);
            var leaderScore = Math.Round(scoreTotal / count, 1, MidpointRounding.AwayFromZero);

            return new LeadershipAdoptionComparison
            {
                Status = LeadershipComparisonStatuses.Ok,
                MinimumCohort = minimum,
                LicensedLeaders = count,
                ActiveLeaders = active,
                HabitualLeaders = habitual,
                LeaderAdoptionRatePct = leaderAdoption,
                LeaderHabitRatePct = leaderHabit,
                LeaderAverageScore = leaderScore,
                TenantAdoptionRatePct = summary.AdoptionRatePct,
                TenantHabitRatePct = summary.HabitRatePct,
                TenantAverageScore = summary.AverageAdoptionScore,
                AdoptionGapPts = Gap(leaderAdoption, summary.AdoptionRatePct),
                HabitGapPts = Gap(leaderHabit, summary.HabitRatePct),
                ScoreGap = Gap(leaderScore, summary.AverageAdoptionScore),
                MembershipRefreshedUtc = membershipRefreshedUtc,
                FiguresIncomplete = analysis.LicensedUsersCapped,
            };
        }

        private static double Gap(double leader, double tenant) => Math.Round(leader - tenant, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Supplies the leadership comparison for a Copilot Adoption analysis, reading the stored membership at most once per
    /// refresh and computing the comparison at most once per analysis and membership version.
    /// </summary>
    /// <remarks>
    /// <para>Per request this costs two cached reads and a dictionary lookup. The settings and the snapshot header are
    /// re-read at most every <see cref="HeaderCacheDuration"/> (a failed read every <see cref="FailureCacheDuration"/>),
    /// the member pages only when the snapshot's version changes, and the comparison is computed once per cached
    /// analysis instance and membership version - so the O(licensed users) pass runs once, not per reader.</para>
    /// <para>The cached analysis and summary are never modified. The controller attaches the result to a copy
    /// (<see cref="CopilotAdoptionSummary.WithLeadershipComparison"/>).</para>
    /// </remarks>
    public sealed class LeadershipComparisonProvider
    {
        public static readonly TimeSpan HeaderCacheDuration = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(15);

        private static readonly InMemoryKeyValueStore ProcessFallbackStore = new InMemoryKeyValueStore();
        private static readonly object DefaultLock = new object();
        private static LeadershipComparisonProvider _default;

        private readonly Func<LeadershipCohortStore> _openStore;
        private readonly Func<DateTime> _utcNow;
        private readonly object _lock = new object();
        private readonly ConditionalWeakTable<CopilotAdoptionAnalysis, ConcurrentDictionary<string, LeadershipAdoptionComparison>> _comparisons =
            new ConditionalWeakTable<CopilotAdoptionAnalysis, ConcurrentDictionary<string, LeadershipAdoptionComparison>>();

        private State _state;

        public LeadershipComparisonProvider(Func<LeadershipCohortStore> openStore, Func<DateTime> utcNow = null)
        {
            _openStore = openStore ?? throw new ArgumentNullException(nameof(openStore));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>The store for this deployment: the <c>AnalyticsState</c> table, or a per-process store without Storage.</summary>
        public static LeadershipCohortStore OpenStore(AppConfig config)
        {
            var values = StateStore.TryOpen(config, StatePartitions.LeadershipCohort);
            return values != null
                ? new LeadershipCohortStore(values, isDurable: true)
                : new LeadershipCohortStore(ProcessFallbackStore, isDurable: false);
        }

        /// <summary>The process-wide provider, reading the deployment's store.</summary>
        public static LeadershipComparisonProvider Default
        {
            get
            {
                lock (DefaultLock)
                {
                    if (_default == null)
                    {
                        var config = new AppConfig();
                        var store = OpenStore(config);
                        _default = new LeadershipComparisonProvider(() => store);
                    }
                    return _default;
                }
            }
        }

        /// <summary>Forgets the cached settings and membership, after a save or a refresh in this process.</summary>
        public void Invalidate()
        {
            lock (_lock) _state = null;
        }

        /// <summary>The comparison for an analysis. Never throws for a storage failure: that is a status.</summary>
        public async Task<LeadershipAdoptionComparison> GetAsync(CopilotAdoptionAnalysis analysis, bool scopedView)
        {
            if (analysis == null) return null;
            var state = await GetStateAsync().ConfigureAwait(false);

            if (state.Failed) return LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.Unavailable, LeadershipComparisonReasons.StateUnavailable);
            if (state.Settings == null) return LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.NotConfigured);
            if (scopedView) return LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.ScopedView);

            var snapshot = state.Snapshot;
            if (snapshot == null || !string.Equals(snapshot.SettingsRevision, state.Settings.Revision, StringComparison.Ordinal))
                return LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.PendingRefresh);
            if (!snapshot.IsReady)
                return LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.Unavailable, LeadershipComparisonReasons.ForRefreshStatus(snapshot.Status));

            if (!snapshot.RefreshedUtc.HasValue || _utcNow() - snapshot.RefreshedUtc.Value > LeadershipAdoptionCalculator.StaleAfter)
            {
                var stale = LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.Stale);
                stale.MembershipRefreshedUtc = snapshot.RefreshedUtc;
                return stale;
            }

            if (state.Members == null)
                return LeadershipAdoptionComparison.WithStatus(LeadershipComparisonStatuses.Unavailable, LeadershipComparisonReasons.MembershipChanging);

            var perAnalysis = _comparisons.GetValue(analysis, _ => new ConcurrentDictionary<string, LeadershipAdoptionComparison>(StringComparer.Ordinal));
            return perAnalysis.GetOrAdd(snapshot.Version, _ => LeadershipAdoptionCalculator.Compare(analysis, state.Members, snapshot.RefreshedUtc));
        }

        private async Task<State> GetStateAsync()
        {
            var now = _utcNow();
            State previous;
            lock (_lock)
            {
                previous = _state;
                if (previous != null && now < previous.ExpiresUtc) return previous;
            }

            State next;
            try
            {
                var store = _openStore();
                var settings = await store.GetSettingsAsync().ConfigureAwait(false);
                var snapshot = settings == null ? null : await store.GetSnapshotAsync().ConfigureAwait(false);
                IReadOnlyCollection<int> members = null;
                if (snapshot != null && snapshot.IsReady)
                {
                    members = previous != null && previous.Members != null && previous.Snapshot != null
                        && string.Equals(previous.Snapshot.Version, snapshot.Version, StringComparison.Ordinal)
                        ? previous.Members
                        : await store.ReadMembersAsync(snapshot).ConfigureAwait(false);
                }
                // A version mismatch (a refresh mid-write) is retried soon rather than held for the full minute.
                var hold = snapshot != null && snapshot.IsReady && members == null ? FailureCacheDuration : HeaderCacheDuration;
                next = new State { Settings = settings, Snapshot = snapshot, Members = members, ExpiresUtc = now + hold };
            }
            catch (Exception)
            {
                // Including LeadershipCohortStateUnavailableException: "unknown" is reported as unavailable, never as a
                // previous answer and never as "not configured".
                next = new State { Failed = true, ExpiresUtc = now + FailureCacheDuration };
            }

            lock (_lock) _state = next;
            return next;
        }

        private sealed class State
        {
            public bool Failed { get; set; }
            public LeadershipCohortSettings Settings { get; set; }
            public LeadershipCohortSnapshot Snapshot { get; set; }
            public IReadOnlyCollection<int> Members { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }
    }
}
