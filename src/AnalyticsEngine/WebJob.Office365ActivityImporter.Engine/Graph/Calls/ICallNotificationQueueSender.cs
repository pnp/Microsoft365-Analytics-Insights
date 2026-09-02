using Azure.Messaging.ServiceBus;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Write port for the queue the calls webhook drops Graph change notifications onto. Extracted so
    /// the dispatch logic in <see cref="CallQueueProcessor.AddChangeMsgToQueue(List{Common.Entities.Models.GraphChangeNotification}, Microsoft.Extensions.Logging.ILogger, ICallNotificationQueueSender)"/>
    /// can be tested without a Service Bus namespace. See issue #378.
    /// </summary>
    public interface ICallNotificationQueueSender
    {
        Task SendAsync(string messageBody);
    }

    /// <summary>
    /// Service Bus implementation of <see cref="ICallNotificationQueueSender"/>. Does not own the
    /// sender - the caller creates and disposes it, exactly as before.
    /// </summary>
    public class ServiceBusCallNotificationQueueSender : ICallNotificationQueueSender
    {
        private readonly ServiceBusSender _sender;

        public ServiceBusCallNotificationQueueSender(ServiceBusSender sender)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        }

        public Task SendAsync(string messageBody)
        {
            return _sender.SendMessageAsync(new ServiceBusMessage(messageBody));
        }
    }

    /// <summary>
    /// In-memory <see cref="ICallNotificationQueueSender"/> for tests: keeps every message body it was
    /// asked to send, in order.
    /// </summary>
    public class InMemoryCallNotificationQueueSender : ICallNotificationQueueSender
    {
        public List<string> SentMessages { get; } = new List<string>();

        public Task SendAsync(string messageBody)
        {
            SentMessages.Add(messageBody);
            return Task.CompletedTask;
        }
    }
}
