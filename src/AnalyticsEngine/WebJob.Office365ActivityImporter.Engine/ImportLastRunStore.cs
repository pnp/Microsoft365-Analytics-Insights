using Common.Entities.State;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// Stores the "last successfully ran" timestamp for an import section, so the importer can
    /// gate the non-fresh Graph imports (user metadata, user Teams apps, Teams crawl) to run at
    /// most once per configured interval instead of every cycle.
    ///
    /// IMPORTANT: a single instance must be held for the lifetime of the WebJob process (created
    /// once, outside the per-cycle loop) - otherwise the in-memory implementation loses its state
    /// every cycle and the gate never fires. This mirrors <c>IStatsDatesLoader</c>.
    /// </summary>
    public interface IImportLastRunStore
    {
        Task<DateTime?> GetLastRunUtc(string key);
        Task SetLastRunUtc(string key, DateTime whenUtc);
        Task Clear(string key);
    }

    /// <summary>
    /// Optional companion to <see cref="IImportLastRunStore"/> for a section that backs off after failing: how many
    /// attempts in a row have failed and when the last one did. A last-run store that does not implement it simply gives
    /// every section the plain cadence gate.
    /// </summary>
    public interface IImportFailureStore
    {
        /// <summary>The failed attempts since the section last succeeded, or null when there are none.</summary>
        Task<ImportFailureState> GetFailureState(string key);

        Task SetFailureState(string key, ImportFailureState state);

        Task ClearFailureState(string key);
    }

    /// <summary>Consecutive failed attempts of a section, and when the latest ended (UTC).</summary>
    public sealed class ImportFailureState
    {
        public int ConsecutiveFailures { get; set; }

        public DateTime LastFailureUtc { get; set; }
    }

    /// <summary>
    /// How long a section that has just failed waits before it is tried again: one hour after the first failed attempt,
    /// two after the second, four after the third and so on, never longer than the section's normal interval. A failed
    /// attempt is never recorded as a completed run, so the normal cadence gate is untouched.
    /// </summary>
    public static class ImportFailureBackoff
    {
        public static readonly TimeSpan FirstDelay = TimeSpan.FromHours(1);

        /// <param name="consecutiveFailures">Attempts in a row that have failed, at least 1.</param>
        /// <param name="intervalHours">The section's normal interval; <c>0</c> (gate disabled) means no back-off.</param>
        public static TimeSpan DelayAfter(int consecutiveFailures, int intervalHours)
        {
            if (consecutiveFailures < 1 || intervalHours <= 0)
            {
                return TimeSpan.Zero;
            }

            var cap = TimeSpan.FromHours(intervalHours);
            var doublings = Math.Min(consecutiveFailures - 1, 20);
            var delay = TimeSpan.FromTicks(FirstDelay.Ticks * (1L << doublings));
            return delay > cap ? cap : delay;
        }

        /// <summary>Whether the back-off after the recorded failures has passed.</summary>
        public static bool IsOver(ImportFailureState state, int intervalHours, DateTime nowUtc)
            => state == null || nowUtc >= state.LastFailureUtc + DelayAfter(state.ConsecutiveFailures, intervalHours);
    }

    /// <summary>
    /// Pure decision logic for the cadence gate, factored out so it can be unit tested without any store.
    /// </summary>
    public static class ImportCadenceGate
    {
        /// <summary>
        /// Whether an import section is due to run.
        /// </summary>
        /// <param name="lastRunUtc">When the section last ran (UTC), or null if never.</param>
        /// <param name="intervalHours">Minimum hours between runs. <c>0</c> (or less) disables gating (always run).</param>
        /// <param name="force">When true, bypass the gate entirely (one-off force).</param>
        /// <param name="nowUtc">Current time (UTC).</param>
        public static bool ShouldRun(DateTime? lastRunUtc, int intervalHours, bool force, DateTime nowUtc)
        {
            if (force) return true;
            if (intervalHours <= 0) return true;        // gating disabled -> run every cycle
            if (lastRunUtc == null) return true;        // never ran -> run
            return nowUtc - lastRunUtc.Value >= TimeSpan.FromHours(intervalHours);
        }
    }

    /// <summary>
    /// Durable <see cref="IImportLastRunStore"/> over the runtime state store (the
    /// <see cref="StatePartitions.ImportSchedule"/> partition of the <see cref="StateStore.TableName"/> Azure Table), so
    /// the gate survives WebJob restarts. Reads/writes are <b>fail-open</b>: if the store is unreachable, a read returns
    /// <c>null</c> (so the import still runs) and a write is swallowed with a warning. This deliberately matches the
    /// legacy behaviour where these imports had no store dependency at all - a storage blip must never skip an import.
    /// </summary>
    /// <remarks>
    /// Each key is one row, holding the round-trip ("o") UTC timestamp in its <c>Value</c> column. Deleting a row makes
    /// that import run on the next cycle.
    /// </remarks>
    public class PersistedImportLastRunStore : IImportLastRunStore, IImportFailureStore
    {
        private readonly IKeyValueStore _store;
        private readonly ILogger _logger;

        public PersistedImportLastRunStore(IKeyValueStore store, ILogger logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
        }

        /// <summary>The row holding a section's failed attempts: a new kind of row beside the section's own last-run stamp.</summary>
        internal static string FailureKey(string key) => key + ":FailedAttempts";

        public async Task<ImportFailureState> GetFailureState(string key)
        {
            try
            {
                var raw = await _store.GetStringAsync(FailureKey(key));
                return string.IsNullOrEmpty(raw) ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<ImportFailureState>(raw);
            }
            catch (Exception ex)
            {
                // Fail-open, like the cadence stamp: a storage blip must not skip an attempt, so no back-off is applied.
                _logger?.LogWarning($"Import cadence: state store read failed for the failed attempts of '{key}' ({ex.GetType().Name}); not backing off.");
                return null;
            }
        }

        public async Task SetFailureState(string key, ImportFailureState state)
        {
            try
            {
                await _store.SetStringAsync(FailureKey(key), Newtonsoft.Json.JsonConvert.SerializeObject(state));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Import cadence: state store write failed for the failed attempts of '{key}' ({ex.GetType().Name}); the next cycle may try again at once.");
            }
        }

        public async Task ClearFailureState(string key)
        {
            try
            {
                await _store.DeleteAsync(FailureKey(key));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Import cadence: state store delete failed for the failed attempts of '{key}' ({ex.GetType().Name}).");
            }
        }

        public async Task<DateTime?> GetLastRunUtc(string key)
        {
            try
            {
                var raw = await _store.GetStringAsync(key);
                if (!string.IsNullOrEmpty(raw)
                    && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
                {
                    return dt.ToUniversalTime();
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Import cadence: state store read failed for '{key}' ({ex.Message}); treating as not-yet-run so the import proceeds.");
                return null;
            }
        }

        public async Task SetLastRunUtc(string key, DateTime whenUtc)
        {
            try
            {
                await _store.SetStringAsync(key, whenUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Import cadence: state store write failed for '{key}' ({ex.Message}); the next cycle may re-run this import.");
            }
        }

        public async Task Clear(string key)
        {
            try
            {
                await _store.DeleteAsync(key);
                await _store.DeleteAsync(FailureKey(key));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Import cadence: state store delete failed for '{key}' ({ex.Message}).");
            }
        }
    }

    /// <summary>
    /// In-memory <see cref="IImportLastRunStore"/> used when no Storage connection string is configured. The gate still
    /// works within a single WebJob process lifetime; the timestamps reset when the WebJob restarts
    /// (so a restart is itself a way to force a re-import).
    /// </summary>
    public class InMemoryImportLastRunStore : IImportLastRunStore, IImportFailureStore
    {
        private readonly ConcurrentDictionary<string, DateTime> _lastRuns =
            new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<string, ImportFailureState> _failures =
            new ConcurrentDictionary<string, ImportFailureState>(StringComparer.Ordinal);

        public Task<ImportFailureState> GetFailureState(string key)
            => Task.FromResult(_failures.TryGetValue(key, out var state) ? state : null);

        public Task SetFailureState(string key, ImportFailureState state)
        {
            _failures[key] = state;
            return Task.CompletedTask;
        }

        public Task ClearFailureState(string key)
        {
            _failures.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<DateTime?> GetLastRunUtc(string key)
            => Task.FromResult(_lastRuns.TryGetValue(key, out var dt) ? (DateTime?)dt : null);

        public Task SetLastRunUtc(string key, DateTime whenUtc)
        {
            _lastRuns[key] = whenUtc.ToUniversalTime();
            return Task.CompletedTask;
        }

        public Task Clear(string key)
        {
            _lastRuns.TryRemove(key, out _);
            _failures.TryRemove(key, out _);
            return Task.CompletedTask;
        }
    }
}
