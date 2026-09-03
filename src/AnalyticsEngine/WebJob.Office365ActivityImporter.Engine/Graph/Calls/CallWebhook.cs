using Azure.Identity;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Net;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Used to ensure call webhooks are in place & valid
    /// </summary>
    public class CallWebhook
    {
        private readonly ICallRecordSubscriptionManager _subscriptions;
        private readonly IClock _clock;

        public ILogger Telemetry { get; set; }

        public CallWebhook(AppConfig o365DownloadSettings, ILogger logger)
            : this(o365DownloadSettings?.TenantGUID.ToString(), o365DownloadSettings?.ClientID, o365DownloadSettings?.ClientSecret, logger) { }

        public CallWebhook(string tenantId, string clientId, string secret, ILogger logger)
            : this(new GraphCallRecordSubscriptionManager(new GraphServiceClient(new ClientSecretCredential(tenantId, clientId, secret))), logger, SystemClock.Instance) { }

        /// <summary>
        /// Constructor taking the Graph subscription API as a port and the clock as a dependency, so
        /// the create/renew decision and the failure reporting can be tested without Graph.
        /// See issue #378.
        /// </summary>
        public CallWebhook(ICallRecordSubscriptionManager subscriptions, ILogger logger, IClock clock)
        {
            _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
            this.Telemetry = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        // Graph Application permission required to subscribe to /communications/callRecords. Surfaced in error
        // messages so operators see the exact permission to grant (matches docs/wiki - Prerequisites.md).
        internal const string REQUIRED_GRAPH_PERMISSION = "CallRecords.Read.All";

        // Grep-friendly tag so the calls-webhook lifecycle is easy to filter in App Insights traces.
        private const string LOG_TAG = "[Calls Webhook]";

        public async Task CreateOrUpdateWebhook(Uri webAppUrl, string secret)
        {
            // https://docs.microsoft.com/en-us/graph/api/resources/webhooks?view=graph-rest-1.0
            var matchingSubs = await _subscriptions.FindCallRecordSubscriptions(webAppUrl);
            var action = CallSubscriptionRules.Decide(matchingSubs, _clock.UtcNow);

            if (action.Kind == CallSubscriptionActionKind.Create)
            {
                Telemetry.LogInformation($"{LOG_TAG} No subscription found for call-records, for URL '{webAppUrl}'. Creating...");
                try
                {
                    var result = await _subscriptions.CreateSubscription(webAppUrl, secret, action.ExpiryUtc);
                    Telemetry.LogInformation($"{LOG_TAG} Created subscription id '{result.Id}' for webhook at '{webAppUrl}'. Teams call records will start importing as calls end.");
                }
                catch (ODataError ex)
                {
                    LogSubscriptionFailure(ex, webAppUrl, isUpdate: false);
                    throw;
                }
            }
            else
            {
                // https://docs.microsoft.com/en-us/graph/api/subscription-update?view=graph-rest-beta&tabs=http
                var existingSubId = action.ExistingSubscriptionId;
                try
                {
                    var result = await _subscriptions.RenewSubscription(existingSubId, action.ExpiryUtc);
                    Telemetry.LogInformation($"{LOG_TAG} Renewed subscription id '{result.Id}' for webhook at '{webAppUrl}'. New expiry: {result.ExpirationDateTime:u}.");
                }
                catch (ODataError ex)
                {
                    LogSubscriptionFailure(ex, webAppUrl, isUpdate: true, existingSubId: existingSubId);
                    throw;
                }
            }
        }

        /// <summary>
        /// Read-only check of the current call-records webhook subscription, for status display
        /// (e.g. the web homepage). Does NOT create or renew anything. Returns whether a matching
        /// subscription currently exists and, if so, when it expires. Any Graph error is allowed to
        /// propagate so the caller can surface it as an explicit "couldn't check" state.
        /// </summary>
        public async Task<CallRecordSubscriptionInfo> GetCallRecordsSubscriptionInfo(Uri webAppUrl)
        {
            var matchingSubs = await _subscriptions.FindCallRecordSubscriptions(webAppUrl);

            // If more than one matches (shouldn't normally happen), report the one that expires
            // latest - that's the subscription keeping the webhook alive.
            var current = CallSubscriptionRules.SelectCurrentForStatus(matchingSubs);

            if (current == null)
            {
                return new CallRecordSubscriptionInfo { Exists = false };
            }

            return new CallRecordSubscriptionInfo
            {
                Exists = true,
                SubscriptionId = current.Id,
                ExpirationDateTime = current.ExpirationDateTime,
            };
        }

        /// <summary>
        /// Emit a single, actionable critical log so operators can immediately tell:
        ///   (a) that the Teams calls import is broken,
        ///   (b) what Graph returned (status + message), and
        ///   (c) for the common 403 case, exactly which Graph Application permission to grant.
        /// The exception is then re-thrown by the caller so Program.cs's outer catch also runs
        /// TrackException, surfacing it in App Insights' Failures blade.
        /// </summary>
        private void LogSubscriptionFailure(ODataError ex, Uri webAppUrl, bool isUpdate, string existingSubId = null)
        {
            var action = isUpdate ? $"renew subscription '{existingSubId}'" : "create subscription";
            var statusCode = ex.ResponseStatusCode;
            var statusLine = statusCode > 0
                ? (Enum.IsDefined(typeof(HttpStatusCode), statusCode)
                    ? $"Graph returned {statusCode} {(HttpStatusCode)statusCode}."
                    : $"Graph returned {statusCode}.")
                : "Graph call failed before a status code was returned.";
            var graphError = ex.Error?.Message ?? ex.Message;

            if (statusCode == (int)HttpStatusCode.Forbidden)
            {
                Telemetry.LogCritical(
                    $"{LOG_TAG} Couldn't {action} for call-records at '{webAppUrl}'. {statusLine} " +
                    $"This is almost always because the importer's Azure AD app registration is missing the " +
                    $"'{REQUIRED_GRAPH_PERMISSION}' Application permission on Microsoft Graph, or admin consent " +
                    $"has not been granted on it. " +
                    $"Fix: Azure portal -> Azure AD -> App registrations -> <importer app> -> API permissions -> " +
                    $"Add a permission -> Microsoft Graph -> Application permissions -> {REQUIRED_GRAPH_PERMISSION} -> " +
                    $"Grant admin consent for the tenant. " +
                    $"Until this is fixed, NO Teams call records will be imported. Graph error: '{graphError}'");
            }
            else
            {
                Telemetry.LogCritical(
                    $"{LOG_TAG} Couldn't {action} for call-records at '{webAppUrl}'. {statusLine} " +
                    $"Until this is fixed, NO Teams call records will be imported. Graph error: '{graphError}'");
            }
        }
    }
}
