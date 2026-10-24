using Common.Entities.State;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>A user the import quarantined, with the latest Graph profile it has for them.</summary>
    internal sealed class QuarantinedUserIdentity
    {
        [JsonProperty("objectId")]
        public string ObjectId { get; set; }

        [JsonProperty("kind")]
        [JsonConverter(typeof(StringEnumConverter))]
        public UserIdentityConflictKind Kind { get; set; }

        /// <summary>When the user was first quarantined, continuously.</summary>
        [JsonProperty("sinceUtc")]
        public DateTime SinceUtc { get; set; }

        /// <summary>
        /// Their latest profile from <c>/users/delta</c>, written through the normal path once the conflict is resolved.
        /// </summary>
        [JsonProperty("profile")]
        public GraphUser Profile { get; set; }
    }

    /// <summary>Where quarantined users are kept between cycles.</summary>
    internal interface IUserIdentityQuarantineStore
    {
        string Description { get; }

        /// <summary>Every stored entry. Throws when the store cannot be read: an empty answer must mean "none".</summary>
        Task<List<QuarantinedUserIdentity>> LoadAsync();

        /// <summary>Replaces the stored set. Throws when it cannot be saved.</summary>
        Task SaveAsync(IReadOnlyCollection<QuarantinedUserIdentity> entries);
    }

    /// <summary>
    /// The quarantined users in <see cref="StatePartitions.UserIdentityQuarantine"/>: a header row
    /// (<see cref="HeaderKey"/>) plus pages of at most <see cref="MaxEntriesPerPage"/> entries (<see cref="PageKey"/>).
    /// </summary>
    /// <remarks>
    /// Pages are written before the header, and pages a smaller set no longer needs are deleted after it, so a failed
    /// save leaves the previous header pointing at pages that still exist. A missing page fails the load rather than
    /// reading as fewer users. Bounded at <see cref="MaxEntries"/>: a page is at most a few hundred KB of JSON, well
    /// within one compressed table entity.
    /// </remarks>
    internal sealed class UserIdentityQuarantineStore : IUserIdentityQuarantineStore
    {
        internal const int MaxEntriesPerPage = 250;
        internal const int MaxEntries = 10000;
        private const int PageReadConcurrency = 16;

        internal static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        private readonly IKeyValueStore _store;
        private readonly Guid _tenantId;

        public UserIdentityQuarantineStore(IKeyValueStore store, Guid tenantId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _tenantId = tenantId;
        }

        public string Description => _store.Description;

        internal static string HeaderKey(Guid tenantId) => "IdentityQuarantine-" + tenantId.ToString("D");

        internal static string PageKey(Guid tenantId, int page) => HeaderKey(tenantId) + ":" + page.ToString(CultureInfo.InvariantCulture);

        public async Task<List<QuarantinedUserIdentity>> LoadAsync()
        {
            var header = ParseHeader(await _store.GetStringAsync(HeaderKey(_tenantId)));
            var entries = new List<QuarantinedUserIdentity>(header?.Count ?? 0);
            if (header == null)
                return entries;

            for (var first = 0; first < header.Pages; first += PageReadConcurrency)
            {
                var batch = Enumerable.Range(first, Math.Min(PageReadConcurrency, header.Pages - first)).ToList();
                var pages = await Task.WhenAll(batch.Select(p => _store.GetStringAsync(PageKey(_tenantId, p))));
                for (var i = 0; i < pages.Length; i++)
                {
                    if (pages[i] == null)
                        throw new InvalidDataException($"page {batch[i] + 1} of {header.Pages} of the identity quarantine is missing");
                    entries.AddRange(JsonConvert.DeserializeObject<List<QuarantinedUserIdentity>>(pages[i], Json) ?? new List<QuarantinedUserIdentity>());
                }
            }
            return entries;
        }

        public async Task SaveAsync(IReadOnlyCollection<QuarantinedUserIdentity> entries)
        {
            var sorted = (entries ?? new QuarantinedUserIdentity[0])
                .Where(e => e != null && !string.IsNullOrEmpty(e.ObjectId))
                .OrderBy(e => e.ObjectId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var pageCount = (sorted.Count + MaxEntriesPerPage - 1) / MaxEntriesPerPage;
            var previousPageCount = ParseHeaderLeniently(await _store.GetStringAsync(HeaderKey(_tenantId)))?.Pages ?? 0;

            for (var p = 0; p < pageCount; p++)
            {
                var page = sorted.GetRange(p * MaxEntriesPerPage, Math.Min(MaxEntriesPerPage, sorted.Count - p * MaxEntriesPerPage));
                await _store.SetStringAsync(PageKey(_tenantId, p), JsonConvert.SerializeObject(page, Json));
            }

            if (pageCount == 0)
                await _store.DeleteAsync(HeaderKey(_tenantId));
            else
                await _store.SetStringAsync(HeaderKey(_tenantId), JsonConvert.SerializeObject(new Header { Pages = pageCount, Count = sorted.Count }, Json));

            for (var p = pageCount; p < previousPageCount; p++)
                await _store.DeleteAsync(PageKey(_tenantId, p));
        }

        private static Header ParseHeader(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;
            var header = JsonConvert.DeserializeObject<Header>(raw, Json);
            if (header != null && (header.Pages < 0 || header.Count < 0))
                throw new InvalidDataException($"the identity quarantine header says {header.Pages} page(s) and {header.Count} entries");
            return header;
        }

        private static Header ParseHeaderLeniently(string raw)
        {
            try
            {
                return ParseHeader(raw);
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException)
            {
                return null;
            }
        }

        private sealed class Header
        {
            [JsonProperty("pages")]
            public int Pages { get; set; }

            [JsonProperty("count")]
            public int Count { get; set; }
        }
    }

    /// <summary>What <see cref="UserIdentityQuarantineRules.Merge"/> did with the stored entries.</summary>
    internal sealed class UserIdentityQuarantineMerge
    {
        /// <summary>Stored entries checked again this cycle, from the store or from a newer entry in this read.</summary>
        public int Retried { get; set; }

        /// <summary>Stored entries whose profile this read replaced with a newer one.</summary>
        public int Superseded { get; set; }

        /// <summary>Stored entries dropped: the account was deleted, or is now outside <c>UserGroupsFilter</c>.</summary>
        public int Dropped { get; set; }

        /// <summary>Stored entries kept as they are because a user in this read now has their sign-in name.</summary>
        public List<QuarantinedUserIdentity> KeptUnchanged { get; } = new List<QuarantinedUserIdentity>();

        /// <summary>Stored entries that took part in this cycle, by object id.</summary>
        public Dictionary<string, QuarantinedUserIdentity> Considered { get; } = new Dictionary<string, QuarantinedUserIdentity>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>How quarantined users are carried from one cycle to the next. No SQL, no Graph, no storage.</summary>
    /// <remarks>
    /// Why moving the <c>/users/delta</c> checkpoint on cannot lose a quarantined user's changes: the checkpoint only
    /// moves once their latest profile from the read has been saved here; every later cycle checks that profile again
    /// beside the read; a newer entry for them in a later read replaces it, keeping a manager change the newer entry does
    /// not repeat (incremental entries leave out an unchanged manager); and it is only removed once it has been written
    /// by a cycle whose checkpoint moves.
    /// </remarks>
    internal static class UserIdentityQuarantineRules
    {
        /// <summary>
        /// Adds each stored profile to this cycle's users unless this read has a newer one for them.
        /// </summary>
        /// <param name="inScope">Whether a profile is in <c>UserGroupsFilter</c>; out-of-scope users are never written.</param>
        public static UserIdentityQuarantineMerge Merge(IReadOnlyList<QuarantinedUserIdentity> stored, List<GraphUser> active, List<GraphUser> disabled,
            GraphUserDeactivations deactivations, Func<GraphUser, bool> inScope)
        {
            var merge = new UserIdentityQuarantineMerge();
            if (stored == null || stored.Count == 0)
                return merge;

            var positions = new Dictionary<string, Tuple<List<GraphUser>, int>>(StringComparer.OrdinalIgnoreCase);
            var nameOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in new[] { active, disabled })
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var user = list[i];
                    if (!string.IsNullOrEmpty(user?.Id))
                        positions[user.Id] = Tuple.Create(list, i);
                    if (!string.IsNullOrEmpty(user?.UserPrincipalName) && !string.IsNullOrEmpty(user.Id))
                        nameOwners[user.UserPrincipalName] = user.Id;
                }
            }
            var removed = new HashSet<string>(deactivations?.RemovedObjectIds ?? new string[0], StringComparer.OrdinalIgnoreCase);

            foreach (var entry in stored)
            {
                var profile = entry?.Profile;
                if (profile == null || string.IsNullOrEmpty(entry.ObjectId) || merge.Considered.ContainsKey(entry.ObjectId))
                    continue;
                if (removed.Contains(entry.ObjectId))
                {
                    merge.Dropped++;
                    continue;
                }

                if (positions.TryGetValue(entry.ObjectId, out var position))
                {
                    var fresh = position.Item1[position.Item2];
                    if (!fresh.ManagerReported && profile.ManagerReported)
                        position.Item1[position.Item2] = fresh.WithManagerFrom(profile);
                    merge.Superseded++;
                    merge.Retried++;
                    merge.Considered[entry.ObjectId] = entry;
                    continue;
                }

                if (inScope != null && !inScope(profile))
                {
                    merge.Dropped++;
                    continue;
                }

                merge.Retried++;
                if (!string.IsNullOrEmpty(profile.UserPrincipalName) && nameOwners.TryGetValue(profile.UserPrincipalName, out var owner)
                    && !string.Equals(owner, entry.ObjectId, StringComparison.OrdinalIgnoreCase))
                {
                    // Graph has given their name to someone in this read, so this profile is out of date; their own newer
                    // entry will come in a later read. Kept, and not written.
                    merge.KeptUnchanged.Add(entry);
                    continue;
                }

                merge.Considered[entry.ObjectId] = entry;
                (profile.AccountEnabled == false ? disabled : active).Add(profile);
                if (!string.IsNullOrEmpty(profile.UserPrincipalName))
                    nameOwners[profile.UserPrincipalName] = entry.ObjectId;
            }
            return merge;
        }

        /// <summary>
        /// The set to store after this cycle: everyone quarantined now, with their latest profile and the time they were
        /// first quarantined - and, when the checkpoint is not moving, every stored entry not yet confirmed written by a
        /// cycle that moves it.
        /// </summary>
        public static List<QuarantinedUserIdentity> Next(UserIdentityQuarantineMerge merge, IReadOnlyDictionary<string, UserIdentityConflict> conflicts,
            IReadOnlyDictionary<string, GraphUser> profiles, DateTime nowUtc, bool checkpointWillMove)
        {
            var next = new Dictionary<string, QuarantinedUserIdentity>(StringComparer.OrdinalIgnoreCase);
            foreach (var conflict in conflicts.Values)
            {
                if (!profiles.TryGetValue(conflict.ObjectId, out var profile))
                    continue;
                merge.Considered.TryGetValue(conflict.ObjectId, out var earlier);
                next[conflict.ObjectId] = new QuarantinedUserIdentity
                {
                    ObjectId = conflict.ObjectId,
                    Kind = conflict.Kind,
                    SinceUtc = earlier?.SinceUtc ?? nowUtc,
                    Profile = profile,
                };
            }

            foreach (var kept in merge.KeptUnchanged)
            {
                if (!next.ContainsKey(kept.ObjectId))
                    next[kept.ObjectId] = kept;
            }

            if (!checkpointWillMove)
            {
                foreach (var considered in merge.Considered.Values)
                {
                    if (!next.ContainsKey(considered.ObjectId))
                        next[considered.ObjectId] = considered;
                }
            }
            return next.Values.ToList();
        }

        /// <summary>True when <paramref name="next"/> stores something other than <paramref name="stored"/>.</summary>
        public static bool Differs(IReadOnlyList<QuarantinedUserIdentity> stored, IReadOnlyList<QuarantinedUserIdentity> next)
        {
            if ((stored?.Count ?? 0) != (next?.Count ?? 0))
                return true;
            if (next == null || next.Count == 0)
                return false;
            var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in stored)
            {
                if (!string.IsNullOrEmpty(entry?.ObjectId))
                    before[entry.ObjectId] = JsonConvert.SerializeObject(entry, UserIdentityQuarantineStore.Json);
            }
            foreach (var entry in next)
            {
                if (!before.TryGetValue(entry.ObjectId, out var json) || json != JsonConvert.SerializeObject(entry, UserIdentityQuarantineStore.Json))
                    return true;
            }
            return false;
        }
    }
}
