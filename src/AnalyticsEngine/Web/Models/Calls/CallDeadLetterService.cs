using Azure.Core;
using Common.Entities.Calls;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Web.AnalyticsWeb.Models.Health;

namespace Web.AnalyticsWeb.Models.Calls
{
    /// <summary>
    /// What the Service configuration page's dead-lettered calls card shows and does: the dead-letter count, the latest
    /// request to reprocess or clear the dead-letter queue and its result, and recording a new request.
    /// </summary>
    /// <remarks>
    /// The web app only records a request (<see cref="CallDeadLetterRequestStore"/>); the importer, which owns the Service
    /// Bus client and the replay, carries it out and records the result. So a request needs the calls import, a Service Bus
    /// queue, and a runtime state table both processes can see - without a Storage connection string the web app could
    /// only keep it in its own memory, where the importer never looks.
    /// </remarks>
    internal sealed class CallDeadLetterService
    {
        private const string LogContext = "CallDeadLetters";

        /// <summary>The longest the page waits for Service Bus's counts before showing the card without them.</summary>
        internal static readonly TimeSpan CountTimeout = TimeSpan.FromSeconds(10);

        /// <summary>A certificate rotated in Key Vault is picked up within this time.</summary>
        private static readonly TimeSpan CredentialLifetime = TimeSpan.FromMinutes(10);

