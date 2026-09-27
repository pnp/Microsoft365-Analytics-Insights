using Common.Entities.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Common.Entities.Calls
{
    /// <summary>
    /// Puts the Graph change notifications the calls webhook accepted onto the calls queue, one message
    /// per notification. The body is the serialised <see cref="GraphChangeNotification"/>, which is
    /// exactly what the importer's <c>CallQueueProcessor</c> deserialises on the other side - so the
    /// producer (web app) and consumer (importer) share this rather than each writing their own.
    /// </summary>
    public static class CallNotificationDispatcher
    {
        /// <summary>
        /// Queue each notification for processing. Takes the queue as a port so the dispatch can be
        /// tested without Service Bus. See issue #378.
        /// </summary>
        public static async Task AddChangeMsgToQueue(List<GraphChangeNotification> changes, ILogger logger, ICallNotificationQueueSender queue)
        {
            foreach (var change in changes)
            {
                string callId = change.ResourceData.Id;

                if (!string.IsNullOrEmpty(callId))
                {
                    logger.LogInformation($"New call POSTed from Graph with ID '{callId}'");
                }
                else
                {
                    logger.LogInformation($"New call POSTed from Graph with unknown ID. Adding to service-bus queue anyway.");
                }

                var json = JsonConvert.SerializeObject(change);
                await queue.SendAsync(json);
            }
        }
    }
}
