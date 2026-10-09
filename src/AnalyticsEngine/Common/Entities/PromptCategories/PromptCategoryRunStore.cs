using Common.Entities.Config;
using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.PromptCategories
{
    public sealed class PromptCategoryRunRecord
    {
        [JsonProperty("startedUtc")] public DateTime StartedUtc { get; set; }
        [JsonProperty("counters")] public PromptCategoryRun Counters { get; set; }
    }

    /// <summary>
    /// The most recent content-free classification counters, kept in the <c>PromptCategories</c> partition
    /// of the AnalyticsState table rather than SQL: it is operational state, not business data, and nothing
    /// joins to it. One entity holds a bounded list (about 3 KB), so reading it is a single point read.
    /// Only the importer writes it, one cycle at a time, so read-modify-write needs no concurrency control.
    /// </summary>
    public sealed class PromptCategoryRunStore
    {
        public const int MaxRuns = 10;
        private const string Key = "runs";
        private static readonly InMemoryKeyValueStore Fallback = new InMemoryKeyValueStore();
        private readonly IKeyValueStore _store;

        /// <summary>False when no Storage connection string is configured, so only this process would see what is written.</summary>
        public bool IsDurable { get; }

        public PromptCategoryRunStore(IKeyValueStore store)
        {
            _store = store ?? Fallback;
            IsDurable = store != null;
        }

        public static PromptCategoryRunStore Open(AppConfig settings) =>
            new PromptCategoryRunStore(StateStore.TryOpen(settings, StatePartitions.PromptCategories));

        public async Task AppendAsync(DateTime startedUtc, PromptCategoryRun counters, CancellationToken cancellationToken = default)
        {
            var runs = await ReadAsync(cancellationToken);
            runs.Add(new PromptCategoryRunRecord { StartedUtc = DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc), Counters = counters });
            var kept = runs.OrderByDescending(r => r.StartedUtc).Take(MaxRuns).ToList();
            await _store.SetStringAsync(Key, JsonConvert.SerializeObject(kept), cancellationToken: cancellationToken);
        }

        /// <summary>Newest first.</summary>
        public async Task<IReadOnlyList<PromptCategoryRunRecord>> RecentAsync(CancellationToken cancellationToken = default) =>
            (await ReadAsync(cancellationToken)).OrderByDescending(r => r.StartedUtc).Take(MaxRuns).ToList();

        private async Task<List<PromptCategoryRunRecord>> ReadAsync(CancellationToken cancellationToken)
        {
            var raw = await _store.GetStringAsync(Key, cancellationToken);
            if (string.IsNullOrEmpty(raw)) return new List<PromptCategoryRunRecord>();
            try { return JsonConvert.DeserializeObject<List<PromptCategoryRunRecord>>(raw) ?? new List<PromptCategoryRunRecord>(); }
            catch (JsonException) { return new List<PromptCategoryRunRecord>(); }
        }
    }
}
