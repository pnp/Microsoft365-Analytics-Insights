using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    /// <summary>
    /// Exact outcomes of daily user-detail imports, independent of fact-row presence and cadence stamps.
    /// Keys are v1:{source}:yyyyMMdd, in UsageReportCoverage; no user identifiers are stored.
    /// Only a whole, identifiable, tenant-wide report saved after the settlement delay certifies a day.
    /// Older cadence stamps are deliberately not promoted: they contain neither dates nor scope.
    /// This records importer commits, not SQL recovery: clear this partition after restoring/replacing
    /// the usage dataset. It cannot detect manual SQL deletion or out-of-band database replacement.
    /// </summary>
    public sealed class UsageReportCoverageStore
    {
        public const int MaximumDays = 366;
        public const int ReadConcurrency = 8;
        public static readonly string[] Sources = { "teams", "outlook", "sharepoint", "onedrive" };
        private static readonly InMemoryKeyValueStore ProcessFallback = new InMemoryKeyValueStore();
        private readonly IKeyValueStore _store;
        private readonly bool _durable;
        private readonly TimeSpan _readTimeout;

        public UsageReportCoverageStore(IKeyValueStore store, bool durable, TimeSpan? readTimeout = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _durable = durable;
            _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
        }

        public static UsageReportCoverageStore Open(AppConfig config, ILogger logger = null)
        {
            var store = StateStore.TryOpen(config, StatePartitions.UsageReportCoverage, logger);
            return new UsageReportCoverageStore(store ?? ProcessFallback, store != null);
        }

        private static string Key(string source, DateTime day)
        {
            if (!Sources.Contains(source)) throw new ArgumentException("Unknown usage-report source.", nameof(source));
            return "v1:" + source + ":" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Invalidate BEFORE any SQL write. An unreachable durable store throws: allowing the write would leave
        /// a previous success certifying a partly overwritten day. A failed download before this changes nothing.
        /// </summary>
        public Task BeginDayAsync(string source, DateTime day)
            => _store.SetStringAsync(Key(source, day), "partial", TimeSpan.FromDays(400));

        public Task CompleteDayAsync(string source, DateTime day, bool wholeTenant, bool allRowsSaved,
            bool empty, DateTime importedUtc, int settlementDays)
        {
            var settled = day.Date <= importedUtc.Date.AddDays(-Math.Max(1, settlementDays));
            var status = !wholeTenant ? "scopeUnverified"
                : !allRowsSaved || !settled ? "partial"
                : empty ? "empty" : "complete";
            return _store.SetStringAsync(Key(source, day), status, TimeSpan.FromDays(400));
        }

        /// <summary>
        /// At most 4 x 366 point reads, eight in flight, once per analysis (not per user or nested scope).
        /// The analysis/result cache carries this snapshot into API pages and exports. A source outage stops
        /// further reads for that source and cannot fall back to process-local successes.
        /// </summary>
        public async Task<List<UsageReportCoverage>> ReadAsync(DateTime from, DateTime through,
            CancellationToken cancellationToken = default)
        {
            var count = (through.Date - from.Date).Days + 1;
            var results = Sources.Select(source => new UsageReportCoverage
            {
                Source = source, FromUtc = from.Date, ThroughUtc = through.Date,
                ExpectedDays = Math.Max(0, count),
                Reason = !_durable ? "durableStateMissing"
                    : count <= 0 || count > MaximumDays ? "windowUnsupported" : "outcomesMissing",
            }).ToList();
            if (!_durable || count <= 0 || count > MaximumDays) return results;

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var gate = new SemaphoreSlim(ReadConcurrency))
            {
                deadline.CancelAfter(_readTimeout);
                await Task.WhenAll(results.Select(async result =>
                {
                    try
                    {
                        for (var offset = 0; offset < count; offset += ReadConcurrency)
                        {
                            var days = Enumerable.Range(offset, Math.Min(ReadConcurrency, count - offset));
                            var outcomes = await Task.WhenAll(days.Select(async day =>
                            {
                                await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
                                try
                                {
                                    return await _store.GetStringAsync(Key(result.Source, from.Date.AddDays(day)),
                                        deadline.Token).ConfigureAwait(false);
                                }
                                finally { gate.Release(); }
                            })).ConfigureAwait(false);
                            foreach (var outcome in outcomes)
                            {
                                if (outcome == "complete" || outcome == "empty") result.VerifiedDays++;
                                if (outcome == "empty") result.EmptyDays++;
                                if (outcome == "partial") result.PartialDays++;
                                if (outcome == "scopeUnverified") result.ScopeUnverifiedDays++;
                            }
                        }
                        result.Status = result.VerifiedDays == count
                            ? result.EmptyDays == count ? "empty" : "complete"
                            : result.VerifiedDays + result.PartialDays > 0 ? "partial" : "unverified";
                        result.Reason = result.IsComplete ? null
                            : result.ScopeUnverifiedDays > 0 ? "scopeUnverified" : "outcomesMissing";
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        result.Status = "unverified";
                        result.Reason = "durableStateUnavailable";
                    }
                })).ConfigureAwait(false);
            }
            return results;
        }
    }

    public sealed class UsageReportCoverage
    {
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("status")] public string Status { get; set; } = "unverified";
        [JsonProperty("reason")] public string Reason { get; set; }
        [JsonProperty("fromUtc")] public DateTime FromUtc { get; set; }
        [JsonProperty("throughUtc")] public DateTime ThroughUtc { get; set; }
        [JsonProperty("expectedDays")] public int ExpectedDays { get; set; }
        [JsonProperty("verifiedDays")] public int VerifiedDays { get; set; }
        [JsonProperty("emptyDays")] public int EmptyDays { get; set; }
        [JsonProperty("partialDays")] public int PartialDays { get; set; }
        [JsonProperty("scopeUnverifiedDays")] public int ScopeUnverifiedDays { get; set; }
        [JsonIgnore] public bool IsComplete => Status == "complete" || Status == "empty";
    }
}
