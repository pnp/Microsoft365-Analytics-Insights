using Azure.Messaging.ServiceBus;
using Common.Entities.Config;
using Common.Entities.Models;
using DataUtils;
using Microsoft.Graph;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using WebJob.Office365ActivityImporter.Engine.Graph.Calls;

namespace Web.AnalyticsWeb.Controllers
{
    public class CallRecordWebhookController : ApiController
    {
        // Webhook called by Graph for new calls
        // POST: api/CallRecordWebhook
        [HttpPost]
        public async Task<HttpResponseMessage> Post([FromBody] GraphChangeNotificationList changeMsg, string validationToken = "")
        {
            var config = new AppConfig();
            var telemetry = new AnalyticsLogger(config.AppInsightsConnectionString, nameof(CallRecordWebhookController));

            // Test ping from Graph?
            if (!string.IsNullOrEmpty(validationToken))
            {
                telemetry.LogInformation($"{nameof(CallRecordWebhookController)}: test ping from Graph received.");
                var pingTestResponse = new HttpResponseMessage(HttpStatusCode.OK);
                pingTestResponse.Content = new StringContent(validationToken, System.Text.Encoding.UTF8, "text/plain");
                return pingTestResponse;
            }

            // Do we have a correctly deserialised body?
            if (changeMsg != null)
            {
                var changes = new List<GraphChangeNotification>();
                int invalidChangeMsgs = 0;

                // Verify each msg is legit - compare ClientState to app secret
                foreach (var change in changeMsg.Notifications)
                {
                    if (change.ClientState == config.ClientSecret)
                        changes.Add(change);
                    else invalidChangeMsgs++;

                }
                telemetry.LogInformation($"{nameof(CallRecordWebhookController)} invoked. {changes.Count} valid changes; {invalidChangeMsgs} invalid changes.");

                // Create new SB client
                var sbClient = new ServiceBusClient(config.ConnectionStrings.ServiceBusConnectionString);
                var sbConnectionProps = ServiceBusConnectionStringProperties.Parse(config.ConnectionStrings.ServiceBusConnectionString);
                var sbSender = sbClient.CreateSender(sbConnectionProps.EntityPath);

                try
                {
                    await CallQueueProcessor.AddChangeMsgToQueue(changes, telemetry, sbSender);
                }
                catch (ServiceBusException ex)
                {
                    telemetry.TrackException(ex);
                    telemetry.LogError($"Error adding change messages to queue: {ex.Message}");
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                var successResponse = new HttpResponseMessage(HttpStatusCode.OK);
                successResponse.Content = new StringContent("not null and that", System.Text.Encoding.UTF8, "text/plain");
                return successResponse;
            }
            else
            {
                telemetry.LogInformation($"{nameof(CallRecordWebhookController)} invoked with invalid body.");
                var errResponse = new HttpResponseMessage(HttpStatusCode.BadRequest);
                errResponse.Content = new StringContent($"Could not find {nameof(ChangeNotificationCollection)} in body",
                    System.Text.Encoding.UTF8, "text/plain");
                return errResponse;
            }
        }
    }
}
