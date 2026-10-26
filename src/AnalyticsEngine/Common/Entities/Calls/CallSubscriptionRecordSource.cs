using Common.Entities.State;
using DataUtils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.Calls
{
    /// <summary>The importer's <see cref="CallSubscriptionRecord"/>, as the web app reads it.</summary>
    public interface ICallSubscriptionRecordSource
    {
        /// <summary>The record, or null when there is none or it can't be read. Never throws.</summary>
        Task<CallSubscriptionRecord> GetAsync();
    }

    /// <summary>
    /// Reads the importer's record from the runtime state table at most once per <see cref="DefaultRefresh"/>, whatever
    /// the notification traffic - the webhook is anonymous, so a stranger's POSTs must not turn into storage reads.
    /// Only consulted for a notification whose clientState matched neither the current nor the legacy value.
    /// </summary>
    public sealed class CachedCallSubscriptionRecordSource : ICallSubscriptionRecordSource
    {
        public static readonly TimeSpan DefaultRefresh = TimeSpan.FromMinutes(1);

        private readonly IKeyValueStore _store;
        private readonly string _key;
        private readonly IClock _clock;
        private readonly ILogger _logger;
        private readonly TimeSpan _refresh;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private CallSubscriptionRecord _cached;
        private long _loadedTicks; // 0 = never loaded; ticks so a lock-free read is never torn
        private bool _readFailing;

        /// <param name="notificationUrl">The subscription's notification URL - the record's key (<see cref="CallSubscriptionRules.CurrentNotificationUrl"/>).</param>
        public CachedCallSubscriptionRecordSource(IKeyValueStore store, Uri notificationUrl, IClock clock, ILogger logger, TimeSpan? refresh = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _key = (notificationUrl ?? throw new ArgumentNullException(nameof(notificationUrl))).ToString();
            _clock = clock ?? SystemClock.Instance;
            _logger = logger;
            _refresh = refresh ?? DefaultRefresh;
        }

        /// <summary>
        /// The web app's source for its configured <c>WebAppURL</c>, or null when there is no Storage account or no valid
        /// URL - then a rotated secret's subscription is refused, as it always was.
        /// </summary>
        public static ICallSubscriptionRecordSource ForConfig(Config.AppConfig config, ILogger logger)
        {
            var webhookUrl = CallSubscriptionRules.WebhookUrlFor(config?.WebAppURL);
            if (webhookUrl == null) return null;
            var store = StateStore.TryOpen(config, StatePartitions.CallsWebhook, logger);
            return store == null ? null : new CachedCallSubscriptionRecordSource(store, CallSubscriptionRules.CurrentNotificationUrl(webhookUrl), SystemClock.Instance, logger);
        }

        public async Task<CallSubscriptionRecord> GetAsync()
        {
            if (IsFresh()) return Volatile.Read(ref _cached);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsFresh()) return _cached;

                CallSubscriptionRecord record = null;
                try
                {
                    var json = await _store.GetStringAsync(_key).ConfigureAwait(false);
                    if (json != null)
                    {
                        try
                        {
                            record = JsonConvert.DeserializeObject<CallSubscriptionRecord>(json);
                        }
                        catch (JsonException)
                        {
                            record = null;
                        }
                    }
                    if (_readFailing)
                    {
                        _logger?.LogInformation("[Calls Webhook] The webhook can read the importer's subscription record from the runtime state table again.");
                    }
                    _readFailing = false;
                }
                catch (Exception ex)
                {
                    // Fail closed: without the record, only the current clientState is accepted. Logged once per outage.
                    if (!_readFailing)
                    {
                        _logger?.LogWarning($"[Calls Webhook] The webhook couldn't read the importer's subscription record from the runtime state table " +
                            $"({ex.GetType().Name}: {ex.Message}). Until it can, notifications from a subscription created with the client secret in " +
                            "use before a rotation are refused. Retried at most once a minute.");
                    }
                    _readFailing = true;
                }

                Volatile.Write(ref _cached, record);
                Interlocked.Exchange(ref _loadedTicks, _clock.UtcNow.Ticks);
                return record;
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool IsFresh()
        {
            var loaded = Interlocked.Read(ref _loadedTicks);
            return loaded != 0 && _clock.UtcNow.Ticks - loaded < _refresh.Ticks;
        }
    }
}