        private static readonly Lazy<AnalyticsLogger> ProductionLogger = new Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, LogContext));

        private static readonly SemaphoreSlim CredentialGate = new SemaphoreSlim(1, 1);
        private static TokenCredential _credential;
        private static string _credentialKey;
        private static DateTime _credentialBuiltUtc;

        private readonly bool _callsImportEnabled;
        private readonly bool _serviceBusConfigured;
        private readonly CallDeadLetterRequestStore _store;
        private readonly Func<CancellationToken, Task<CallQueueDepth>> _readDepth;
        private readonly Func<DateTime> _utcNow;
        private readonly ILogger _logger;

        /// <param name="readDepth">Reads the queue's counts; null when there is no queue.</param>
        internal CallDeadLetterService(bool callsImportEnabled, bool serviceBusConfigured, CallDeadLetterRequestStore store,
            Func<CancellationToken, Task<CallQueueDepth>> readDepth, Func<DateTime> utcNow, ILogger logger)
        {
            _callsImportEnabled = callsImportEnabled;
            _serviceBusConfigured = serviceBusConfigured;
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _readDepth = readDepth;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        internal static CallDeadLetterService ForThisDeployment()
        {
            var config = new AppConfig();
            var serviceBus = config.ConnectionStrings?.ServiceBusConnectionString;
            var serviceBusConfigured = !string.IsNullOrWhiteSpace(serviceBus);
            return new CallDeadLetterService(
                config.ImportJobSettings?.Calls == true,
                serviceBusConfigured,
                CallDeadLetterRequestStore.ForConfig(config),
                serviceBusConfigured ? (Func<CancellationToken, Task<CallQueueDepth>>)(async ct => await CallQueueDepth.ReadAsync(serviceBus, await CredentialAsync(config), ct)) : null,
                () => DateTime.UtcNow,
                ProductionLogger.Value);
        }

        /// <summary>Why a request can't reach the importer (<see cref="CallDeadLetterUnavailableReasons"/>); null when it can.</summary>
        internal string UnavailableReason
        {
            get
            {
                if (!_callsImportEnabled) return CallDeadLetterUnavailableReasons.CallsImportDisabled;
                if (!_serviceBusConfigured) return CallDeadLetterUnavailableReasons.ServiceBusNotConfigured;
                if (!_store.IsDurable) return CallDeadLetterUnavailableReasons.StateStoreInMemory;
                return null;
            }
        }

        internal async Task<CallDeadLetterStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            var status = new CallDeadLetterStatus
            {
                UnavailableReason = UnavailableReason,
                PickUpWithinMinutes = (int)CallDeadLetterRequestRules.PickUpWithin.TotalMinutes,
                FinishWithinMinutes = (int)CallDeadLetterRequestRules.FinishWithin.TotalMinutes,
                RunBudgetMinutes = (int)CallDeadLetterRequestRules.RunBudget.TotalMinutes
            };
            status.Available = status.UnavailableReason == null;

            var readCount = _callsImportEnabled && _readDepth != null ? ReadCountAsync(status, cancellationToken) : Task.CompletedTask;
            try
            {
                var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
                status.Latest = CallDeadLetterRequestView.From(snapshot.Request, _utcNow());
            }
            catch (CallDeadLetterStateUnavailableException ex)
            {
                status.LatestUnavailable = true;
                _logger.LogWarning($"Teams calls dead-letter requests: couldn't read the latest request ({ex.InnerException?.GetType().Name ?? ex.GetType().Name}).");
            }
            await readCount.ConfigureAwait(false);

            status.CanRequest = status.Available && !status.LatestUnavailable &&
                (status.Latest == null || (status.Latest.State != CallDeadLetterRequestStates.Waiting && status.Latest.State != CallDeadLetterRequestStates.Running));
            return status;
        }

        /// <summary>
        /// Records a request for the importer, replacing the latest one when that has finished or gone stale. Throws
        /// <see cref="CallDeadLetterRequestException"/> with the answer when it is refused.
        /// </summary>
        internal async Task<CallDeadLetterStatus> SubmitAsync(string action, CancellationToken cancellationToken)
        {
            var parsed = CallDeadLetterActions.Parse(action);
            if (parsed == null)
            {
                throw new CallDeadLetterRequestException(HttpStatusCode.BadRequest, CallDeadLetterErrorCodes.UnknownAction);
            }

            var unavailable = UnavailableReason;
            if (unavailable != null)
            {
                throw new CallDeadLetterRequestException(HttpStatusCode.Conflict, unavailable);
            }

            // Two tries: the first can lose only to a write that landed since the read - the importer finishing, or another
            // administrator's request, which the second read then finds pending.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                CallDeadLetterRequest request;
                try
                {
                    var snapshot = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
                    var now = _utcNow();
                    if (CallDeadLetterRequestRules.IsPending(snapshot.Request, now))
                    {
                        throw new CallDeadLetterRequestException(HttpStatusCode.Conflict, CallDeadLetterErrorCodes.RequestPending);
                    }

                    request = new CallDeadLetterRequest { Id = Guid.NewGuid().ToString("N"), Action = parsed, RequestedUtc = now };
                    if (!await _store.TryReplaceAsync(request, snapshot.VersionToken, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                }
                catch (CallDeadLetterStateUnavailableException ex)
                {
                    _logger.LogWarning($"Teams calls dead-letter requests: couldn't record a {parsed} request ({ex.InnerException?.GetType().Name ?? ex.GetType().Name}).");
                    throw new CallDeadLetterRequestException(HttpStatusCode.ServiceUnavailable, CallDeadLetterErrorCodes.StateStoreUnavailable);
                }

                _logger.LogInformation($"Teams calls dead-letter {parsed} requested from the portal (request {request.Id}); the importer checks for requests " +
                    $"every {CallDeadLetterRequestRules.PollInterval.TotalMinutes:0} minute(s).");
                return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }

            throw new CallDeadLetterRequestException(HttpStatusCode.Conflict, CallDeadLetterErrorCodes.RequestPending);
        }

        private async Task ReadCountAsync(CallDeadLetterStatus status, CancellationToken cancellationToken)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(CountTimeout);
                try
                {
                    var depth = await _readDepth(timeout.Token).ConfigureAwait(false);
                    status.DeadLetterCount = depth.DeadLetterMessageCount;
                    status.ActiveCount = depth.ActiveMessageCount;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // The Health page explains a Service Bus failure in full; the card only says the count is unknown.
                    status.CountUnavailable = true;
                    _logger.LogWarning($"Teams calls dead-letter requests: couldn't read the calls queue's counts ({ex.GetType().Name}).");
                }
            }
        }

        private static async Task<TokenCredential> CredentialAsync(AppConfig config)
        {
            // Keyed on the identity it was built for, so a configuration change is picked up rather than masked by the cache.
            var key = config.TenantGUID + "|" + config.ClientID + "|" + (config.UseClientCertificate ? "cert|" + config.KeyVaultUrl : "secret");
            await CredentialGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_credential == null || _credentialKey != key || DateTime.UtcNow - _credentialBuiltUtc >= CredentialLifetime)
                {
                    _credential = await HealthService.BuildCredential(config).ConfigureAwait(false);
                    _credentialKey = key;
                    _credentialBuiltUtc = DateTime.UtcNow;
                }
                return _credential;
            }
            finally
            {
                CredentialGate.Release();
            }
        }
    }
}
