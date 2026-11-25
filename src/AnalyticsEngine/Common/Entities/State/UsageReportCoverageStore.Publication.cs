using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.State
{
    public sealed partial class UsageReportCoverageStore
    {
        /// <summary>
        /// Capture daily ledger versions BEFORE starting the report's SQL queries. Read the same ranges
        /// after materialisation: even complete → partial → complete is a different certificate. A
        /// successful range with no entity is a confirmed legacy miss; a failed range proves nothing.
        /// At most two indexed ranges per source: eight for Adoption or ten for the weekly chart,
        /// each spanning at most 366 dates, regardless of the number of people. The configured
        /// storage time budget covers both passes; it does not include the SQL queries between them.
        /// </summary>
        public static async Task<PublicationRead> BeginPublicationAsync(IReadOnlyList<string> sources,
            DateTime from, DateTime through, Func<UsageReportCoverageStore> openStore, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            UsageReportCoverageStore store = null;
            try { store = openStore(); }
            catch (Exception) { token.ThrowIfCancellationRequested(); }
            return await PublicationRead.CaptureAsync(store, sources, from, through, true, token).ConfigureAwait(false);
        }

        public sealed class PublicationRead
        {
            private readonly IVersionedKeyRangeReader _reader;
            private readonly string[] _sources;
            private readonly DateTime _from, _through;
            private readonly int _days;
            private readonly TimeSpan _timeout;
            private readonly Stopwatch _storageTime = new Stopwatch();
            private readonly string _unavailableReason;
            private Dictionary<string, VersionedValue[]> _before;

            private PublicationRead(UsageReportCoverageStore store, IReadOnlyList<string> sources, DateTime from, DateTime through)
            {
                _from = from;
                _through = through;
                _days = (through - from).Days + 1;
                _sources = sources.Distinct(StringComparer.Ordinal).ToArray();
                if (_sources.Except(WeeklySources).Any()) throw new ArgumentException("Unknown usage-report source.", nameof(sources));
                _reader = store?._store as IVersionedKeyRangeReader;
                _timeout = store?._readTimeout ?? TimeSpan.Zero;
                _unavailableReason = store != null && !store.IsDurable ? "durableStateMissing"
                    : _days < 1 || _days > MaximumDays ? "windowUnsupported"
                    : _reader == null ? "durableStateUnavailable" : null;
            }

            internal static async Task<PublicationRead> CaptureAsync(UsageReportCoverageStore store, IReadOnlyList<string> sources,
                DateTime from, DateTime through, bool bracketFacts, CancellationToken token)
            {
                var read = new PublicationRead(store, sources, from.Date, through.Date);
                // If a source stalls, reserve half the total storage budget for healthy sources' closing reads.
                var budget = bracketFacts ? TimeSpan.FromTicks(read._timeout.Ticks / 2) : read._timeout;
                read._before = await read.ReadSourcesAsync(read._sources, budget, token).ConfigureAwait(false);
                return read;
            }

            private async Task<Dictionary<string, VersionedValue[]>> ReadSourcesAsync(IEnumerable<string> sources, TimeSpan budget, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var result = new Dictionary<string, VersionedValue[]>(StringComparer.Ordinal);
                if (_unavailableReason != null || budget <= TimeSpan.Zero) return result;
                _storageTime.Start();
                try
                {
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        deadline.CancelAfter(budget);
                        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        using (deadline.Token.Register(() => stopped.TrySetResult(true)))
                        {
                            var reads = sources.ToDictionary(source => source, source => ReadSourceAsync(source, deadline.Token));
                            // Do not wait for a non-cooperative transport or publish its late result.
                            await Task.WhenAny(Task.WhenAll(reads.Values), stopped.Task).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            foreach (var read in reads)
                                if (read.Value.Status == TaskStatus.RanToCompletion && read.Value.Result != null)
                                    result.Add(read.Key, read.Value.Result);
                        }
                    }
                }
                finally { _storageTime.Stop(); }
                return result;
            }

            private async Task<VersionedValue[]> ReadSourceAsync(string source, CancellationToken token)
            {
                try
                {
                    var rows = await _reader.ReadVersionedRangeAsync(Key(source, _from), Key(source, _through), _days, token)
                        .ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var days = new VersionedValue[_days];
                    for (var i = 0; i < days.Length; i++)
                        if (rows.TryGetValue(Key(source, _from.AddDays(i)), out days[i]) &&
                            (string.IsNullOrEmpty(days[i]?.VersionToken) || days[i].VersionToken == "*"))
                            return null;
                    return days;
                }
                catch (Exception) { return null; } // The enclosing pass propagates caller cancellation.
            }

            internal Dictionary<string, Dictionary<DateTime, string>> InitialOutcomes() => Project(_sources, _before);

            public async Task<Dictionary<string, Dictionary<DateTime, string>>> ReadStableOutcomesAsync(
                IReadOnlyList<string> sources, CancellationToken token = default)
            {
                var after = await ReadSourcesAsync(sources.Where(_before.ContainsKey), _timeout - _storageTime.Elapsed, token).ConfigureAwait(false);
                return Project(sources, after);
            }

            private Dictionary<string, Dictionary<DateTime, string>> Project(IReadOnlyList<string> sources,
                Dictionary<string, VersionedValue[]> after)
            {
                var result = sources.ToDictionary(source => source, _ => new Dictionary<DateTime, string>());
                foreach (var source in sources)
                {
                    if (!after.TryGetValue(source, out var days)) continue;
                    var prior = _before[source];
                    for (var i = 0; i < days.Length; i++)
                    {
                        var outcome = Outcome(days[i]?.Value);
                        // Expiry can change the logical value without changing the physical ETag. Preserve
                        // explicit negative outcomes (notably a purge); never promote a changed miss to legacy proof.
                        if ((prior[i]?.VersionToken != days[i]?.VersionToken || prior[i]?.Value != days[i]?.Value) &&
                            (outcome == Complete || outcome == Empty || outcome == null))
                            outcome = Partial;
                        result[source].Add(_from.AddDays(i), outcome);
                    }
                }
                return result;
            }

            internal async Task<List<UsageReportCoverage>> SummariseAsync(Func<string, Task<HashSet<DateTime>>> daysWithStoredRows,
                CancellationToken token)
            {
                var stored = new Dictionary<string, HashSet<DateTime>>();
                if (daysWithStoredRows != null)
                    foreach (var source in _before)
                    {
                        if (!source.Value.Any(day => Outcome(day?.Value) == Complete || Outcome(day?.Value) == Empty)) continue;
                        try { stored[source.Key] = await daysWithStoredRows(source.Key).ConfigureAwait(false); }
                        catch (Exception) { token.ThrowIfCancellationRequested(); stored[source.Key] = null; }
                    }
                // The closing read follows BOTH the workload facts and tenant-wide SQL presence probes.
                var daily = daysWithStoredRows == null ? InitialOutcomes() : await ReadStableOutcomesAsync(_sources, token).ConfigureAwait(false);
                return _sources.Select(source =>
                {
                    var result = new UsageReportCoverage
                    {
                        Source = source, FromUtc = _from, ThroughUtc = _through, ExpectedDays = Math.Max(0, _days),
                        Reason = _unavailableReason ?? "durableStateUnavailable",
                    };
                    if (_unavailableReason != null || daily[source].Count != _days) return result;
                    if (stored.TryGetValue(source, out var present) && present == null)
                    {
                        result.Reason = "storedRowsUnavailable";
                        return result;
                    }
                    var contradiction = false;
                    foreach (var day in daily[source])
                    {
                        var outcome = present == null ? day.Value : ReconcileWithStoredRows(day.Value, present.Contains(day.Key));
                        if (outcome != day.Value) contradiction = true;
                        if (outcome == Complete || outcome == Empty) result.VerifiedDays++;
                        if (outcome == Empty) result.EmptyDays++;
                        if (outcome == Partial) result.PartialDays++;
                        if (outcome == Incomplete) result.IncompleteDays++;
                        if (outcome == ScopeUnverified) result.ScopeUnverifiedDays++;
                    }
                    result.Status = result.VerifiedDays == _days
                        ? result.EmptyDays == _days ? "empty" : "complete"
                        : result.VerifiedDays + result.PartialDays + result.IncompleteDays > 0 ? "partial" : "unverified";
                    result.Reason = result.IsComplete ? null : result.ScopeUnverifiedDays > 0 ? ScopeUnverified
                        : contradiction ? "storedRowsMismatch" : result.IncompleteDays > 0 ? Incomplete : "outcomesMissing";
                    return result;
                }).ToList();
            }
        }
    }
}
