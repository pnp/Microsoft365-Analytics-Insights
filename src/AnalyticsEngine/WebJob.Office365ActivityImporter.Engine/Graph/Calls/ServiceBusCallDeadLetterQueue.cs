using Azure.Messaging.ServiceBus;
using Common.Entities.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Service Bus implementation of <see cref="ICallDeadLetterQueue"/> for the Teams calls queue: a PeekLock receiver on
    /// the queue's dead-letter sub-queue, and a sender to the queue itself.
    /// </summary>
    public sealed class ServiceBusCallDeadLetterQueue : ICallDeadLetterQueue
    {
        /// <summary>How long a receive waits for the first message before deciding the dead-letter queue has none left.</summary>
        internal static readonly TimeSpan ReceiveWaitTime = TimeSpan.FromSeconds(5);

        private readonly ServiceBusClient _ownedClient;
        private readonly ServiceBusReceiver _receiver;
        private readonly ServiceBusSender _sender;

        /// <param name="ownsClient">Whether disposing this also disposes <paramref name="client"/>.</param>
        public ServiceBusCallDeadLetterQueue(ServiceBusClient client, string queueName, bool ownsClient = false)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(queueName)) throw new ArgumentException("No queue name.", nameof(queueName));

            QueueName = queueName;
            _receiver = client.CreateReceiver(queueName, CreateDeadLetterReceiverOptions());
            _sender = client.CreateSender(queueName);
            _ownedClient = ownsClient ? client : null;
        }

        /// <summary>
        /// Opens the calls queue the importer reads, from the same configuration and with the same Entra ID (RBAC) client
        /// as <see cref="CallQueueProcessor"/>: the <c>ServiceBus</c> connection string names the namespace and queue.
        /// </summary>
        public static ServiceBusCallDeadLetterQueue Open(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var connectionString = config.ConnectionStrings?.ServiceBusConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Service Bus is not configured on this deployment (no 'ServiceBus' connection string), so there is no Teams calls queue.");
            }
            var queueName = ServiceBusConnectionStringProperties.Parse(connectionString).EntityPath;
            if (string.IsNullOrWhiteSpace(queueName))
            {
                throw new InvalidOperationException("The 'ServiceBus' connection string names no queue (EntityPath).");
            }
            return new ServiceBusCallDeadLetterQueue(CallQueueProcessor.CreateRbacServiceBusClient(config), queueName, ownsClient: true);
        }

        /// <summary>The dead-letter sub-queue, PeekLock, nothing prefetched: every message received is one being worked on.</summary>
        public static ServiceBusReceiverOptions CreateDeadLetterReceiverOptions()
        {
            return new ServiceBusReceiverOptions
            {
                SubQueue = SubQueue.DeadLetter,
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
                PrefetchCount = 0
            };
        }

        public string QueueName { get; }

        /// <summary>The entity messages are read from.</summary>
        public string ReceiveEntityPath => _receiver.EntityPath;

        /// <summary>The entity replays are sent to.</summary>
        public string SendEntityPath => _sender.EntityPath;

        public ServiceBusReceiveMode ReceiveMode => _receiver.ReceiveMode;

        public async Task<IReadOnlyList<CallDeadLetterMessage>> PeekAsync(long fromSequenceNumber, int maxMessages, CancellationToken cancellationToken)
        {
            var messages = await _receiver.PeekMessagesAsync(maxMessages, fromSequenceNumber, cancellationToken);
            return messages.Select(FromReceived).ToList();
        }

        public async Task<IReadOnlyList<CallDeadLetterMessage>> ReceiveAsync(int maxMessages, CancellationToken cancellationToken)
        {
            var messages = await _receiver.ReceiveMessagesAsync(maxMessages, ReceiveWaitTime, cancellationToken);
            return messages.Select(FromReceived).ToList();
        }

        public Task CompleteAsync(CallDeadLetterMessage message, CancellationToken cancellationToken)
        {
            return _receiver.CompleteMessageAsync(Received(message), cancellationToken);
        }

        public Task AbandonAsync(CallDeadLetterMessage message, CancellationToken cancellationToken)
        {
            return _receiver.AbandonMessageAsync(Received(message), null, cancellationToken);
        }

        public async Task<bool> HasMessagesAfterAsync(long sequenceNumber, CancellationToken cancellationToken)
        {
            var messages = await _receiver.PeekMessagesAsync(1, sequenceNumber + 1, cancellationToken);
            return messages.Count > 0;
        }

        public Task SendAsync(CallReplayMessage message, CancellationToken cancellationToken)
        {
            return _sender.SendMessageAsync(ToServiceBusMessage(message), cancellationToken);
        }

        public static CallDeadLetterMessage FromReceived(ServiceBusReceivedMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            return new CallDeadLetterMessage
            {
                MessageId = message.MessageId,
                SequenceNumber = message.SequenceNumber,
                EnqueuedTime = message.EnqueuedTime,
                DeadLetterReason = message.DeadLetterReason,
                Body = message.Body,
                ContentType = message.ContentType,
                CorrelationId = message.CorrelationId,
                Subject = message.Subject,
                ApplicationProperties = message.ApplicationProperties,
                LockToken = message.LockToken,
                LockedUntil = message.LockedUntil,
                Received = message
            };
        }

        public static ServiceBusMessage ToServiceBusMessage(CallReplayMessage replay)
        {
            if (replay == null) throw new ArgumentNullException(nameof(replay));

            var message = new ServiceBusMessage(replay.Body ?? new BinaryData(Array.Empty<byte>()))
            {
                MessageId = replay.MessageId
            };
            if (replay.ContentType != null) message.ContentType = replay.ContentType;
            if (replay.CorrelationId != null) message.CorrelationId = replay.CorrelationId;
            if (replay.Subject != null) message.Subject = replay.Subject;
            foreach (var property in replay.ApplicationProperties)
            {
                message.ApplicationProperties[property.Key] = property.Value;
            }
            return message;
        }

        public async ValueTask DisposeAsync()
        {
            await _receiver.DisposeAsync().ConfigureAwait(false);
            await _sender.DisposeAsync().ConfigureAwait(false);
            if (_ownedClient != null)
            {
                await _ownedClient.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static ServiceBusReceivedMessage Received(CallDeadLetterMessage message)
        {
            if (message?.Received == null)
            {
                throw new InvalidOperationException("Only a message this queue received can be settled.");
            }
            return message.Received;
        }
    }
}
