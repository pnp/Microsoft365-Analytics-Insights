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
    /// v1:scopePurge holds the active purge run token or its UTC end ticks. A scope purge revokes affected days
    /// before it deletes their facts; downloads overlapping it cannot publish new certificates.
    /// Revoked dates retain their outcome with a |purge or |endTicks suffix rather than expiring into a legacy miss.
    /// Only a whole, identifiable, tenant-wide report saved after the settlement delay certifies a day.
    /// Day outcomes: partial (in progress / unsettled), incomplete, scopeUnverified, complete, empty.
    /// Older cadence stamps are deliberately not promoted: they contain neither dates nor scope.
    /// This records importer commits, not SQL recovery: clear this partition after restoring/replacing
    /// the usage dataset. Daily row presence can detect a contradiction, not every out-of-band change.
    /// </summary>
    public sealed class UsageReportCoverageStore
    {
        public const int MaximumDays = 366;
        public const int ReadConcurrency = 8;
        public static readonly string[] Sources = { "teams", "outlook", "sharepoint", "onedrive" };
        // Viva Engage is weekly-chart only. Its importer does not certify empty/complete days, but
        // a purge must still override the chart's historical row-presence fallback for affected dates.
        public static IReadOnlyList<string> WeeklySources { get; } = Array.AsReadOnly(Sources.Concat(new[] { "yammer" }).ToArray());
        private const string ScopePurgeKey = "v1:scopePurge";
        private const string PendingPurge = "purge";
        private static readonly TimeSpan Retention = TimeSpan.FromDays(400);
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

        public static UsageReportCoverageStore Open(AppConfig config, ILogger logger = null, TimeSpan? readTimeout = null)
        {
            var store = StateStore.TryOpen(config, StatePartitions.UsageReportCoverage, logger);
            return new UsageReportCoverageStore(store ?? ProcessFallback, store != null, readTimeout);
        }

        /// <summary>False for the process-local fallback, whose outcomes never certify a workload.</summary>
        public bool IsDurable => _durable;

        private static string Key(string source, DateTime day)
        {
            if (!WeeklySources.Contains(source)) throw new ArgumentException("Unknown usage-report source.", nameof(source));
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
        /// Row presence must be tenant-wide, not narrowed to the reader or to active users. This detects
        /// a contradiction, not a partial deletion that predates purge revocation and left rows on the day.
        /// </summary>
        public static string ReconcileWithStoredRows(string outcome, bool hasRows)
        {
            if ((outcome == Empty && hasRows) || (outcome == Complete && !hasRows)) return Partial;
            return outcome;
        }

        // Only purged dates have a suffix. "purge" means wait for the running purge; once it ends,
        // remember its UTC cutoff so later purges of OTHER days do not affect this date.
        private static string Outcome(string value)
        {
            var separator = value?.IndexOf('|') ?? -1;
            if (separator < 0) return value;
            var barrier = value.Substring(separator + 1);
            // An unknown/malformed future value must not be interpreted as "complete" just because
            // its first component looks familiar. A pending purge cannot carry a positive outcome.
            if (barrier == PendingPurge) return value.StartsWith(Partial + "|", StringComparison.Ordinal) ? Partial : ScopeUnverified;
            return long.TryParse(barrier, NumberStyles.None, CultureInfo.InvariantCulture, out var cutoff) && cutoff > 0
                ? value.Substring(0, separator) : Partial;
        }
        private static string PurgeBarrier(string value)
        {
            var separator = value?.IndexOf('|') ?? -1;
            return separator < 0 ? null : value.Substring(separator + 1);
        }
        private static string WithBarrier(string outcome, string barrier) => barrier == null ? outcome : outcome + "|" + barrier;

        /// <summary>
        /// A new import attempt supersedes the previous certificate. Until every page and SQL batch finishes,
        /// neither a previous empty day nor a previously complete set of users proves what this attempt saw.
        /// </summary>
        public Task BeginDayAsync(string source, DateTime day) =>
            WriteUnverifiedAsync(Key(source, day), Partial);

        private IExpiringConditionalKeyValueStore Conditional => _store as IExpiringConditionalKeyValueStore
            ?? throw new InvalidOperationException("Durable usage-report certification requires conditional writes.");

        private async Task WriteUnverifiedAsync(string key, string outcome)
        {
            if (!_durable)
            {
                await _store.SetStringAsync(key, outcome, Retention).ConfigureAwait(false);
                return;
            }
            // Preserve the purge barrier through retries and later refreshes, including an earlier
            // download finishing after a fresh one. These dates cannot become legacy key misses.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var prior = await Conditional.GetVersionedAsync(key).ConfigureAwait(false);
                var barrier = PurgeBarrier(prior.Value);
                if (await Conditional.TrySetExpiringStringAsync(key, WithBarrier(outcome, barrier),
                    prior.VersionToken, barrier == null ? Retention : (TimeSpan?)null).ConfigureAwait(false)) return;
            }
            throw new InvalidOperationException("Concurrent usage-report updates prevented marking the day unverified.");
        }

        /// <summary>
        /// Records the outcome of a saved day. <paramref name="settlementDays"/> is the analysis' settlement lag
        /// (CopilotAdoptionOptions.UsageReportLagDays, 3 by default): the analysis on day T first assesses
        /// T - lag. A day with rows is certified when saved at least lag - 1 days after it, so the import of
        /// day T - 1 has already certified the newest day the analysis on day T reads, at any time of day T.
        /// A successfully EMPTY day needs the full lag: Graph answers a day it has not published yet with
        /// 200 and no rows, so zero rows inside the lag cannot be told from "not available yet".
        /// <paramref name="importedUtc"/> must be when Graph was ASKED for the day, not when the save ended: a
        /// save that crosses midnight UTC would otherwise age the answer by a day it never had.
        /// </summary>
        public async Task CompleteDayAsync(string source, DateTime day, bool wholeTenant, bool allRowsSaved,
            bool empty, DateTime importedUtc, int settlementDays)
        {
            var lag = Math.Max(2, settlementDays);
            var settled = day.Date <= importedUtc.Date.AddDays(-(empty ? lag : lag - 1));
            var status = !wholeTenant ? ScopeUnverified
                : !settled ? Partial
                : !allRowsSaved ? Incomplete
                : empty ? Empty : Complete;
            var key = Key(source, day);
            if (!_durable || (status != Complete && status != Empty))
            {
                await WriteUnverifiedAsync(key, status).ConfigureAwait(false);
                return;
            }

            // A purge may remove an early SQL batch while a later batch is still being saved. Read the
            // day's ETag BEFORE the purge fence: a purge starting after that read either revokes this
            // certificate afterwards or wins the conditional write. Unaffected dates never need the fence.
            var prior = await Conditional.GetVersionedAsync(key).ConfigureAwait(false);
            var barrier = PurgeBarrier(prior.Value);
            if (barrier != null)
            {
                var fence = barrier == PendingPurge ? await _store.GetStringAsync(ScopePurgeKey).ConfigureAwait(false) : barrier;
                if (long.TryParse(fence, NumberStyles.None, CultureInfo.InvariantCulture, out var endedTicks))
                {
                    barrier = fence;
                    if (importedUtc.Ticks <= endedTicks) status = ScopeUnverified;
                }
                else status = ScopeUnverified;
            }
            if (!await Conditional.TrySetExpiringStringAsync(key, WithBarrier(status, barrier),
                prior.VersionToken, barrier == null ? Retention : (TimeSpan?)null).ConfigureAwait(false))
            {
                // Another writer interleaved with this save. Never leave a positive or legacy miss as
                // its proof; any newer purge barrier survives the conditional downgrade.
                await WriteUnverifiedAsync(key, Partial).ConfigureAwait(false);
            }
        }

        internal Task StartScopePurgeAsync(string runToken, CancellationToken token = default) =>
            _store.SetStringAsync(ScopePurgeKey, "active:" + runToken, cancellationToken: token);

        internal async Task EndScopePurgeAsync(string runToken, DateTime endedUtc)
        {
            var conditional = (IConditionalKeyValueStore)_store;
            var fence = await conditional.GetVersionedAsync(ScopePurgeKey).ConfigureAwait(false);
            if (fence.Value == "active:" + runToken)
                await conditional.TrySetStringAsync(ScopePurgeKey, endedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                    fence.VersionToken).ConfigureAwait(false);
        }

        internal Task RevokeScopeDaysAsync(string source, IEnumerable<DateTime> days, CancellationToken token) =>
            ((IBatchKeyValueStore)_store).SetManyAsync(days.Select(day =>
                new KeyValuePair<string, string>(Key(source, day), WithBarrier(ScopeUnverified, PendingPurge))).ToList(), token);

        /// <summary>
        /// The rows returned by an interrupted download cannot certify the day, even if older rows are retained.
        /// </summary>
        public Task RecordUnfinishedDownloadAsync(string source, DateTime day) => BeginDayAsync(source, day);

        /// <summary>
        /// Whether the ledger alone has a durable complete or successfully empty outcome. Report publication
        /// also checks tenant-wide SQL row presence; a historical certificate can contradict retained facts.
        /// </summary>
        public async Task<bool> IsDayVerifiedAsync(string source, DateTime day, CancellationToken cancellationToken = default)
        {
            if (!_durable) return false;
            var outcome = Outcome(await _store.GetStringAsync(Key(source, day), cancellationToken).ConfigureAwait(false));
            return outcome == Complete || outcome == Empty;
        }

        /// <summary>
        /// Whether a finalized day with rows in SQL should be downloaded again. An empty outcome contradicts
        /// those rows (older builds could leave it after a failed refresh). A scopeUnverified day is retried
        /// once the importer runs tenant-wide again.
        /// </summary>
        public Task<bool> NeedsReimportAsync(string source, DateTime day, bool wholeTenant,
            CancellationToken cancellationToken = default) =>
            NeedsReimportAsync(source, day, wholeTenant, hasStoredRows: true, cancellationToken: cancellationToken);

        /// <summary>Without any retained rows only an explicitly empty report can settle the day.</summary>
        public async Task<bool> NeedsReimportAsync(string source, DateTime day, bool wholeTenant, bool hasStoredRows,
            CancellationToken cancellationToken = default)
        {
            var outcome = Outcome(await _store.GetStringAsync(Key(source, day), cancellationToken).ConfigureAwait(false));
            if (!hasStoredRows) return !_durable || outcome != Empty;
            switch (outcome)
            {
                case Complete:
                case Incomplete:
                    return false;
                case ScopeUnverified:
                    return wholeTenant;
                default:
                    return true;
            }
        }

        /// <summary>Summarises ledger outcomes alone. Publication also checks the dates against tenant-wide SQL.</summary>
        public Task<List<UsageReportCoverage>> ReadAsync(DateTime from, DateTime through, CancellationToken cancellationToken = default) =>
            ReadAsync(from, through, cancellationToken, null);

        /// <summary>
        /// At most 4 x 366 point reads, eight in flight, once per analysis (not per user or nested scope).
        /// The analysis/result cache carries this snapshot into API pages and exports. A source outage stops
        /// further reads for that source and cannot fall back to process-local successes. Report publication
        /// supplies <paramref name="daysWithStoredRows"/> to check positive and empty certificates. A
        /// failed population-wide SQL probe cannot verify that source; other sources are independent.
        /// </summary>
        internal async Task<List<UsageReportCoverage>> ReadAsync(DateTime from, DateTime through,
            CancellationToken cancellationToken, Func<string, Task<HashSet<DateTime>>> daysWithStoredRows)
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

            var daily = await ReadDailyOutcomesAsync(from, through, cancellationToken).ConfigureAwait(false);
            foreach (var result in results)
            {
                var outcomes = daily[result.Source];
                if (outcomes.Count != count)
                {
                    result.Reason = "durableStateUnavailable";
                    continue;
                }
                HashSet<DateTime> stored = null;
                if (daysWithStoredRows != null && outcomes.Values.Any(value => value == Complete || value == Empty))
                {
                    try
                    {
                        stored = await daysWithStoredRows(result.Source).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    if (stored == null)
                    {
                        result.Reason = "storedRowsUnavailable";
                        continue;
                    }
                }
                var contradiction = false;
                foreach (var day in outcomes)
                {
                    var outcome = stored == null ? day.Value : ReconcileWithStoredRows(day.Value, stored.Contains(day.Key));
                    if (outcome != day.Value) contradiction = true;
                    if (outcome == Complete || outcome == Empty) result.VerifiedDays++;
                    if (outcome == Empty) result.EmptyDays++;
                    if (outcome == Partial) result.PartialDays++;
                    if (outcome == Incomplete) result.IncompleteDays++;
                    if (outcome == ScopeUnverified) result.ScopeUnverifiedDays++;
                }
                result.Status = result.VerifiedDays == count
                    ? result.EmptyDays == count ? "empty" : "complete"
                    : result.VerifiedDays + result.PartialDays + result.IncompleteDays > 0 ? "partial" : "unverified";
                result.Reason = result.IsComplete ? null
                    : result.ScopeUnverifiedDays > 0 ? ScopeUnverified
                    : contradiction ? "storedRowsMismatch"
                    : result.IncompleteDays > 0 ? Incomplete : "outcomesMissing";
            }
            return results;
        }

        /// <summary>
        /// Reads the exact daily outcomes for report consumers that need to distinguish a verified empty
        /// day from an older day with no ledger record. A confirmed key miss is a null value for its date;
        /// an unavailable source has no date entries at all.
        /// </summary>
        public Task<Dictionary<string, Dictionary<DateTime, string>>> ReadDailyOutcomesAsync(
            DateTime from, DateTime through, CancellationToken cancellationToken = default) =>
            ReadDailyOutcomesAsync(Sources, from, through, cancellationToken);

        public Task<Dictionary<string, Dictionary<DateTime, string>>> ReadWeeklyChartOutcomesAsync(
            DateTime from, DateTime through, CancellationToken cancellationToken = default) =>
            ReadDailyOutcomesAsync(WeeklySources, from, through, cancellationToken);

        private async Task<Dictionary<string, Dictionary<DateTime, string>>> ReadDailyOutcomesAsync(
            IReadOnlyList<string> sources, DateTime from, DateTime through, CancellationToken cancellationToken)
        {
            var results = sources.ToDictionary(
                source => source,
                _ => new Dictionary<DateTime, string>(),
                StringComparer.Ordinal);
            var count = (through.Date - from.Date).Days + 1;
            if (!_durable || count <= 0 || count > MaximumDays) return results;

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var gate = new SemaphoreSlim(ReadConcurrency))
            {
                deadline.CancelAfter(_readTimeout);
                await Task.WhenAll(sources.Select(async source =>
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
                                    var outcome = await _store.GetStringAsync(
                                        Key(source, from.Date.AddDays(day)), deadline.Token).ConfigureAwait(false);
                                    return Outcome(outcome);
                                }
                                finally { gate.Release(); }
                            })).ConfigureAwait(false);
                            foreach (var day in days)
                                results[source][from.Date.AddDays(day)] = outcomes[day - offset];
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        results[source].Clear();
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
