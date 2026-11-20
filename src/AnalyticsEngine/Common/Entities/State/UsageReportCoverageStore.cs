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
    /// Day outcomes: partial (in progress / unsettled), incomplete, scopeUnverified, complete, empty.
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

        /// <summary>False for the process-local fallback, whose outcomes never certify a workload.</summary>
        public bool IsDurable => _durable;

        private static string Key(string source, DateTime day)
        {
            if (!Sources.Contains(source)) throw new ArgumentException("Unknown usage-report source.", nameof(source));
            return "v1:" + source + ":" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        /// <summary>In progress, or saved before the day settled: not final, so the importer downloads it again.</summary>
        public const string Partial = "partial";
        /// <summary>Settled, but not every row Graph returned was saved (concealed or blank identities). Final.</summary>
        public const string Incomplete = "incomplete";
        /// <summary>Saved under a user scope (UserGroupsFilter), so it cannot show the whole tenant. Final.</summary>
        public const string ScopeUnverified = "scopeUnverified";
        public const string Complete = "complete";
        public const string Empty = "empty";

        /// <summary>
        /// Marks the day in progress BEFORE any SQL write, so a first download that fails midway is never certified.
        /// A day already certified complete/empty keeps its outcome while it is downloaded again: the upsert
        /// never deletes stored rows, so a re-download cannot leave fewer users stored than were certified.
        /// An unreachable durable store throws, and the SQL write does not start.
        /// </summary>
        public async Task BeginDayAsync(string source, DateTime day)
        {
            var key = Key(source, day);
            var outcome = await _store.GetStringAsync(key).ConfigureAwait(false);
            if (outcome == Complete || outcome == Empty) return;
            await _store.SetStringAsync(key, Partial, TimeSpan.FromDays(400)).ConfigureAwait(false);
        }

        /// <summary>
        /// Records the outcome of a saved day. <paramref name="settlementDays"/> is the analysis' settlement lag
        /// (CopilotAdoptionOptions.UsageReportLagDays, 3 by default): the analysis on day T first assesses
        /// T - lag. A day with rows is certified when saved at least lag - 1 days after it, so the import of
        /// day T - 1 has already certified the newest day the analysis on day T reads, at any time of day T.
        /// A successfully EMPTY day needs the full lag: Graph answers a day it has not published yet with
        /// 200 and no rows, so zero rows inside the lag cannot be told from "not available yet".
        /// </summary>
        public Task CompleteDayAsync(string source, DateTime day, bool wholeTenant, bool allRowsSaved,
            bool empty, DateTime importedUtc, int settlementDays)
        {
            var lag = Math.Max(2, settlementDays);
            var settled = day.Date <= importedUtc.Date.AddDays(-(empty ? lag : lag - 1));
            var status = !wholeTenant ? ScopeUnverified
                : !settled ? Partial
                : !allRowsSaved ? Incomplete
                : empty ? Empty : Complete;
            return _store.SetStringAsync(Key(source, day), status, TimeSpan.FromDays(400));
        }

        /// <summary>
        /// True only for a day with a durable complete or successfully empty outcome.
        /// </summary>
        public async Task<bool> IsDayVerifiedAsync(string source, DateTime day, CancellationToken cancellationToken = default)
        {
            var outcome = await _store.GetStringAsync(Key(source, day), cancellationToken).ConfigureAwait(false);
            return outcome == Complete || outcome == Empty;
        }

        /// <summary>
        /// Whether a stored, finalized day should be downloaded again so it can get a final outcome: only when it
        /// has none (imported before this ledger existed) or is still in progress (a download that never
        /// finished). Final outcomes - complete, empty, incomplete, scopeUnverified - are skipped like before,
        /// so a filtered or concealing tenant does not re-download its whole window every cycle. A
        /// scopeUnverified day is retried once the importer runs tenant-wide again.
        /// </summary>
        public async Task<bool> NeedsReimportAsync(string source, DateTime day, bool wholeTenant,
            CancellationToken cancellationToken = default)
        {
            var outcome = await _store.GetStringAsync(Key(source, day), cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case Complete:
                case Empty:
                case Incomplete:
                    return false;
                case ScopeUnverified:
                    return wholeTenant;
                default:
                    return true;
            }
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
                                if (outcome == Complete || outcome == Empty) result.VerifiedDays++;
                                if (outcome == Empty) result.EmptyDays++;
                                if (outcome == Partial) result.PartialDays++;
                                if (outcome == Incomplete) result.IncompleteDays++;
                                if (outcome == ScopeUnverified) result.ScopeUnverifiedDays++;
                            }
                        }
                        result.Status = result.VerifiedDays == count
                            ? result.EmptyDays == count ? "empty" : "complete"
                            : result.VerifiedDays + result.PartialDays + result.IncompleteDays > 0 ? "partial" : "unverified";
                        result.Reason = result.IsComplete ? null
                            : result.ScopeUnverifiedDays > 0 ? ScopeUnverified
                            : result.IncompleteDays > 0 ? Incomplete : "outcomesMissing";
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
        [JsonProperty("incompleteDays")] public int IncompleteDays { get; set; }
        [JsonProperty("scopeUnverifiedDays")] public int ScopeUnverifiedDays { get; set; }
        [JsonIgnore] public bool IsComplete => Status == "complete" || Status == "empty";
    }
}
