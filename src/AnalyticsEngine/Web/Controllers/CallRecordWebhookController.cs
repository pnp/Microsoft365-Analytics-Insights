using Azure.Messaging.ServiceBus;
using Common.Entities.Calls;
using Common.Entities.Config;
using Common.Entities.Models;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.Calls;

namespace Web.AnalyticsWeb.Controllers
{
    public class CallRecordWebhookController : ApiController
    {
        // The endpoint is anonymous - Graph has to be able to POST to it - so nothing costly is built per request.
        // Configuration, the logger and the Service Bus sender are created once per process, and the sender only once
        // a POST has carried a notification whose clientState matches: a stranger's request never gets that far.
        private static readonly Lazy<AppConfig> Config = new Lazy<AppConfig>(() => new AppConfig());
        private static readonly Lazy<AnalyticsLogger> Logger = new Lazy<AnalyticsLogger>(
            () => new AnalyticsLogger(Config.Value.AppInsightsConnectionString, nameof(CallRecordWebhookController)));

        // The clientState derived from the app's client secret (and the legacy raw secret, while subscriptions created
        // before that change are replaced). One per process, so its warnings are throttled per process. After a client
        // secret rotation it also accepts - for a bounded window, and only from the subscription the importer recorded -
        // the previous secret's clientState, recognised by the fingerprint the importer stored in the runtime state table.
        private static readonly Lazy<CallNotificationAuthenticator> Authenticator = new Lazy<CallNotificationAuthenticator>(
            () => new CallNotificationAuthenticator(CallWebhookClientState.ForConfig(Config.Value),
                CachedCallSubscriptionRecordSource.ForConfig(Config.Value, Logger.Value)));

        private static readonly object QueueLock = new object();
        private static ServiceBusClient _queueClient;
        private static ICallNotificationQueueSender _queue;

        // Webhook called by Graph for new calls
        // POST: api/CallRecordWebhook - or api/CallRecordWebhook?cs=v1, the URL subscriptions with the derived clientState
        // use (CallSubscriptionRules.CurrentNotificationUrl). The extra parameter needs no binding: Graph appends its
        // validation token as "&validationToken=", which binds below either way.
        [HttpPost]
        public async Task<HttpResponseMessage> Post([FromBody] GraphChangeNotificationList changeMsg, string validationToken = "")
        {
            // Test ping from Graph, or the importer warming the webhook up before it asks Graph for a subscription?
            if (!string.IsNullOrEmpty(validationToken))
            {
                Logger.Value.LogInformation($"{nameof(CallRecordWebhookController)}: validation request received (from Graph, or the importer's warm-up).");
                var pingTestResponse = new HttpResponseMessage(HttpStatusCode.OK);
                pingTestResponse.Content = new StringContent(validationToken, System.Text.Encoding.UTF8, "text/plain");
                return pingTestResponse;
            }

            // Do we have a correctly deserialised body? "{}" deserialises to a list with no notifications, which is
            // just as malformed as no body at all - and used to be a NullReferenceException (a 500) for anyone.
            if (changeMsg?.Notifications == null)
            {
                Logger.Value.LogInformation($"{nameof(CallRecordWebhookController)} invoked with invalid body.");
                var errResponse = new HttpResponseMessage(HttpStatusCode.BadRequest);
                errResponse.Content = new StringContent($"Could not find {nameof(GraphChangeNotificationList)} in body",
                    System.Text.Encoding.UTF8, "text/plain");
                return errResponse;
            }

            var config = Config.Value;
            var logger = Logger.Value;

            // Verify each msg is legit - compare its ClientState to the one derived from the app secret. A null entry
            // cannot be, and is counted as invalid.
            var notifications = changeMsg.Notifications.Where(n => n != null).ToList();
            var selection = await Authenticator.Value.SelectAsync(notifications, logger);
            var changes = selection.Valid;
            var invalidChangeMsgs = selection.InvalidCount + (changeMsg.Notifications.Count - notifications.Count);

            logger.LogInformation($"{nameof(CallRecordWebhookController)} invoked. {changes.Count} valid changes; {invalidChangeMsgs} invalid changes.");

            if (changes.Count > 0)
            {
                // Service Bus is required to dispatch call notifications. If it's not configured, the calls feature is disabled.
                if (string.IsNullOrWhiteSpace(config.ConnectionStrings.ServiceBusConnectionString))
                {
                    logger.LogError($"{nameof(CallRecordWebhookController)}: Service Bus is not configured. Teams call notifications cannot be processed. Enable Service Bus in the installer to use the Teams calls import.");
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("Service Bus is not configured on this deployment; Teams call notifications are disabled.", System.Text.Encoding.UTF8, "text/plain")
                    };
                }

                try
                {
                    await CallNotificationDispatcher.AddChangeMsgToQueue(changes, logger, SharedQueue(config));
                }
                catch (ServiceBusException ex)
                {
                    logger.TrackException(ex);
                    logger.LogError($"Error adding change messages to queue: {ex.Message}");
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }
            }

            var successResponse = new HttpResponseMessage(HttpStatusCode.OK);
            successResponse.Content = new StringContent("not null and that", System.Text.Encoding.UTF8, "text/plain");
            return successResponse;
        }

        /// <summary>
        /// The process's one Service Bus sender for the calls queue.
        /// </summary>
        /// <remarks>
        /// A <see cref="ServiceBusClient"/> and its senders are built to live as long as the application and are safe
        /// to share. A client per POST opened and tore down an AMQP connection, and acquired a token, for every
        /// notification Graph sent. Authenticates with Entra ID RBAC (runtime service principal), never a SAS key - see
        /// issue #138. A failure to build it is not kept, so the next notification tries again.
        /// </remarks>
        private static ICallNotificationQueueSender SharedQueue(AppConfig config)
        {
            lock (QueueLock)
            {
                if (_queue == null || _queueClient == null)
                {
                    var client = CallNotificationServiceBus.CreateRbacClient(config);
                    var entityPath = ServiceBusConnectionStringProperties.Parse(config.ConnectionStrings.ServiceBusConnectionString).EntityPath;
                    _queue = new ServiceBusCallNotificationQueueSender(client.CreateSender(entityPath));
                    _queueClient = client;
                }

                return _queue;
            }
        }
    }
}
