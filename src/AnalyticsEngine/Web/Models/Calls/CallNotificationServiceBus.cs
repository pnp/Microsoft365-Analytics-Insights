using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Common.Entities.Calls;
using Common.Entities.Config;
using System;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.Calls
{
    /// <summary>
    /// The web app's end of the Teams calls queue. The webhook only ever sends the notifications Graph
    /// POSTs to it; the importer's <c>CallQueueProcessor</c> receives them.
    /// </summary>
    public static class CallNotificationServiceBus
    {
        /// <summary>
        /// Builds a <see cref="ServiceBusClient"/> that authenticates with Entra ID RBAC (the runtime
        /// service principal via <see cref="ClientSecretCredential"/>) - never a SAS key. The fully
        /// qualified namespace is read from the configured Service Bus connection string's Endpoint; the
        /// shared access key in that string is ignored. See issue #138. The importer builds its receiving
        /// client the same way in <c>CallQueueProcessor.CreateRbacServiceBusClient</c>; keep the two in step.
        /// </summary>
        public static ServiceBusClient CreateRbacClient(AppConfig config)
        {
            var sbProps = ServiceBusConnectionStringProperties.Parse(config.ConnectionStrings.ServiceBusConnectionString);
            var credential = new ClientSecretCredential(config.TenantGUID.ToString(), config.ClientID, config.ClientSecret);
            return new ServiceBusClient(sbProps.FullyQualifiedNamespace, credential);
        }
    }

    /// <summary>
    /// Service Bus implementation of <see cref="ICallNotificationQueueSender"/>. Does not own the
    /// sender - the caller creates and disposes it.
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
}
