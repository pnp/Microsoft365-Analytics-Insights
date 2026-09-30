using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Teams
{
    /// <summary>
    /// Azure AI Language results for Teams messages, kept for a day so the same text is not analysed - and billed -
    /// twice when a channel is re-read. Held in the <see cref="StatePartitions.CognitiveCache"/> partition of the runtime
    /// state table (in memory when no Storage connection string is configured).
    /// </summary>
    /// <remarks>
    /// A cache must never break the thing it speeds up, so every failure here is a miss: an unreachable store means the
    /// text is analysed again, exactly as if nothing had been cached. The key is the SHA-256 of the analysed text, so the
    /// row key never contains message content (the cached result does - it is the analysis of that content).
    /// </remarks>
    public class CognitiveResultCache
    {
        /// <summary>How long a result is reused.</summary>
        public static readonly TimeSpan TimeToLive = TimeSpan.FromDays(1);

        /// <summary>Expired results nobody asked for again are purged at most this often, per process.</summary>
        private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);

        private static readonly Lazy<CognitiveResultCache> ForThisDeployment = new Lazy<CognitiveResultCache>(() =>
            new CognitiveResultCache(StateStore.TryOpen(new AppConfig(), StatePartitions.CognitiveCache) ?? new InMemoryKeyValueStore()),
            LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly IKeyValueStore _store;
        private readonly Func<DateTime> _utcNow;
        private long _lastPurgeUtcTicks;

        public CognitiveResultCache(IKeyValueStore store, Func<DateTime> utcNow = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);

            // Zero, so the first successful write after a (re)start purges: a web-job redeployed more often than daily
            // would otherwise never reach the interval.
            _lastPurgeUtcTicks = 0;
        }

        /// <summary>The cache for this deployment, shared by every channel in the process.</summary>
        public static CognitiveResultCache Default => ForThisDeployment.Value;

        /// <summary>The cache key for an analysed text.</summary>
        public static string KeyFor(string analysedText) => AzureTableKeyValueStore.Sha256Hex(analysedText);

        /// <summary>The cached result for <paramref name="analysedText"/>, or <c>null</c> on a miss or any failure.</summary>
        public async Task<string> GetAsync(string analysedText, ILogger logger)
        {
            try
            {
                return await _store.GetStringAsync(KeyFor(analysedText));
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Cognitive result cache: could not read from {_store.Description} ({ex.Message}); analysing the text again.");
                return null;
            }
        }

        /// <summary>Caches <paramref name="resultJson"/> for a day. Failures are logged and otherwise ignored.</summary>
        public async Task SetAsync(string analysedText, string resultJson, ILogger logger)
        {
            try
            {
                await _store.SetStringAsync(KeyFor(analysedText), resultJson, TimeToLive);
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Cognitive result cache: could not save to {_store.Description} ({ex.Message}); the text will be analysed again if it is seen again.");
                return;
            }

            await PurgeExpiredIfDueAsync(logger);
        }

        private async Task PurgeExpiredIfDueAsync(ILogger logger)
        {
            // Azure Tables has no native expiry: a result read again after its day is deleted then, but one that is never
            // read again would stay for ever. The in-memory store sweeps itself.
            var tableStore = _store as AzureTableKeyValueStore;
            if (tableStore == null) return;

            var now = _utcNow();
            var last = Interlocked.Read(ref _lastPurgeUtcTicks);
            if (now.Ticks - last < PurgeInterval.Ticks) return;
            if (Interlocked.CompareExchange(ref _lastPurgeUtcTicks, now.Ticks, last) != last) return;

            try
            {
                var purged = await tableStore.PurgeExpiredAsync();
                if (purged > 0)
                {
                    logger?.LogInformation($"Cognitive result cache: purged {purged:N0} expired result(s) from {_store.Description}.");
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Cognitive result cache: could not purge expired results from {_store.Description} ({ex.Message}); will try again later.");
            }
        }
    }
}
