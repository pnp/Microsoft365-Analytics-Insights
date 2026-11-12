using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.Calls
{
    /// <summary>
    /// How many messages the Teams calls queue holds, and how many it has dead-lettered: one Service Bus management call,
    /// shared by the Health page's Service Bus check and the Service configuration page's dead-lettered calls card.
    /// </summary>
    internal sealed class CallQueueDepth
    {
        /// <summary>The queue the <c>ServiceBus</c> connection string names (its <c>EntityPath</c>).</summary>
        public string QueueName { get; set; }

        public long ActiveMessageCount { get; set; }

        public long DeadLetterMessageCount { get; set; }

        /// <summary>
        /// Reads the queue's runtime properties with the runtime identity (Entra ID; the connection string's key is ignored),
        /// which the installer gives Azure Service Bus Data Owner on the queue.
        /// </summary>
        public static async Task<CallQueueDepth> ReadAsync(string serviceBusConnectionString, TokenCredential credential, CancellationToken cancellationToken)
        {
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            var sbProps = ServiceBusConnectionStringProperties.Parse(serviceBusConnectionString);
            var admin = new ServiceBusAdministrationClient(sbProps.FullyQualifiedNamespace, credential, ClientOptions());
            var runtime = (await admin.GetQueueRuntimePropertiesAsync(sbProps.EntityPath, cancellationToken).ConfigureAwait(false)).Value;
            return new CallQueueDepth
            {
                QueueName = sbProps.EntityPath,
                ActiveMessageCount = runtime.ActiveMessageCount,
                DeadLetterMessageCount = runtime.DeadLetterMessageCount
            };
        }

        /// <summary>
        /// Service Bus's default retry policy (three retries of up to 100 seconds each) could outlast the caller's deadline
        /// on its own. One retry of a short request leaves room for the rest of the page.
        /// </summary>
        internal static ServiceBusAdministrationClientOptions ClientOptions()
        {
            var options = new ServiceBusAdministrationClientOptions();
            options.Retry.MaxRetries = 1;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(15);
            return options;
        }
    }
}
