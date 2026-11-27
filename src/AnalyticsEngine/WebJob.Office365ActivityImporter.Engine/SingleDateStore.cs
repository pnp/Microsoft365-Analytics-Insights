using Common.Entities.Config;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Stores a single "last run" timestamp so a periodic import can be throttled to run at most once per
    /// window (e.g. once a day). Backed by the runtime state store (Azure Table storage) when a Storage connection string
    /// is configured, otherwise an in-memory fallback that lives only for the life of the (long-running) WebJob process.
    /// </summary>
    public interface ISingleDateStore
    {
        Task<DateTime?> GetLastDT();
        Task SaveDT();
        Task DeleteDt();
    }

    // A cadence observation, never a day-coverage certificate. Legacy timestamps have no observation.
    internal enum DailyPhaseFacts { RowsPresent, ExpectedEmpty, Unverified }

    internal interface IActivityReportsPhaseStore : ISingleDateStore
    {
        Task<DailyPhaseFacts?> ReadFactsAsync(DateTime completion);
        Task SavePhaseAsync(DateTime completedUtc, DailyPhaseFacts facts);
    }

    /// <summary>
    /// In-memory fallback for <see cref="ISingleDateStore"/>, used when no Storage connection string is configured. The
    /// stored date lives only for the lifetime of this instance.
    ///
    /// IMPORTANT: callers must hold a single instance for the lifetime of the WebJob process (construct it
    /// ONCE, outside the per-cycle import loop). A fresh instance per cycle would always return null from
    /// <see cref="GetLastDT"/> and defeat the throttle, re-running the multi-hour usage-report phase every
    /// cycle. Mirrors <see cref="StatsUploader.InMemoryStatsDatesLoader"/>.
    /// </summary>
    public class InMemorySingleDateStore : ISingleDateStore, IActivityReportsPhaseStore
    {
        private readonly object _lock = new object();
        private DateTime? _lastDt;
        private DailyPhaseFacts? _facts;

        public Task<DateTime?> GetLastDT()
        {
            lock (_lock)
            {
                return Task.FromResult(_lastDt);
            }
        }

        public Task SaveDT() => Set(DateTime.Now, null);

        public Task DeleteDt() => Set(null, null);

        Task<DailyPhaseFacts?> IActivityReportsPhaseStore.ReadFactsAsync(DateTime completion)
        {
            lock (_lock)
            {
                return Task.FromResult(_lastDt?.ToUniversalTime() == completion.ToUniversalTime() ? _facts : null);
            }
        }

        Task IActivityReportsPhaseStore.SavePhaseAsync(DateTime completedUtc, DailyPhaseFacts facts) => Set(completedUtc, facts);

        private Task Set(DateTime? completion, DailyPhaseFacts? facts)
        {
            lock (_lock)
            {
                _lastDt = completion;
                _facts = facts;
            }
            return Task.CompletedTask;
        }
    }

    internal sealed class PersistedActivityReportsPhaseStore : KeyValueSingleDateStore, IActivityReportsPhaseStore
    {
        private const string FactsKey = "UserActivityPhaseFacts:v1";
        private readonly IBatchKeyValueStore _store;

        internal PersistedActivityReportsPhaseStore(IBatchKeyValueStore store)
            : base(store, ActivityReportsLastImportedStoreFactory.UserActivityLastImportedKey) => _store = store;

        public override async Task DeleteDt()
        {
            // Invalidate the public success first, even if clearing its companion fails. A retry or a
            // legacy writer landing on exactly the old clock tick must not resurrect a failed run's evidence.
            await base.DeleteDt();
            await _store.DeleteAsync(FactsKey);
        }

        public Task SavePhaseAsync(DateTime completedUtc, DailyPhaseFacts facts)
        {
            var stamp = completedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            // Keep the operator-facing timestamp readable by old builds. Publish the two small keys in
            // ONE Table transaction: even overlapping completions at the same clock tick cannot mix runs.
            return _store.SetManyAsync(new[]
            {
                new KeyValuePair<string, string>(Key, stamp),
                new KeyValuePair<string, string>(FactsKey, facts + "|" + stamp),
            });
        }

        public async Task<DailyPhaseFacts?> ReadFactsAsync(DateTime completion)
        {
            // Old writers used DateTime.Now with an offset. They did not publish a receipt, even if
            // their wall clock happened to represent exactly the same instant as an earlier UTC writer.
            if (completion.Kind != DateTimeKind.Utc) return null;
            var receipt = await _store.GetStringAsync(FactsKey);
            var stamp = completion.ToString("o", CultureInfo.InvariantCulture);
            foreach (DailyPhaseFacts facts in Enum.GetValues(typeof(DailyPhaseFacts)))
                if (receipt == facts + "|" + stamp) return facts;
            // Deleting or rewriting just the original timestamp must not resurrect old empty evidence.
            return null;
        }
    }

    /// <summary>
    /// Builds the <see cref="ISingleDateStore"/> used to throttle the activity/usage-report phase to at most
    /// once a day: a durable store in the runtime state table (survives process restarts) when a Storage connection
    /// string is configured, otherwise an in-memory store that persists only for the life of this process. Mirrors the
    /// stats-upload throttle so the usage-report phase behaves the same as other once-a-day imports either way.
    /// </summary>
    public static class ActivityReportsLastImportedStoreFactory
    {
        /// <summary>
        /// The phase-level marker's key in <see cref="StatePartitions.ImportSchedule"/>. Deleting the row makes the
        /// usage-report phase run on the next cycle.
        /// </summary>
        public const string UserActivityLastImportedKey = Common.Entities.State.ImportScheduleKeys.UsageReportsPhase;

        public static ISingleDateStore Create(AppConfig config, ILogger logger)
        {
            var store = StateStore.TryOpen(config, StatePartitions.ImportSchedule, logger);
            if (store != null)
            {
                return new PersistedActivityReportsPhaseStore((IBatchKeyValueStore)store);
            }

            logger?.LogInformation("No Storage connection string configured - using in-memory throttle for activity/usage reports (the once-a-day window resets each time the WebJob process restarts).");
            return new InMemorySingleDateStore();
        }
    }
}
