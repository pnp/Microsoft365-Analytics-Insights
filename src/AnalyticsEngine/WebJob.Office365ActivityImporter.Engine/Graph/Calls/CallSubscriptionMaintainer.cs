using Common.Entities.Calls;
using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Keeps the Teams calls webhook subscription alive on its own timer, independent of the import cycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subscription used to be checked once per import cycle, at its start. That had two failure modes (E-2 and E-7 of
    /// the release review). A client-secret rotation restarts the web app cold, so the first attempt to replace the
    /// subscription usually failed Graph's validation of the endpoint - and the next attempt came a whole cycle later,
    /// hours on a large tenant, with every call in between refused by the webhook. And with a two-day expiry renewed only
    /// at the start of a cycle, a cycle longer than two days (the first full Teams crawl) let the subscription lapse.
    /// </para>
    /// <para>
    /// Now: a check every <see cref="HealthyCheckInterval"/> (renewing once less than
    /// <see cref="CallSubscriptionRules.RenewWhenRemainingBelow"/> is left), the webhook warmed up before every create or
    /// renewal, and a failure retried after <see cref="RetryDelays"/>. Failures are Warnings for the first attempts and
    /// escalate at <see cref="EscalateAtAttempt"/>. One process-lifetime instance, created by <see cref="StartIfEnabled"/>.
    /// </para>
    /// </remarks>
    public sealed class CallSubscriptionMaintainer : IDisposable
    {
        public static readonly TimeSpan HealthyCheckInterval = TimeSpan.FromMinutes(30);
        public static readonly TimeSpan SummaryInterval = TimeSpan.FromHours(1);

        /// <summary>The wait after the 1st, 2nd... consecutive failure; the last repeats.</summary>
        public static readonly IReadOnlyList<TimeSpan> RetryDelays = new[]
        {
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)
        };

        /// <summary>From this attempt on, a failure is reported as the feature being down rather than degraded.</summary>
        public const int EscalateAtAttempt = 3;

        private const string LOG_TAG = "[Calls Webhook]";

        private readonly CallWebhook _webhook;
        private readonly Uri _webhookUrl;
        private readonly CallWebhookClientState _clientState;
        private readonly ICallWebhookWarmer _warmer;
        private readonly ILogger _logger;
        private readonly IClock _clock;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        private DateTime _nextRunUtc = DateTime.MinValue;
        private DateTime _nextSummaryUtc;
        private DateTime _summarySinceUtc;
        private int _consecutiveFailures;
        private DateTime? _firstFailureUtc;
        private string _lastFailure;
        private string _subscriptionId;
        private DateTime? _expiresUtc;
        private int _checksSinceSummary;
        private int _changesSinceSummary;
        private int _failuresSinceSummary;

        public CallSubscriptionMaintainer(CallWebhook webhook, Uri webhookUrl, CallWebhookClientState clientState, ICallWebhookWarmer warmer, ILogger logger, IClock clock)
        {
            _webhook = webhook ?? throw new ArgumentNullException(nameof(webhook));
            _webhookUrl = webhookUrl ?? throw new ArgumentNullException(nameof(webhookUrl));
            _clientState = clientState ?? throw new ArgumentNullException(nameof(clientState));
            _warmer = warmer;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? SystemClock.Instance;
            _summarySinceUtc = _clock.UtcNow;
            _nextSummaryUtc = _summarySinceUtc + SummaryInterval;
            if (_warmer != null) _webhook.BeforeValidatedChangeAsync = WarmUpAsync;
        }

        /// <summary>When the next check is due.</summary>
        public DateTime NextRunUtc => _nextRunUtc;

        public int ConsecutiveFailures => _consecutiveFailures;

        /// <summary>
        /// Starts the timer when the calls import needs it: enabled, with a webhook URL, Service Bus (without it the cycle
        /// reports the import as misconfigured) and a client secret. Returns null otherwise. Call once per process.
        /// </summary>
        public static CallSubscriptionMaintainer StartIfEnabled(AppConfig config, Uri webhookUrl, ILogger logger)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (webhookUrl == null || config.ImportJobSettings == null || !config.ImportJobSettings.Calls) return null;
            if (string.IsNullOrWhiteSpace(config.ConnectionStrings?.ServiceBusConnectionString)) return null;

            var clientState = CallWebhookClientState.ForConfig(config);
            if (!clientState.IsAvailable)
            {
                logger.LogCritical(CallWebhook.NO_CLIENT_SECRET_MESSAGE);
                return null;
            }

            // Process lifetime, like the maintainer: this process's copy of which subscription it created.
            var records = new CallSubscriptionRecordStore(StateStore.TryOpen(config, CallSubscriptionRecordStore.Partition, logger), new InMemoryKeyValueStore());
            var maintainer = new CallSubscriptionMaintainer(new CallWebhook(config, logger, records), webhookUrl, clientState,
                new HttpCallWebhookWarmer(), logger, SystemClock.Instance);
            maintainer.Start();
            logger.LogInformation($"{LOG_TAG} Subscription timer started: checks every {HealthyCheckInterval.TotalMinutes:0} minutes, independently of the import cycle; " +
                $"renews when under {CallSubscriptionRules.RenewWhenRemainingBelow.TotalHours:0} h remain; retries a failure after " +
                $"{string.Join(", ", Array.ConvertAll(((TimeSpan[])RetryDelays), d => d.TotalMinutes.ToString("0")))} minutes.");
            return maintainer;
        }

        public void Start()
        {
            Task.Run(() => LoopAsync(_stop.Token));
        }

        private async Task LoopAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // RunOnceAsync handles its own failures; this only keeps the timer alive through a bug.
                    _logger.LogError(ex, $"{LOG_TAG} Subscription timer tick failed unexpectedly ({ex.GetType().Name}: {ex.Message}); the timer carries on.");
                }

                var nextDue = _nextRunUtc < _nextSummaryUtc ? _nextRunUtc : _nextSummaryUtc;
                var wait = nextDue - _clock.UtcNow;
                if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
                if (wait > TimeSpan.FromMinutes(1)) wait = TimeSpan.FromMinutes(1);
                try
                {
                    await Task.Delay(wait, stop).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Checks the subscription if a check is due, then logs the summary if that is due. Never throws for a failed check.</summary>
        public async Task RunOnceAsync()
        {
            if (_clock.UtcNow >= _nextRunUtc)
            {
                await CheckAsync().ConfigureAwait(false);
            }

            if (_clock.UtcNow >= _nextSummaryUtc)
            {
                LogSummary();
            }
        }

        private async Task CheckAsync()
        {
            var attempt = _consecutiveFailures + 1;
            _webhook.SubscriptionFailureLevel = attempt >= EscalateAtAttempt ? LogLevel.Critical : LogLevel.Warning;
            _checksSinceSummary++;
            var startedUtc = _clock.UtcNow;
            var timer = Stopwatch.StartNew();
            try
            {
                var result = await _webhook.CreateOrUpdateWebhook(_webhookUrl, _clientState).ConfigureAwait(false);
                _subscriptionId = result.SubscriptionId;
                _expiresUtc = result.ExpiresUtc;
                if (result.Change != CallWebhookChange.None) _changesSinceSummary++;

                if (_consecutiveFailures > 0)
                {
                    _logger.LogInformation($"{LOG_TAG} Recovered: the subscription check succeeded on attempt {attempt} in {timer.ElapsedMilliseconds} ms, after failing since " +
                        $"{_firstFailureUtc:u}. Subscription '{result.SubscriptionId}' ({result.Change}) expires {result.ExpiresUtc:u}.");
                }

                _consecutiveFailures = 0;
                _firstFailureUtc = null;
                _lastFailure = null;
                _nextRunUtc = _clock.UtcNow + HealthyCheckInterval;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                _failuresSinceSummary++;
                if (_firstFailureUtc == null) _firstFailureUtc = startedUtc;
                _lastFailure = Describe(ex);
                var delay = RetryDelayAfter(_consecutiveFailures);
                _nextRunUtc = _clock.UtcNow + delay;

                var escalated = _consecutiveFailures >= EscalateAtAttempt;
                var message = $"{LOG_TAG} Retry pending: the subscription check failed on attempt {_consecutiveFailures} after {timer.ElapsedMilliseconds} ms ({_lastFailure}). " +
                    $"Next try at {_nextRunUtc:u} (in {delay.TotalMinutes:0} min); failing since {_firstFailureUtc:u}." +
                    (escalated ? " Teams call notifications are at risk until it succeeds - see the [Calls Webhook] diagnosis above." : string.Empty);
                if (escalated)
                {
                    _logger.LogError(ex, message);
                }
                else
                {
                    _logger.LogWarning(message);
                }
            }
        }

        /// <summary>The wait before the next attempt after <paramref name="consecutiveFailures"/> failures in a row.</summary>
        public static TimeSpan RetryDelayAfter(int consecutiveFailures)
        {
            var index = Math.Max(0, Math.Min(consecutiveFailures, RetryDelays.Count) - 1);
            return RetryDelays[index];
        }

        private async Task WarmUpAsync()
        {
            var result = await _warmer.WarmUpAsync(CallSubscriptionRules.CurrentNotificationUrl(_webhookUrl), _stop.Token).ConfigureAwait(false);
            if (result.Succeeded)
            {
                _logger.LogInformation($"{LOG_TAG} Webhook warm-up answered in {result.ElapsedMs} ms.");
            }
            else
            {
                _logger.LogWarning($"{LOG_TAG} Webhook warm-up failed after {result.ElapsedMs} ms ({result.Failure}); asking Graph anyway. Graph's own validation " +
                    "of the endpoint will probably fail the same way: the web app is starting, stopped, or refusing requests from this App Service.");
            }
        }

        private void LogSummary()
        {
            var now = _clock.UtcNow;
            string subscription;
            if (_subscriptionId == null)
            {
                subscription = "none confirmed yet";
            }
            else if (_expiresUtc.HasValue && _expiresUtc.Value <= now)
            {
                subscription = $"'{_subscriptionId}' EXPIRED at {_expiresUtc.Value:u} - the webhook receives nothing until a check succeeds";
            }
            else
            {
                subscription = _expiresUtc.HasValue
                    ? $"'{_subscriptionId}', expires {_expiresUtc.Value:u} ({(_expiresUtc.Value - now).TotalHours:0.#} h left)"
                    : $"'{_subscriptionId}', expiry unknown";
            }

            var state = _consecutiveFailures > 0
                ? $"retry pending (attempt {_consecutiveFailures + 1} at {_nextRunUtc:u}; failing since {_firstFailureUtc:u}: {_lastFailure})"
                : $"next check at {_nextRunUtc:u}";
            var message = $"{LOG_TAG} Summary: subscription {subscription}; {_checksSinceSummary} check(s), {_changesSinceSummary} change(s) and " +
                $"{_failuresSinceSummary} failed attempt(s) since {_summarySinceUtc:u}; {state}. Notifications the web app refuses are logged by the web app.";

            if (_consecutiveFailures > 0 || (_expiresUtc.HasValue && _expiresUtc.Value <= now))
            {
                _logger.LogWarning(message);
            }
            else
            {
                _logger.LogInformation(message);
            }

            _checksSinceSummary = 0;
            _changesSinceSummary = 0;
            _failuresSinceSummary = 0;
            _summarySinceUtc = now;
            _nextSummaryUtc = now + SummaryInterval;
        }

        /// <summary>Type, HTTP status and Graph error code - enough to classify it from a log export. No response body.</summary>
        private static string Describe(Exception ex)
        {
            if (ex is ODataError graphError)
            {
                var code = graphError.Error?.Code;
                return $"{nameof(ODataError)}, HTTP {graphError.ResponseStatusCode}" + (string.IsNullOrEmpty(code) ? string.Empty : $", Graph code '{code}'");
            }

            return ex.GetType().Name + ": " + ex.Message;
        }

        public void Dispose()
        {
            _stop.Cancel();
        }
    }
}
