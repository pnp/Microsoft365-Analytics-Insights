using Azure.Identity;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
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
    public class CallWebhook
    {
        public GraphServiceClient Client { get; set; }
        public ILogger Telemetry { get; set; }

        public CallWebhook(AppConfig o365DownloadSettings, ILogger logger)
            : this(o365DownloadSettings?.TenantGUID.ToString(), o365DownloadSettings?.ClientID, o365DownloadSettings?.ClientSecret, logger) { }

        public CallWebhook(string tenantId, string clientId, string secret, ILogger logger)
        {
            var cred = new ClientSecretCredential(tenantId, clientId, secret);

            this.Telemetry = logger ?? throw new ArgumentNullException(nameof(logger));
            this.Client = new GraphServiceClient(cred);
        }

        // Graph Application permission required to subscribe to /communications/callRecords. Surfaced in error
        // messages so operators see the exact permission to grant (matches docs/wiki - Prerequisites.md).
        internal const string REQUIRED_GRAPH_PERMISSION = "CallRecords.Read.All";

        // Grep-friendly tag so the calls-webhook lifecycle is easy to filter in App Insights traces.
        private const string LOG_TAG = "[Calls Webhook]";

        // The Graph resource path we subscribe to for Teams call records.
        private const string CALL_RECORDS_RESOURCE = "/communications/callRecords";

        public async Task CreateOrUpdateWebhook(Uri webAppUrl, string secret)
        {
            // https://docs.microsoft.com/en-us/graph/api/resources/webhooks?view=graph-rest-1.0
            var matchingSubs = await FindCallRecordsSubscriptions(webAppUrl);

            if (matchingSubs.Count == 0)
            {
                Telemetry.LogInformation($"{LOG_TAG} No subscription found for call-records, for URL '{webAppUrl}'. Creating...");
                try
                {
                    var result = await this.Client.Subscriptions.PostAsync(new Subscription()
                    {
                        NotificationUrl = webAppUrl.ToString(),
                        Resource = CALL_RECORDS_RESOURCE,
                        ClientState = secret,
                        ChangeType = "created",
                        ExpirationDateTime = DateTime.UtcNow.AddDays(2)        // the max Graph will permit - https://docs.microsoft.com/en-us/graph/api/resources/subscription?view=graph-rest-beta#properties
                    });
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
                var existingSubId = matchingSubs.First().Id;
                try
                {
                    var result = await this.Client.Subscriptions[existingSubId].PatchAsync(
                        new Subscription
                        {
                            ExpirationDateTime = DateTime.UtcNow.AddDays(2)
                        }
                    );
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
        /// Walk every subscriptions page (the default Graph page size may not include the
        /// call-records subscription when the tenant has many subscriptions) and return those
        /// matching the call-records resource for this web-app's notification URL.
        /// </summary>
        private async Task<List<Subscription>> FindCallRecordsSubscriptions(Uri webAppUrl)
        {
            var matchingSubs = new List<Subscription>();
            var firstPage = await this.Client.Subscriptions.GetAsync();
            if (firstPage != null)
            {
                var iterator = PageIterator<Subscription, SubscriptionCollectionResponse>.CreatePageIterator(
                    this.Client,
                    firstPage,
                    sub =>
                    {
                        if (sub.Resource == CALL_RECORDS_RESOURCE && sub.NotificationUrl == webAppUrl.ToString())
                        {
                            matchingSubs.Add(sub);
                        }

                        return true;
                    });

                await iterator.IterateAsync();
            }

            return matchingSubs;
        }

        /// <summary>
        /// Read-only check of the current call-records webhook subscription, for status display
        /// (e.g. the web homepage). Does NOT create or renew anything. Returns whether a matching
        /// subscription currently exists and, if so, when it expires. Any Graph error is allowed to
        /// propagate so the caller can surface it as an explicit "couldn't check" state.
        /// </summary>
        public async Task<CallRecordSubscriptionInfo> GetCallRecordsSubscriptionInfo(Uri webAppUrl)
        {
            var matchingSubs = await FindCallRecordsSubscriptions(webAppUrl);

            // If more than one matches (shouldn't normally happen), report the one that expires
            // latest - that's the subscription keeping the webhook alive.
            var current = matchingSubs
                .OrderByDescending(s => s.ExpirationDateTime ?? DateTimeOffset.MinValue)
                .FirstOrDefault();

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
