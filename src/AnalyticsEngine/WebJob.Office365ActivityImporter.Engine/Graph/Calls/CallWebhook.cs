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
        ///   (b) what Graph returned (status + message),
        ///   (c) for the common 403 case, exactly which Graph Application permission to grant, and
        ///   (d) for a failed validation callback, that the fault is at OUR endpoint rather than in
        ///       Graph permissions - see issue #273.
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

            var validation = CallSubscriptionRules.ClassifyValidationCallbackFailure(graphError);

            if (validation.IsValidationCallbackFailure)
            {
                Telemetry.LogCritical(
                    $"{LOG_TAG} Couldn't {action} for call-records at '{webAppUrl}'. {statusLine} " +
                    BuildValidationCallbackDiagnosis(validation, webAppUrl) +
                    $" Until this is fixed, NO Teams call records will be imported. Graph error: '{graphError}'");
            }
            else if (statusCode == (int)HttpStatusCode.Forbidden)
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

        /// <summary>
        /// The operator-facing explanation of a failed validation callback. Deliberately lists the
        /// candidate causes rather than asserting one: which it is can only be established against the
        /// affected deployment, and naming the wrong one sends an admin down a days-long dead end.
        /// </summary>
        private static string BuildValidationCallbackDiagnosis(SubscriptionValidationFailure validation, Uri webAppUrl)
        {
            // Common to both variants: say plainly which way the failing request travelled, because the
            // direction is the single most misunderstood thing about this error.
            var diagnosis =
                "This is NOT a Graph permission problem. Graph accepted the request and then called BACK to " +
                $"this deployment's notification endpoint '{webAppUrl}' to validate it, and that callback failed. " +
                $"The endpoint must be reachable from the public internet (Graph calls from Microsoft's own IP " +
                $"ranges, not from your network), must accept the request ANONYMOUSLY, and must answer 200 OK " +
                $"echoing the 'validationToken' query parameter. ";

            if (validation.TimedOut)
            {
                diagnosis +=
                    "Graph gave up waiting for a response, which usually means App Service cold start: the first " +
                    "request after an idle period or a deploy can take far longer than Graph's validation window. " +
                    "Check: (1) is 'Always On' enabled on the App Service plan; (2) does the site stay warm between " +
                    "import cycles; (3) does the endpoint respond quickly when called from outside your network.";
            }
            else
            {
                var returned = string.IsNullOrEmpty(validation.EndpointStatus)
                    ? "a non-200 response"
                    : $"'{validation.EndpointStatus}'";

                diagnosis +=
                    $"The endpoint returned {returned} to Graph. Check, in order: " +
                    "(1) App Service access restrictions / IP allow-list, or a private endpoint with public network " +
                    "access disabled - any of these refuse Graph before the request reaches the app; " +
                    "(2) an authentication layer in front of the endpoint (App Service Authentication / 'Easy Auth' " +
                    "with 'require authentication', a WAF, or an App Gateway / Front Door rule) - the validation " +
                    "handshake is unauthenticated by design and any challenge fails it; " +
                    "(3) that the notification URL above matches the deployed hostname. " +
                    "Note that the installer's own webhook warm-up POST proves only that the endpoint answers from " +
                    "the network the installer ran on - it does not prove Graph can reach it.";
            }

            return diagnosis;
        }
    }
}
