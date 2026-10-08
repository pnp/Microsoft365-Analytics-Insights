using Common.Entities.Config;
using Common.Entities.CopilotAdoption;
using Common.Entities.State;
using DataUtils;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.CopilotAdoption
{
    /// <summary>Where the adoption analysis gets the score settings in force.</summary>
    internal interface ICopilotAdoptionScoreSettingsSource
    {
        /// <summary>
        /// The settings in force. Throws <see cref="CopilotAdoptionScoreSettingsUnavailableException"/> when they
        /// cannot be read: never quietly answers with the defaults.
        /// </summary>
        Task<CopilotAdoptionEffectiveScoreSettings> GetAsync();

        /// <summary>The last settings read, without touching storage; the defaults before the first read.</summary>
        CopilotAdoptionEffectiveScoreSettings LastKnown { get; }
    }

    /// <summary>Always the built-in defaults. What a coordinator built without a settings source uses.</summary>
    internal sealed class DefaultCopilotAdoptionScoreSettingsSource : ICopilotAdoptionScoreSettingsSource
    {
        public static readonly DefaultCopilotAdoptionScoreSettingsSource Instance = new DefaultCopilotAdoptionScoreSettingsSource();
        private static readonly Task<CopilotAdoptionEffectiveScoreSettings> Completed =
            Task.FromResult(CopilotAdoptionEffectiveScoreSettings.Defaults);

        public Task<CopilotAdoptionEffectiveScoreSettings> GetAsync() => Completed;

        public CopilotAdoptionEffectiveScoreSettings LastKnown => CopilotAdoptionEffectiveScoreSettings.Defaults;
    }

    /// <summary>
    /// Reads the score settings at most once per <see cref="DefaultRefreshInterval"/> per web instance, whatever
    /// the number of requests, and shares one in-flight read between concurrent callers.
    /// </summary>
    /// <remarks>
    /// <para>One point read of one table row, not per user and not per analysis step. The interval is what bounds
    /// how long another instance of a scaled-out web app keeps scoring with the previous settings after a save:
    /// the version is part of the analysis cache key, so the first request after the refresh starts a fresh
    /// analysis everywhere. The instance that saved invalidates its own copy at once.</para>
    /// <para>A failed read is not cached and is not papered over with the last good value or the defaults: the
    /// caller gets <see cref="CopilotAdoptionScoreSettingsUnavailableException"/>, the report answers 503, and the
    /// next request tries again.</para>
    /// </remarks>
    internal sealed class CopilotAdoptionScoreSettingsProvider : ICopilotAdoptionScoreSettingsSource
    {
        public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(15);

        private static readonly Lazy<AnalyticsLogger> ProductionLogger = new Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "CopilotAdoptionSettings"));

        private static readonly Lazy<CopilotAdoptionScoreSettingsStore> ProductionStore = new Lazy<CopilotAdoptionScoreSettingsStore>(() =>
        {
            var durable = StateStore.TryOpen(new AppConfig(), StatePartitions.CopilotAdoptionSettings, ProductionLogger.Value);
            return durable != null
                ? new CopilotAdoptionScoreSettingsStore(durable, isDurable: true)
                : new CopilotAdoptionScoreSettingsStore(new InMemoryKeyValueStore(), isDurable: false);
        });

        /// <summary>The deployment's provider, shared by the report coordinator and the settings page.</summary>
        public static readonly CopilotAdoptionScoreSettingsProvider Production =
            new CopilotAdoptionScoreSettingsProvider(() => ProductionStore.Value);

        private readonly Func<CopilotAdoptionScoreSettingsStore> _store;
        private readonly TimeSpan _refreshInterval;
        private readonly Func<DateTime> _utcNow;
        private readonly SemaphoreSlim _readGate = new SemaphoreSlim(1, 1);
        private volatile Cached _cached;
        private long _invalidations;

        public CopilotAdoptionScoreSettingsProvider(
            Func<CopilotAdoptionScoreSettingsStore> store,
            TimeSpan? refreshInterval = null,
            Func<DateTime> utcNow = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public CopilotAdoptionScoreSettingsStore Store => _store();

        public CopilotAdoptionEffectiveScoreSettings LastKnown => _cached?.Settings ?? CopilotAdoptionEffectiveScoreSettings.Defaults;

        public async Task<CopilotAdoptionEffectiveScoreSettings> GetAsync()
        {
            var cached = _cached;
            if (IsFresh(cached)) return cached.Settings;

            await _readGate.WaitAsync().ConfigureAwait(false);
            try
            {
                cached = _cached;
                if (IsFresh(cached)) return cached.Settings;

                var invalidationsBefore = Interlocked.Read(ref _invalidations);
                var document = await _store().GetAsync().ConfigureAwait(false);
                var settings = document.ToEffective();
                // A save that landed while this read was in flight must not be overwritten by the older value.
                if (Interlocked.Read(ref _invalidations) == invalidationsBefore)
                {
                    _cached = new Cached(settings, _utcNow());
                }
                return settings;
            }
            finally
            {
                _readGate.Release();
            }
        }

        /// <summary>Forgets the cached settings, so this instance reads the new ones on its next request.</summary>
        public void Invalidate()
        {
            Interlocked.Increment(ref _invalidations);
            _cached = null;
        }

        /// <summary>Records settings just written by this instance, so its next report uses them at once.</summary>
        public void Publish(CopilotAdoptionScoreSettingsDocument document)
        {
            Interlocked.Increment(ref _invalidations);
            _cached = document == null ? null : new Cached(document.ToEffective(), _utcNow());
        }

        private bool IsFresh(Cached cached) =>
            cached != null && _utcNow() - cached.ReadUtc < _refreshInterval && _utcNow() >= cached.ReadUtc;

        private sealed class Cached
        {
            public Cached(CopilotAdoptionEffectiveScoreSettings settings, DateTime readUtc)
            {
                Settings = settings;
                ReadUtc = readUtc;
            }

            public CopilotAdoptionEffectiveScoreSettings Settings { get; }
            public DateTime ReadUtc { get; }
        }
    }
}
