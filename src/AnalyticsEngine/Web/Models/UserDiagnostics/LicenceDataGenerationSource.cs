using Common.Entities.Config;
using Common.Entities.UserDiagnostics;
using System;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.UserScope;

namespace Web.AnalyticsWeb.Models.UserDiagnostics
{
    /// <summary>
    /// The licence-data generation report caches are keyed on (<see cref="UserRefreshStore.GetLicenceDataGenerationAsync"/>):
    /// the importer advances it whenever licence assignments change, so a corrected licence reaches the report that
    /// prompted the question without waiting out the cache - on every web instance, because it lives in Azure Table
    /// storage rather than in one process.
    /// </summary>
    /// <remarks>
    /// Read at most every <see cref="RefreshInterval"/>; a report request otherwise pays nothing. A failed read keeps
    /// the last value rather than failing the report: caches then fall back to their own lifetimes, minutes at most.
    /// An analysis started under an older generation still publishes, but under its own key, which nobody asks for
    /// once the generation has moved on.
    /// </remarks>
    public sealed class LicenceDataGenerationSource
    {
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

        /// <summary>The longest a report request waits for the stored value before using the last one.</summary>
        public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

        /// <summary>This deployment's generation, or none at all without a Storage account.</summary>
        public static readonly LicenceDataGenerationSource Production = new LicenceDataGenerationSource(() =>
        {
            var store = UserRefreshStore.TryOpen(new AppConfig(), UserScopeService.ProductionLogger.Value);
            return store == null ? Task.FromResult<string>(null) : store.GetLicenceDataGenerationAsync();
        });

        /// <summary>No generation: cache keys stay exactly as they were. For tests and deployments without Storage.</summary>
        public static readonly LicenceDataGenerationSource None = new LicenceDataGenerationSource(() => Task.FromResult<string>(null));

        private readonly Func<Task<string>> _read;
        private readonly Func<DateTime> _utcNow;
        private readonly object _sync = new object();
        private Task<string> _pending;
        private DateTime _readUtc = DateTime.MinValue;
        private volatile string _lastKnown;

        public LicenceDataGenerationSource(Func<Task<string>> read, Func<DateTime> utcNow = null)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        /// <summary>The value last read; null before the first read or when none has been recorded.</summary>
        public string LastKnown => _lastKnown;

        /// <summary>The current generation. Completes synchronously while the last read is fresh. Never throws.</summary>
        public Task<string> GetAsync()
        {
            lock (_sync)
            {
                if (_pending != null && !_pending.IsCompleted)
                {
                    return _pending;
                }
                if (_utcNow() - _readUtc < RefreshInterval)
                {
                    return Task.FromResult(_lastKnown);
                }
                _pending = ReadAsync();
                return _pending;
            }
        }

        private async Task<string> ReadAsync()
        {
            try
            {
                var read = Task.Run(_read);
                if (await Task.WhenAny(read, Task.Delay(ReadTimeout)).ConfigureAwait(false) == read)
                {
                    _lastKnown = await read.ConfigureAwait(false);
                }
                else
                {
                    // Observe a late failure so it is not an unobserved task exception.
                    _ = read.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Keep the last value: a report must not fail because this optional hint cannot be read.
            }
            finally
            {
                lock (_sync)
                {
                    _readUtc = _utcNow();
                }
            }
            return _lastKnown;
        }

        /// <summary>What a report appends to its cache key; empty when there is no generation, so keys are unchanged.</summary>
        public static string KeySuffix(string generation)
            => string.IsNullOrEmpty(generation) ? string.Empty : "::lg" + generation;
    }
}
