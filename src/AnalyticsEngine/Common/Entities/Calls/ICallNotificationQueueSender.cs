using System.Collections.Generic;
using System.Threading.Tasks;

namespace Common.Entities.Calls
{
    /// <summary>
    /// Write port for the queue the calls webhook drops Graph change notifications onto. Extracted so
    /// the dispatch logic in <see cref="CallNotificationDispatcher.AddChangeMsgToQueue"/> can be tested
    /// without a Service Bus namespace. See issue #378. The Service Bus implementation lives in the web
    /// app, the only producer, so this project does not need the Service Bus SDK.
    /// </summary>
    public interface ICallNotificationQueueSender
    {
        Task SendAsync(string messageBody);
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
