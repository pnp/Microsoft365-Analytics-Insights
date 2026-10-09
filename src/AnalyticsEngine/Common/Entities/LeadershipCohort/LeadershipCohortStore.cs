using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.LeadershipCohort
{
    /// <summary>
    /// The leadership cohort's settings, refresh header and members, in the <see cref="StatePartitions.LeadershipCohort"/>
    /// partition of the <c>AnalyticsState</c> table.
    /// </summary>
    /// <remarks>
    /// <para><b>Shape.</b> <c>Settings</c> holds the configured group. <c>Snapshot</c> is the header of the last refresh.
    /// The members are stored as SQL user ids in pages of at most <see cref="MembersPerPage"/> under
    /// <c>Members:{slot}:{page}</c>: never names, UPNs or object ids, so the stored membership is useless to anyone without
    /// the analytics database, and well inside a table entity's 32K-character string limit (2,000 ids of at most ten digits
    /// plus separators is about 22K characters).</para>
    /// <para><b>Bounded.</b> At most <see cref="MaxMembers"/> members, so at most <see cref="MaxPages"/> pages, in two
    /// alternating slots. A refresh writes the slot the current header does not point at, then the header, then deletes
    /// that slot's surplus pages, so storage never grows beyond 2 x <see cref="MaxPages"/> pages plus three rows
    /// (settings, snapshot and the latest durable refresh request).</para>
    /// <para><b>Coherent.</b> Each page carries the header version it was written for. Two refreshes racing for the same
    /// slot (the importer and an administrator's "Refresh now") can interleave pages, and a reader that finds a page from
    /// another version reports the membership as unavailable instead of comparing a mixture. The next refresh repairs it.</para>
    /// <para>Every store failure is rethrown as <see cref="LeadershipCohortStateUnavailableException"/>: a missing key is
    /// "nothing saved", an unreachable store is "unknown", and callers must never confuse the two.</para>
    /// </remarks>
    public sealed class LeadershipCohortStore
    {
        public const string SettingsKey = "Settings";
        public const string SnapshotKey = "Snapshot";
        public const string RefreshRequestKey = "RefreshRequest";

        /// <summary>Member ids per stored page.</summary>
        public const int MembersPerPage = 2000;

        /// <summary>
        /// The largest group the comparison accepts. A leadership team is tens or hundreds of people; ten thousand is
        /// already a population, not a leadership cohort, and reading it costs about eleven Graph pages.
        /// </summary>
        public const int MaxMembers = 10000;

        /// <summary>Pages per slot at <see cref="MaxMembers"/>.</summary>
        public const int MaxPages = (MaxMembers + MembersPerPage - 1) / MembersPerPage;

        private readonly IKeyValueStore _values;

        public LeadershipCohortStore(IKeyValueStore values, bool isDurable)
        {
            _values = values ?? throw new ArgumentNullException(nameof(values));
            IsDurable = isDurable;
        }

        /// <summary>False when no Storage connection string is configured and this is a per-process in-memory store.</summary>
        public bool IsDurable { get; }

        public static string PageKey(string slot, int page) => "Members:" + slot + ":" + page.ToString("D3", CultureInfo.InvariantCulture);

        public async Task<LeadershipCohortSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            var settings = await ReadAsync<LeadershipCohortSettings>(SettingsKey, cancellationToken).ConfigureAwait(false);
            return settings != null && !string.IsNullOrWhiteSpace(settings.GroupId) ? settings : null;
        }

        /// <summary>Saves the settings, or clears them (turning the feature off) when <paramref name="settings"/> is null.</summary>
        public async Task SaveSettingsAsync(LeadershipCohortSettings settings, CancellationToken cancellationToken = default)
        {
            if (settings == null)
            {
                await WriteAsync(SettingsKey, null, cancellationToken).ConfigureAwait(false);
                return;
            }
            await WriteAsync(SettingsKey, JsonConvert.SerializeObject(settings), cancellationToken).ConfigureAwait(false);
        }

        public Task<LeadershipCohortSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ReadAsync<LeadershipCohortSnapshot>(SnapshotKey, cancellationToken);

        public Task<LeadershipCohortRefreshRequest> GetRefreshRequestAsync(CancellationToken cancellationToken = default) =>
            ReadAsync<LeadershipCohortRefreshRequest>(RefreshRequestKey, cancellationToken);

        public Task RequestRefreshAsync(LeadershipCohortRefreshRequest request, CancellationToken cancellationToken = default) =>
            WriteAsync(RefreshRequestKey, JsonConvert.SerializeObject(request), cancellationToken);

        /// <summary>
        /// Records a refresh that produced no usable members. The previous members are deliberately NOT carried forward:
        /// the header now says the refresh failed, and the comparison says so rather than showing the last good figures.
        /// </summary>
        public async Task SaveFailureAsync(LeadershipCohortSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (snapshot.IsReady) throw new ArgumentException("A ready snapshot must be saved with its members.", nameof(snapshot));
            snapshot.PageCount = 0;
            snapshot.Slot = null;
            await WriteAsync(SnapshotKey, JsonConvert.SerializeObject(snapshot)).ConfigureAwait(false);
        }

        /// <summary>Stores a successful refresh: member pages first, then the header that points at them.</summary>
        public async Task SaveMembersAsync(LeadershipCohortSnapshot snapshot, IReadOnlyList<int> userIds)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!snapshot.IsReady) throw new ArgumentException("Only a ready snapshot has members.", nameof(snapshot));
            var ids = (userIds ?? Array.Empty<int>()).Distinct().OrderBy(i => i).ToList();
            if (ids.Count > MaxMembers) throw new ArgumentOutOfRangeException(nameof(userIds), "More members than the store accepts.");

            var current = await GetSnapshotAsync().ConfigureAwait(false);
            var slot = current?.Slot == "a" ? "b" : "a";
            var pageCount = (ids.Count + MembersPerPage - 1) / MembersPerPage;

            for (var page = 0; page < pageCount; page++)
            {
                var start = page * MembersPerPage;
                var body = new LeadershipCohortMemberPage
                {
                    Version = snapshot.Version,
                    UserIds = ids.GetRange(start, Math.Min(MembersPerPage, ids.Count - start)),
                };
                await WriteAsync(PageKey(slot, page), JsonConvert.SerializeObject(body)).ConfigureAwait(false);
            }

            snapshot.Slot = slot;
            snapshot.PageCount = pageCount;
            await WriteAsync(SnapshotKey, JsonConvert.SerializeObject(snapshot)).ConfigureAwait(false);

            // Surplus pages from an older, larger refresh in this slot. Bounded by MaxPages, so this never walks far.
            for (var page = pageCount; page < MaxPages; page++)
            {
                try
                {
                    await _values.DeleteAsync(PageKey(slot, page)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new LeadershipCohortStateUnavailableException($"Couldn't tidy leadership member pages in {_values.Description}: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// The members of a ready snapshot, or null when a page is missing or belongs to another refresh (a refresh is
        /// being written right now). Never returns a partial set.
        /// </summary>
        public async Task<IReadOnlyCollection<int>> ReadMembersAsync(LeadershipCohortSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsReady || string.IsNullOrEmpty(snapshot.Slot)) return null;
            if (snapshot.PageCount < 0 || snapshot.PageCount > MaxPages) return null;

            var members = new HashSet<int>();
            for (var page = 0; page < snapshot.PageCount; page++)
            {
                var body = await ReadAsync<LeadershipCohortMemberPage>(PageKey(snapshot.Slot, page)).ConfigureAwait(false);
                if (body == null || !string.Equals(body.Version, snapshot.Version, StringComparison.Ordinal)) return null;
                foreach (var id in body.UserIds ?? new List<int>()) members.Add(id);
            }
            return members;
        }

        private async Task<T> ReadAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            string json;
            try
            {
                json = await _values.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new LeadershipCohortStateUnavailableException($"Couldn't read '{key}' from {_values.Description}: {ex.Message}", ex);
            }
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (JsonException ex)
            {
                throw new LeadershipCohortStateUnavailableException($"'{key}' in {_values.Description} is not valid: {ex.Message}", ex);
            }
        }

        private async Task WriteAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            try
            {
                await _values.SetStringAsync(key, value, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                throw new LeadershipCohortStateUnavailableException($"Couldn't save '{key}' to {_values.Description}: {ex.Message}", ex);
            }
        }
    }
}
