using Azure.Identity;
using Common.Entities.Calls;
using Common.Entities.Config;
using Common.Entities.State;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Used to ensure call webhooks are in place & valid
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subscription's clientState is <see cref="CallWebhookClientState"/>, derived from the client secret, and the
    /// subscription is created on <see cref="CallSubscriptionRules.CurrentNotificationUrl"/> - the webhook URL plus
    /// <c>?cs=v1</c> - so that an older importer, after a rollback, never renews it (see
    /// <see cref="CallSubscriptionRules.ClientStateVersionQuery"/>).
    /// </para>
    /// <para>
    /// Graph can neither change a subscription's clientState nor report it, so the importer records the fingerprint of
    /// the one it created its subscription with (<see cref="ICallSubscriptionRecordStore"/>). A subscription on the
    /// current URL that it has no such record of - one created before the client secret was rotated - is replaced: a new
    /// one is created, then the old one deleted. A legacy subscription on the bare URL, whose clientState is the raw
    /// secret, is replaced too but no longer renewed, and is only deleted after <see cref="LegacyGracePeriod"/>.
    /// </para>
    /// </remarks>
    public class CallWebhook
    {
        private readonly ICallRecordSubscriptionManager _subscriptions;
        private readonly ICallSubscriptionRecordStore _records;
        private readonly IClock _clock;

        public ILogger Telemetry { get; set; }

        /// <param name="records">Must outlive the import cycle; see <see cref="CallSubscriptionRecordStore"/>.</param>
        public CallWebhook(AppConfig o365DownloadSettings, ILogger logger, ICallSubscriptionRecordStore records)
            : this(CreateGraphSubscriptionManager(o365DownloadSettings), records, logger, SystemClock.Instance) { }

        /// <summary>
        /// Constructor taking the Graph subscription API as a port and the clock as a dependency, so
        /// the create/renew decision and the failure reporting can be tested without Graph.
        /// See issue #378. Records which subscription it created for this instance only.
        /// </summary>
        public CallWebhook(ICallRecordSubscriptionManager subscriptions, ILogger logger, IClock clock)
            : this(subscriptions, new CallSubscriptionRecordStore(null, new InMemoryKeyValueStore()), logger, clock) { }

        public CallWebhook(ICallRecordSubscriptionManager subscriptions, ICallSubscriptionRecordStore records, ILogger logger, IClock clock)
        {
            _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
            _records = records ?? throw new ArgumentNullException(nameof(records));
            this.Telemetry = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        private static ICallRecordSubscriptionManager CreateGraphSubscriptionManager(AppConfig settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (string.IsNullOrEmpty(settings.ClientSecret)) throw new InvalidOperationException(NO_CLIENT_SECRET_MESSAGE);

            return new GraphCallRecordSubscriptionManager(GraphServiceClientFactory.CreateWithTimeout(
                new ClientSecretCredential(settings.TenantGUID.ToString(), settings.ClientID, settings.ClientSecret), GraphServiceClientFactory.DefaultGraphSdkTimeout));
        }

        // Graph Application permission required to subscribe to /communications/callRecords. Surfaced in error
        // messages so operators see the exact permission to grant (matches docs/wiki - Prerequisites.md).
        internal const string REQUIRED_GRAPH_PERMISSION = "CallRecords.Read.All";

        // Grep-friendly tag so the calls-webhook lifecycle is easy to filter in App Insights traces.
        private const string LOG_TAG = "[Calls Webhook]";

        internal const string NO_CLIENT_SECRET_MESSAGE = LOG_TAG + " The Teams calls import needs the runtime app registration's " +
            "client secret (app setting 'ClientSecret'): the Graph subscription is created with it, and the webhook's clientState " +
            "is derived from it. None is configured (certificate-only authentication), so no subscription is created and the " +
            "webhook refuses every notification.";

        /// <summary>
        /// How long a replaced legacy subscription is left in place - not renewed - before it is deleted, counted from when
        /// this importer process first found it superseded. Covers an upgrade, when the importer can restart on its new
        /// files a little before the web app loads its own: until then the old web app refuses the new subscription's
        /// notifications and accepts only the legacy one's. Both deliver meanwhile, which is harmless - saving a call
        /// replaces any copy already saved.
        /// </summary>
        public static readonly TimeSpan LegacyGracePeriod = TimeSpan.FromHours(1);

        /// <param name="webAppUrl">The webhook's own URL. The subscription itself is on <see cref="CallSubscriptionRules.CurrentNotificationUrl"/>.</param>
        public async Task CreateOrUpdateWebhook(Uri webAppUrl, CallWebhookClientState clientState)
        {
            if (clientState == null) throw new ArgumentNullException(nameof(clientState));
            if (!clientState.IsAvailable) throw new InvalidOperationException(NO_CLIENT_SECRET_MESSAGE);

            var currentUrl = CallSubscriptionRules.CurrentNotificationUrl(webAppUrl);

            // https://docs.microsoft.com/en-us/graph/api/resources/webhooks?view=graph-rest-1.0
            // Both of this webhook's URLs, and nothing else: see CallSubscriptionRules.IsCallRecordsSubscriptionFor.
            var matchingSubs = await _subscriptions.FindCallRecordSubscriptions(webAppUrl);
            var current = matchingSubs.Where(s => s.NotificationUrl == currentUrl.ToString()).ToList();
            var legacy = matchingSubs.Where(s => s.NotificationUrl == webAppUrl.ToString()).ToList();
            var action = CallSubscriptionRules.Decide(matchingSubs, _clock.UtcNow);

            if (action.Kind == CallSubscriptionActionKind.Create)
            {
                Telemetry.LogInformation($"{LOG_TAG} No subscription found for call-records, for URL '{currentUrl}'. Creating...");
                CallRecordSubscription result;
                try
                {
                    result = await _subscriptions.CreateSubscription(currentUrl, clientState.Value, action.ExpiryUtc);
                }
                catch (ODataError ex)
                {
                    LogSubscriptionFailure(ex, currentUrl, isUpdate: false);
                    throw;
                }
                Telemetry.LogInformation($"{LOG_TAG} Created subscription id '{result.Id}' for webhook at '{currentUrl}'. Teams call records will start importing as calls end.");
                await RecordAsync(currentUrl, result, clientState);
                return;
            }

            CallSubscriptionRecord record;
            try
            {
                record = await _records.GetAsync(currentUrl);
            }
            catch (Exception ex)
            {
                // Can't tell whether anything needs replacing, so keep what exists alive rather than replace one that may be fine.
                var keep = current.FirstOrDefault() ?? legacy[0];
                Telemetry.LogWarning($"{LOG_TAG} Couldn't read which call-records subscription the importer created ({ex.Message}). " +
                    $"Renewing subscription id '{keep.Id}' without checking its clientState; it is checked again next cycle.");
                await RenewAsync(keep.Id, action.ExpiryUtc, current.Count > 0 ? currentUrl : webAppUrl);
                return;
            }

            var keyUnchanged = record != null && string.Equals(record.ClientStateFingerprint, clientState.Fingerprint, StringComparison.Ordinal);
            var ours = keyUnchanged ? current.FirstOrDefault(s => s.Id == record.SubscriptionId) : null;
            if (ours != null)
            {
                await RenewAsync(ours.Id, action.ExpiryUtc, currentUrl);
            }
            else
            {
                var reason = current.Count == 0
                    ? "there is no subscription with the current clientState yet - the existing one predates it, and its " +
                      "clientState is the app's client secret"
                    : record == null
                        ? "the importer has no record of creating it"
                        : !keyUnchanged
                            ? "it was created with a client secret that has since changed, so the webhook refuses its notifications"
                            : "the subscription the importer last created no longer exists";
                ours = current.Count > 0
                    ? await ReplaceAsync(currentUrl, current, currentUrl, clientState, action.ExpiryUtc, reason)
                    : await ReplaceAsync(currentUrl, legacy, webAppUrl, clientState, action.ExpiryUtc, reason);
            }

            // Any other subscription on the current URL is one this replaced: the web app refuses or duplicates its
            // notifications, and an older web app refuses them all, so it goes at once.
            foreach (var replaced in current.Where(s => s.Id != ours.Id))
            {
                await DeleteReplacedAsync(replaced);
            }

            await RetireLegacyAsync(currentUrl, legacy);
        }

        /// <summary>
        /// Graph cannot change a subscription's clientState, so create the replacement and only then remove the old
        /// one(s) - never a moment with no subscription. Notifications from both in between are harmless: a call is saved
        /// by its Graph id, replacing any copy already saved.
        /// </summary>
        /// <returns>The new subscription.</returns>
        private async Task<CallRecordSubscription> ReplaceAsync(Uri currentUrl, IReadOnlyList<CallRecordSubscription> existing, Uri existingUrl,
            CallWebhookClientState clientState, DateTime expiryUtc, string reason)
        {
            var existingIds = string.Join("', '", existing.Select(s => s.Id));
            Telemetry.LogInformation($"{LOG_TAG} Replacing call-records subscription id '{existingIds}' with one for webhook at '{currentUrl}': {reason}. " +
                "The new subscription is created first and the old one removed after it.");

            CallRecordSubscription created;
            try
            {
                created = await _subscriptions.CreateSubscription(currentUrl, clientState.Value, expiryUtc);
            }
            catch (ODataError ex)
            {
                LogSubscriptionFailure(ex, currentUrl, isUpdate: false, replacingSubId: existing[0].Id);

                // Keep the old subscription from lapsing while the replacement is retried each cycle.
                await RenewAsync(existing[0].Id, expiryUtc, existingUrl);
                throw;
            }

            Telemetry.LogInformation($"{LOG_TAG} Created subscription id '{created.Id}' for webhook at '{currentUrl}', replacing '{existingIds}'.");
            await RecordAsync(currentUrl, created, clientState);
            return created;
        }

        /// <summary>
        /// Once replaced, legacy subscriptions - on the bare webhook URL, with the raw client secret as their clientState -
        /// are never renewed again, and are deleted once this process has seen them superseded for <see cref="LegacyGracePeriod"/>.
        /// Even if that never happens (an importer restarting more often than that), they lapse within two days.
        /// </summary>
        private async Task RetireLegacyAsync(Uri currentUrl, IReadOnlyList<CallRecordSubscription> legacy)
        {
            if (legacy.Count == 0)
            {
                await _records.SetLegacySeenAsync(currentUrl, null);
                return;
            }

            var now = _clock.UtcNow;
            var seen = await _records.GetLegacySeenAsync(currentUrl);
            if (seen == null)
            {
                await _records.SetLegacySeenAsync(currentUrl, now);
                Telemetry.LogInformation($"{LOG_TAG} Leaving the previous subscription id '{string.Join("', '", legacy.Select(s => s.Id))}' in place, " +
                    $"no longer renewed, for {LegacyGracePeriod.TotalMinutes:0} minutes before deleting it: during an upgrade the web app " +
                    "can still be the previous version, which only accepts that subscription's notifications. Both deliver meanwhile, which is harmless.");
                return;
            }

            if (now - seen.Value < LegacyGracePeriod) return;

            foreach (var old in legacy)
            {
                await DeleteReplacedAsync(old);
            }
        }

        /// <summary>Best effort: a replaced subscription is never renewed again, so one that can't be deleted still lapses.</summary>
        private async Task DeleteReplacedAsync(CallRecordSubscription old)
        {
            try
            {
                await _subscriptions.DeleteSubscription(old.Id);
                Telemetry.LogInformation($"{LOG_TAG} Deleted replaced subscription id '{old.Id}'.");
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == (int)HttpStatusCode.NotFound)
            {
                Telemetry.LogInformation($"{LOG_TAG} Replaced subscription id '{old.Id}' was already gone.");
            }
            catch (Exception ex)
            {
                var error = ex is ODataError graphError ? $"{StatusLine(graphError)} Graph error: '{graphError.Error?.Message ?? graphError.Message}'" : ex.Message;
                var expires = old.ExpirationDateTime.HasValue ? $" at {old.ExpirationDateTime.Value.UtcDateTime:u}" : string.Empty;
                Telemetry.LogWarning($"{LOG_TAG} Couldn't delete replaced subscription id '{old.Id}': {error} " +
                    $"It is no longer renewed, so Graph removes it when it expires{expires}; deleting it is retried next cycle. Until then " +
                    "a call may be notified twice, which is harmless - saving a call replaces any copy already saved.");
            }
        }

        private async Task RenewAsync(string subscriptionId, DateTime expiryUtc, Uri webAppUrl)
        {
            // https://docs.microsoft.com/en-us/graph/api/subscription-update?view=graph-rest-beta&tabs=http
            try
            {
                var result = await _subscriptions.RenewSubscription(subscriptionId, expiryUtc);
                Telemetry.LogInformation($"{LOG_TAG} Renewed subscription id '{result.Id}' for webhook at '{webAppUrl}'. New expiry: {result.ExpirationDateTime:u}.");
            }
            catch (ODataError ex)
            {
                LogSubscriptionFailure(ex, webAppUrl, isUpdate: true, existingSubId: subscriptionId);
                throw;
            }
        }

        /// <summary>
        /// A record that cannot be written is not fatal: <see cref="CallSubscriptionRecordStore"/> keeps this process's
        /// copy, so the worst case is one more replacement after a restart.
        /// </summary>
        private async Task RecordAsync(Uri webAppUrl, CallRecordSubscription created, CallWebhookClientState clientState)
        {
            try
            {
                await _records.SaveAsync(webAppUrl, new CallSubscriptionRecord
                {
                    SubscriptionId = created.Id,
                    ClientStateFingerprint = clientState.Fingerprint,
                    CreatedUtc = _clock.UtcNow
                });
            }
            catch (Exception ex)
            {
                Telemetry.LogWarning($"{LOG_TAG} Created subscription id '{created.Id}' but couldn't record it in the runtime state table: {ex.Message} " +
                    "This process remembers it; if the importer restarts before the record can be written, the subscription is replaced once more.");
            }
        }

        /// <summary>
        /// Read-only check of the current call-records webhook subscription, for status display.
        /// Does NOT create or renew anything. See <see cref="CallRecordSubscriptionStatus.ReadAsync"/>,
        /// which the web app calls directly.
        /// </summary>
        public Task<CallRecordSubscriptionInfo> GetCallRecordsSubscriptionInfo(Uri webAppUrl)
        {
            return CallRecordSubscriptionStatus.ReadAsync(_subscriptions, webAppUrl);
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
        /// <param name="replacingSubId">
        /// Set when the failed create was to replace this subscription, which is renewed meanwhile - so the impact is
        /// stated as it is rather than as "nothing is imported".
        /// </param>
        private void LogSubscriptionFailure(ODataError ex, Uri webAppUrl, bool isUpdate, string existingSubId = null, string replacingSubId = null)
        {
            var action = isUpdate
                ? $"renew subscription '{existingSubId}'"
                : replacingSubId == null ? "create subscription" : $"create a subscription to replace '{replacingSubId}'";
            var statusCode = ex.ResponseStatusCode;
            var statusLine = StatusLine(ex);
            var graphError = ex.Error?.Message ?? ex.Message;
            var impact = replacingSubId == null
                ? "Until this is fixed, NO Teams call records will be imported."
                : $"Subscription '{replacingSubId}' is renewed meanwhile and the replacement retried every cycle, but its notifications are " +
                  "only imported while the webhook accepts its clientState - not at all if the client secret has been rotated since it was created.";

            var validation = CallSubscriptionRules.ClassifyValidationCallbackFailure(graphError);

            if (validation.IsValidationCallbackFailure)
            {
                Telemetry.LogCritical(
                    $"{LOG_TAG} Couldn't {action} for call-records at '{webAppUrl}'. {statusLine} " +
                    BuildValidationCallbackDiagnosis(validation, webAppUrl) +
                    $" {impact} Graph error: '{graphError}'");
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
                    $"{impact} Graph error: '{graphError}'");
            }
            else
            {
                Telemetry.LogCritical(
                    $"{LOG_TAG} Couldn't {action} for call-records at '{webAppUrl}'. {statusLine} " +
                    $"{impact} Graph error: '{graphError}'");
            }
        }

        private static string StatusLine(ODataError ex)
        {
            var statusCode = ex.ResponseStatusCode;
            return statusCode > 0
                ? (Enum.IsDefined(typeof(HttpStatusCode), statusCode)
                    ? $"Graph returned {statusCode} {(HttpStatusCode)statusCode}."
                    : $"Graph returned {statusCode}.")
                : "Graph call failed before a status code was returned.";
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
