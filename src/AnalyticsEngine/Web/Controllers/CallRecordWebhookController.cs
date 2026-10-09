using Azure.Messaging.ServiceBus;
using Common.Entities.Calls;
using Common.Entities.Config;
using Common.Entities.Models;
using DataUtils;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models.Calls;

namespace Web.AnalyticsWeb.Controllers
{
    [Route("api/CallRecordWebhook")]
    public class CallRecordWebhookController  : ControllerBase
    {
        // Webhook called by Graph for new calls
        // POST: api/CallRecordWebhook
        [HttpPost]
        public async Task<IActionResult> Post(
            [FromBody] GraphChangeNotificationList changeMsg,
            [FromQuery] string validationToken = "")
        {
            var config = new AppConfig();
            var logger = new AnalyticsLogger(config.AppInsightsConnectionString, nameof(CallRecordWebhookController));

            // Test ping from Graph?
            if (!string.IsNullOrEmpty(validationToken))
            {
                logger.LogInformation($"{nameof(CallRecordWebhookController)}: test ping from Graph received.");
                return Content(validationToken, "text/plain", Encoding.UTF8);
            }

            // Do we have a correctly deserialised body?
            if (changeMsg != null)
            {
                var changes = new List<GraphChangeNotification>();
                int invalidChangeMsgs = 0;

                // Verify each msg is legit - compare ClientState to app secret
                var selection = CallNotificationRules.SelectValidNotifications(changeMsg.Notifications, config.ClientSecret);
                changes = selection.Valid;
                invalidChangeMsgs = selection.InvalidCount;

                logger.LogInformation($"{nameof(CallRecordWebhookController)} invoked. {changes.Count} valid changes; {invalidChangeMsgs} invalid changes.");

                // Service Bus is required to dispatch call notifications. If it's not configured, the calls feature is disabled.
                if (string.IsNullOrWhiteSpace(config.ConnectionStrings.ServiceBusConnectionString))
                {
                    logger.LogError($"{nameof(CallRecordWebhookController)}: Service Bus is not configured. Teams call notifications cannot be processed. Enable Service Bus in the installer to use the Teams calls import.");
                    return new ContentResult
                    {
                        StatusCode = StatusCodes.Status503ServiceUnavailable,
                        ContentType = "text/plain; charset=utf-8",
                        Content = "Service Bus is not configured on this deployment; Teams call notifications are disabled."
                    };
                }

                // Create new SB client. Wrap in try/finally so client + sender are disposed
                // even if AddChangeMsgToQueue throws - otherwise sockets leak per webhook call.
                // Authenticate with Entra ID RBAC (runtime service principal), never a SAS key. See issue #138.
                var sbClient = CallNotificationServiceBus.CreateRbacClient(config);
                var sbConnectionProps = ServiceBusConnectionStringProperties.Parse(config.ConnectionStrings.ServiceBusConnectionString);
                var sbSender = sbClient.CreateSender(sbConnectionProps.EntityPath);

                try
                {
                    try
                    {
                        await CallNotificationDispatcher.AddChangeMsgToQueue(changes, logger, new ServiceBusCallNotificationQueueSender(sbSender));
                    }
                    catch (ServiceBusException ex)
                    {
                        logger.TrackException(ex);
                        logger.LogError($"Error adding change messages to queue: {ex.Message}");
                        return StatusCode(StatusCodes.Status500InternalServerError);
                    }
                }
                finally
                {
                    await sbSender.DisposeAsync();
                    await sbClient.DisposeAsync();
                }

                return Content("not null and that", "text/plain", Encoding.UTF8);
            }
            else
            {
                logger.LogInformation($"{nameof(CallRecordWebhookController)} invoked with invalid body.");
                return new ContentResult
                {
                    StatusCode = StatusCodes.Status400BadRequest,
                    ContentType = "text/plain; charset=utf-8",
                    Content = $"Could not find {nameof(GraphChangeNotificationList)} in body"
                };
            }
        }
    }
}