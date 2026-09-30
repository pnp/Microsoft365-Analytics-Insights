using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Records when each individual usage report last completed successfully.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the phase-level <see cref="ISingleDateStore"/>, which answers a different question. That
    /// one timestamp has to mean two things at once: "don't re-run this once-a-day phase yet" AND "these
    /// stored days are proven complete, so they can be skipped". Withholding it after a failure is correct for
    /// the first meaning (issue #285 - a broken import must not look complete) but wrong for the second: it
    /// also emptied the finalized-date skip list for the reports that had succeeded, so one permanently
    /// failing report made all eleven re-download their full window every cycle (issue #311).
    /// </para>
    /// <para>
    /// A per-report stamp lets those two meanings separate. The phase timestamp keeps its throttling job
    /// unchanged; the skip list now asks each report when <b>it</b> last completed.
    /// </para>
    /// </remarks>
    public interface IReportCompletionStore
    {
        /// <summary>When this report last completed successfully, or null if it never has.</summary>
        Task<DateTime?> GetLastSuccessAsync(string reportKey);

        /// <summary>Record that this report has just completed successfully.</summary>
        Task SaveSuccessAsync(string reportKey);

        /// <summary>
        /// Forget this report's completion. Called before a report runs, so that a crash part-way through its
        /// save cannot leave a stamp claiming the window is complete.
        /// </summary>
        Task ClearAsync(string reportKey);
    }

    public interface IReportAttemptScheduleStore
    {
        Task<DateTime?> GetNextAttemptUtcAsync(string reportKey);
        Task SaveNextAttemptUtcAsync(string reportKey, DateTime nextAttemptUtc);
        Task ClearNextAttemptUtcAsync(string reportKey);
    }

    /// <summary>
    /// In-memory fallback used when no Storage connection string is configured; lives only for the life of the WebJob
    /// process. Must be constructed ONCE outside the per-cycle loop, exactly like <see cref="InMemorySingleDateStore"/>.
    /// </summary>
    public class InMemoryReportCompletionStore : IReportCompletionStore, IReportAttemptScheduleStore
    {
        // Reports run concurrently under Task.WhenAll, so this must be thread-safe.
        private readonly ConcurrentDictionary<string, DateTime> _lastSuccess =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _nextAttemptUtc =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public Task<DateTime?> GetLastSuccessAsync(string reportKey)
        {
            return Task.FromResult(_lastSuccess.TryGetValue(reportKey, out var dt) ? dt : (DateTime?)null);
        }

        public Task SaveSuccessAsync(string reportKey)
        {
            _lastSuccess[reportKey] = DateTime.Now;
            return Task.CompletedTask;
        }

        public Task ClearAsync(string reportKey)
        {
            _lastSuccess.TryRemove(reportKey, out _);
            return Task.CompletedTask;
        }

        public Task<DateTime?> GetNextAttemptUtcAsync(string reportKey)
        {
            return Task.FromResult(_nextAttemptUtc.TryGetValue(reportKey, out var dt) ? dt : (DateTime?)null);
        }

        public Task SaveNextAttemptUtcAsync(string reportKey, DateTime nextAttemptUtc)
        {
            _nextAttemptUtc[reportKey] = nextAttemptUtc.Kind == DateTimeKind.Utc
                ? nextAttemptUtc
                : nextAttemptUtc.ToUniversalTime();
            return Task.CompletedTask;
        }

        public Task ClearNextAttemptUtcAsync(string reportKey)
        {
            _nextAttemptUtc.TryRemove(reportKey, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Durable <see cref="IReportCompletionStore"/> in the runtime state store (<see cref="StatePartitions.ImportSchedule"/>),
    /// so per-report completion survives a WebJob restart (which is the whole point - an in-memory stamp would be lost on
    /// every restart and the skip list would be empty again).
    /// </summary>
    public class PersistedReportCompletionStore : IReportCompletionStore, IReportAttemptScheduleStore
    {
        /// <summary>
        /// Prefix for the per-report keys. Deliberately distinct from the phase-level
        /// <c>UserActivityLastImported</c> key so the two cannot collide.
        /// </summary>
        internal const string KeyPrefix = "UserActivityReportLastImported:";
        internal const string NextAttemptKeyPrefix = "UserActivityReportNextAttemptUtc:";

        private readonly IKeyValueStore _store;

        public PersistedReportCompletionStore(IKeyValueStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        private KeyValueSingleDateStore LoaderFor(string reportKey) => new KeyValueSingleDateStore(_store, KeyPrefix + reportKey);

        private KeyValueSingleDateStore NextAttemptLoaderFor(string reportKey) => new KeyValueSingleDateStore(_store, NextAttemptKeyPrefix + reportKey);

        public Task<DateTime?> GetLastSuccessAsync(string reportKey) => LoaderFor(reportKey).GetLastDT();

        public Task SaveSuccessAsync(string reportKey) => LoaderFor(reportKey).SaveDT();

        public Task ClearAsync(string reportKey) => LoaderFor(reportKey).DeleteDt();

        public Task<DateTime?> GetNextAttemptUtcAsync(string reportKey) => NextAttemptLoaderFor(reportKey).GetLastDT();

        public Task SaveNextAttemptUtcAsync(string reportKey, DateTime nextAttemptUtc)
            => NextAttemptLoaderFor(reportKey).SaveDT(nextAttemptUtc.Kind == DateTimeKind.Utc ? nextAttemptUtc : nextAttemptUtc.ToUniversalTime());

        public Task ClearNextAttemptUtcAsync(string reportKey) => NextAttemptLoaderFor(reportKey).DeleteDt();
    }

    /// <summary>
    /// Builds the <see cref="IReportCompletionStore"/> for per-report completion stamps: the runtime state table when a
    /// Storage connection string is configured (durable across restarts), otherwise in-memory for the life of this
    /// process. Mirrors <see cref="ActivityReportsLastImportedStoreFactory"/>.
    /// </summary>
    public static class ReportCompletionStoreFactory
    {
        public static IReportCompletionStore Create(AppConfig config, ILogger logger)
        {
            var store = StateStore.TryOpen(config, StatePartitions.ImportSchedule, logger);
            if (store != null)
            {
                return new PersistedReportCompletionStore(store);
            }

            logger?.LogInformation(
                "No Storage connection string configured - per-report usage-report completion is tracked in memory only, "
                + "so the finalized-date skip list resets each time the WebJob process restarts.");
            return new InMemoryReportCompletionStore();
        }
    }
}
