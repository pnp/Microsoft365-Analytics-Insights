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
        Task<CopilotAdoptionEffectiveScoreSettings> GetAsync(CancellationToken cancellationToken = default);

        /// <summary>The last settings read, without touching storage; the defaults before the first read.</summary>
        CopilotAdoptionEffectiveScoreSettings LastKnown { get; }
    }

    /// <summary>Always the built-in defaults. What a coordinator built without a settings source uses.</summary>
    internal sealed class DefaultCopilotAdoptionScoreSettingsSource : ICopilotAdoptionScoreSettingsSource
    {
        public static readonly DefaultCopilotAdoptionScoreSettingsSource Instance = new DefaultCopilotAdoptionScoreSettingsSource();
        private static readonly Task<CopilotAdoptionEffectiveScoreSettings> Completed =
            Task.FromResult(CopilotAdoptionEffectiveScoreSettings.Defaults);

        public Task<CopilotAdoptionEffectiveScoreSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Completed;
        }

        public CopilotAdoptionEffectiveScoreSettings LastKnown => CopilotAdoptionEffectiveScoreSettings.Defaults;
    }

    /// <summary>
    /// Reads the score settings afresh for every adoption report request, so the first request after a save -
    /// on any instance of a scaled-out web app - starts an analysis with the new settings.
    /// </summary>
    /// <remarks>
    /// <para>One point read of one table row per report request: not per user, not per analysis step, and not per
    /// query the analysis runs. Requests are not merged onto a read already in flight, because a read that started
    /// before another instance's save committed can return the old values to a request made after it - exactly the
    /// staleness this provider exists to rule out. The version is part of the analysis cache key, so a request
    /// carrying new settings never joins or reuses an analysis scored with the old ones.</para>
    /// <para>A failed read is not cached and is not papered over with the last good value or the defaults: the
    /// caller gets <see cref="CopilotAdoptionScoreSettingsUnavailableException"/>, the report answers 503, and the
    /// next request tries again.</para>
    /// </remarks>
    internal sealed class CopilotAdoptionScoreSettingsProvider : ICopilotAdoptionScoreSettingsSource
    {
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
        private readonly object _lastKnownLock = new object();
        private CopilotAdoptionEffectiveScoreSettings _lastKnown;
        private long _lastKnownSequence;
        private long _sequence;

        public CopilotAdoptionScoreSettingsProvider(Func<CopilotAdoptionScoreSettingsStore> store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public CopilotAdoptionScoreSettingsStore Store => _store();

        public CopilotAdoptionEffectiveScoreSettings LastKnown
        {
            get
            {
                lock (_lastKnownLock) return _lastKnown ?? CopilotAdoptionEffectiveScoreSettings.Defaults;
            }
        }

        public async Task<CopilotAdoptionEffectiveScoreSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            // Numbered when the read starts, so a slow read that finishes after a newer one cannot replace LastKnown.
            var sequence = Interlocked.Increment(ref _sequence);
            var document = await _store().GetAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var settings = document.ToEffective();
            Remember(settings, sequence);
            return settings;
        }

        /// <summary>Records settings just written by this instance as the last known ones.</summary>
        public void Publish(CopilotAdoptionScoreSettingsDocument document)
        {
            if (document == null) return;
            Remember(document.ToEffective(), Interlocked.Increment(ref _sequence));
        }

        private void Remember(CopilotAdoptionEffectiveScoreSettings settings, long sequence)
        {
            lock (_lastKnownLock)
            {
                if (sequence < _lastKnownSequence) return;
                _lastKnownSequence = sequence;
                _lastKnown = settings;
            }
        }
    }
}