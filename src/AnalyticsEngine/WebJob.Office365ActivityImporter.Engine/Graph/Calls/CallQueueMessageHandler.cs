using Azure.Messaging.ServiceBus;
using Common.Entities.Models;
using DataUtils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>A call notification as received from the calls queue.</summary>
    public sealed class CallQueueMessage
    {
        public string MessageId { get; set; }
        public string Body { get; set; }
        public IReadOnlyDictionary<string, object> ApplicationProperties { get; set; } = new Dictionary<string, object>();

        public static CallQueueMessage From(ServiceBusReceivedMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            return new CallQueueMessage
            {
                MessageId = message.MessageId,
                Body = message.Body?.ToString(),
                ApplicationProperties = message.ApplicationProperties
            };
        }
    }

    /// <summary>
    /// Settles one received call notification. A port so <see cref="CallQueueMessageHandler"/> can be tested without
    /// Service Bus.
    /// </summary>
    public interface ICallQueueMessageSettlement
    {
        Task CompleteAsync();
        Task AbandonAsync();
        Task DeadLetterAsync(string reason, string description);

        /// <summary>
        /// Queues a copy of the message, carrying <paramref name="state"/>, to be delivered at
        /// <paramref name="enqueueAtUtc"/>. Does not settle the original.
        /// </summary>
        Task ScheduleRetryAsync(CallQueueRetryState state, DateTime enqueueAtUtc);
    }

    public enum CallQueueMessageOutcome
    {
        Completed,
        RetryScheduled,
        Abandoned,
        DeadLettered
    }

    /// <summary>
    /// Imports the Teams call a queued Graph notification refers to, and settles the message: completed once the call
    /// is imported, re-queued with a back-off when the import fails (see <see cref="CallQueueRetryPolicy"/>), and
    /// dead-lettered with a reason when it can never succeed or has run out of retries. Failures that look like an
    /// outage, from more than one call, also pause the queue (see <see cref="CallImportCircuitBreaker"/>).
    /// </summary>
    public sealed class CallQueueMessageHandler
    {
        public const string InvalidNotificationReason = "InvalidCallNotification";
        public const string MissingCallIdReason = "MissingCallId";
        public const string NotRetryableReason = "CallImportNotRetryable";

        private readonly Func<GraphChangeNotification, Task<CallImportOutcome>> _importCall;
        private readonly ILogger _logger;
        private readonly IClock _clock;
        private readonly CallQueueRetryPolicy _policy;
        private readonly CallImportCircuitBreaker _breaker;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;

        /// <param name="importCall">
        /// Imports the notification's call. <see cref="CallImportOutcome.NotImported"/> or an exception means it was not
        /// imported; only <see cref="CallImportOutcome.Saved"/> shows that saving works again after a pause.
        /// </param>
        /// <param name="delay">How to wait while the queue is paused; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> by default.</param>
        public CallQueueMessageHandler(Func<GraphChangeNotification, Task<CallImportOutcome>> importCall, ILogger logger, IClock clock = null,
            CallQueueRetryPolicy policy = null, CallImportCircuitBreaker breaker = null, Func<TimeSpan, CancellationToken, Task> delay = null)
        {
            _importCall = importCall ?? throw new ArgumentNullException(nameof(importCall));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? SystemClock.Instance;
            _policy = policy ?? CallQueueRetryPolicy.Default;
            _breaker = breaker ?? new CallImportCircuitBreaker();
            _delay = delay ?? ((wait, token) => Task.Delay(wait, token));
        }

        public async Task<CallQueueMessageOutcome> HandleAsync(CallQueueMessage message, ICallQueueMessageSettlement settlement, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (settlement == null) throw new ArgumentNullException(nameof(settlement));

            _logger.LogInformation($"New message received from ServiceBus with ID '{message.MessageId}'.");

            // The body is never logged: it carries the webhook subscription's clientState.
            GraphChangeNotification change = null;
            try
            {
                change = JsonConvert.DeserializeObject<GraphChangeNotification>(message.Body ?? string.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deserialising the body of ServiceBus message '{message.MessageId}' ({message.Body?.Length ?? 0} characters). Exception: {ex.Message}");
            }
            if (change == null)
            {
                return await DeadLetterAsync(message, settlement, InvalidNotificationReason,
                    "The message body is not a Graph change notification, so it can never be imported.");
            }

            var callId = change.ResourceData?.Id;
            if (string.IsNullOrEmpty(callId))
            {
                return await DeadLetterAsync(message, settlement, MissingCallIdReason,
                    "The Graph change notification has no call ID, so there is no call record to import.");
            }

            bool isProbe;
            try
            {
                isProbe = await WaitUntilImportsAllowedAsync(message, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The processor is stopping: hand the message back for the next start.
                await TryAbandonAsync(message, settlement);
                return CallQueueMessageOutcome.Abandoned;
            }

            var outcomeRecorded = false;
            try
            {
                Exception failure = null;
                var outcome = CallImportOutcome.NotImported;
                try
                {
                    outcome = await _importCall(change);
                }
                catch (Exception ex)
                {
                    failure = ex;
                    _logger.LogError(ex, $"ServiceBus processing error: processing the notification for call '{callId}' (message ID '{message.MessageId}'). Exception: {ex.Message}");
                }

                if (failure == null && outcome == CallImportOutcome.Saved)
                {
                    outcomeRecorded = true;
                    if (_breaker.RecordSaved())
                    {
                        _logger.LogInformation("Teams calls import resumed: a call was saved after the pause.");
                    }
                    return await CompleteAsync(message, settlement);
                }

                if (failure == null && outcome == CallImportOutcome.ProcessedWithoutSaving)
                {
                    // Never reached SQL, so it can't end a pause - but it needs nothing more doing either.
                    outcomeRecorded = true;
                    _breaker.RecordProcessedWithoutSaving(isProbe);
                    return await CompleteAsync(message, settlement);
                }

                var kind = failure == null ? CallImportFailureKind.Transient : CallImportFailureClassifier.Classify(failure);
                var error = failure == null ? "Graph returned no readable call record." : CallImportFailureClassifier.Describe(failure);

                outcomeRecorded = true;
                LogPause(_breaker.RecordFailure(kind, callId, isProbe, _clock.UtcNow), callId, error);

                if (kind == CallImportFailureKind.Permanent)
                {
                    return await DeadLetterAsync(message, settlement, NotRetryableReason, $"Call '{callId}': {error}");
                }

                var decision = _policy.Decide(CallQueueRetryState.FromMessage(message), kind, _clock.UtcNow, error);
                if (decision.DeadLetter)
                {
                    return await DeadLetterAsync(message, settlement, decision.DeadLetterReason, $"Call '{callId}': {decision.DeadLetterDescription}");
                }

                return await ScheduleRetryAsync(message, settlement, callId, kind, decision);
            }
            finally
            {
                if (isProbe && !outcomeRecorded)
                {
                    _breaker.RecordFailure(CallImportFailureKind.Transient, callId, wasProbe: true, _clock.UtcNow);
                }
            }
        }

        private async Task<bool> WaitUntilImportsAllowedAsync(CallQueueMessage message, CancellationToken cancellationToken)
        {
            var announced = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var wait = _breaker.TryEnter(_clock.UtcNow, out var isProbe);
                if (wait <= TimeSpan.Zero)
                {
                    if (isProbe)
                    {
                        _logger.LogInformation($"Teams calls import paused: trying message '{message.MessageId}' to see whether calls import again.");
                    }
                    return isProbe;
                }

                if (!announced)
                {
                    _logger.LogInformation($"Teams calls import paused: message '{message.MessageId}' waits before it is imported.");
                    announced = true;
                }
                await _delay(wait, cancellationToken);
            }
        }

        private void LogPause(TimeSpan? pause, string callId, string error)
        {
            if (pause.HasValue)
            {
                _logger.LogWarning($"Teams calls import paused for {pause.Value.TotalMinutes:0.#} minute(s): more than one call has failed in a way " +
                    $"that usually affects every call - most recently call '{callId}' ({error}). " +
                    "Queued calls wait in the queue meanwhile, instead of each failing and using up its retries.");
            }
        }

        private async Task<CallQueueMessageOutcome> CompleteAsync(CallQueueMessage message, ICallQueueMessageSettlement settlement)
        {
            try
            {
                // Complete the message. messages is deleted from the queue.
                await settlement.CompleteAsync();
                _logger.LogInformation($"Succesfully processed & completed ServiceBus message ID '{message.MessageId}'");
            }
            catch (ServiceBusException ex)
            {
                _logger.LogError(ex, $"Couldn't complete ServiceBus message '{message.MessageId}': " + ex.Message);
#if DEBUG
                throw;
#endif
            }
            return CallQueueMessageOutcome.Completed;
        }

        private async Task<CallQueueMessageOutcome> ScheduleRetryAsync(CallQueueMessage message, ICallQueueMessageSettlement settlement, string callId,
            CallImportFailureKind kind, CallQueueRetryDecision decision)
        {
            try
            {
                await settlement.ScheduleRetryAsync(decision.NextState, decision.RetryAtUtc);
            }
            catch (Exception ex)
            {
                // Couldn't re-queue it, so fall back to letting Service Bus redeliver it. Service Bus failing affects
                // every call, so this counts towards a pause as well.
                _logger.LogError(ex, $"Couldn't queue a later retry of call '{callId}': {ex.Message}");
                LogPause(_breaker.RecordFailure(CallImportFailureKind.Systemic, callId, wasProbe: false, _clock.UtcNow), callId, ex.Message);
                _logger.LogInformation($"Abandoning ServiceBus message ID '{message.MessageId}' as import was NOT succesful");
                await TryAbandonAsync(message, settlement);
                return CallQueueMessageOutcome.Abandoned;
            }

            try
            {
                await settlement.CompleteAsync();
            }
            catch (Exception ex)
            {
                // The retry is queued, and this message will be delivered again when its lock expires: harmless, as
                // saving a call replaces any copy already saved.
                _logger.LogWarning($"Queued a retry of call '{callId}' but couldn't complete ServiceBus message '{message.MessageId}': {ex.Message}. It will be delivered again.");
            }

            _logger.LogWarning($"Call '{callId}' was not imported ({kind}; failed attempts used: {decision.NextState.Failures} of {_policy.MaxFailures} " +
                $"for this call, {decision.NextState.SystemicFailures} of {_policy.MaxSystemicFailures} outage-type). " +
                $"Retry {decision.NextState.Retries} queued for {decision.RetryAtUtc:u}. Last error: {decision.NextState.LastError}");
            return CallQueueMessageOutcome.RetryScheduled;
        }

        private async Task<CallQueueMessageOutcome> DeadLetterAsync(CallQueueMessage message, ICallQueueMessageSettlement settlement, string reason, string description)
        {
            _logger.LogError($"Dead-lettering ServiceBus message ID '{message.MessageId}' ({reason}): {description}");
            await settlement.DeadLetterAsync(reason, description);
            return CallQueueMessageOutcome.DeadLettered;
        }

        private async Task TryAbandonAsync(CallQueueMessage message, ICallQueueMessageSettlement settlement)
        {
            try
            {
                await settlement.AbandonAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Couldn't abandon ServiceBus message '{message.MessageId}': {ex.Message}. It will be delivered again when its lock expires.");
            }
        }
    }

    /// <summary>
    /// Service Bus implementation of <see cref="ICallQueueMessageSettlement"/> for one message the processor received.
    /// </summary>
    public sealed class ServiceBusCallQueueMessageSettlement : ICallQueueMessageSettlement
    {
        private const int MaxDeadLetterReasonLength = 128;
        private const int MaxDeadLetterDescriptionLength = 1024;

        private readonly ProcessMessageEventArgs _args;
        private readonly ServiceBusSender _retrySender;

        /// <param name="retrySender">A sender for the same queue, used to schedule retries.</param>
        public ServiceBusCallQueueMessageSettlement(ProcessMessageEventArgs args, ServiceBusSender retrySender)
        {
            _args = args ?? throw new ArgumentNullException(nameof(args));
            _retrySender = retrySender ?? throw new ArgumentNullException(nameof(retrySender));
        }

        public Task CompleteAsync() => _args.CompleteMessageAsync(_args.Message);

        public Task AbandonAsync() => _args.AbandonMessageAsync(_args.Message);

        public Task DeadLetterAsync(string reason, string description)
            => _args.DeadLetterMessageAsync(_args.Message, Truncate(reason, MaxDeadLetterReasonLength), Truncate(description, MaxDeadLetterDescriptionLength));

        public Task ScheduleRetryAsync(CallQueueRetryState state, DateTime enqueueAtUtc)
            => _retrySender.ScheduleMessageAsync(BuildRetryMessage(_args.Message, state), new DateTimeOffset(DateTime.SpecifyKind(enqueueAtUtc, DateTimeKind.Utc)));

        /// <summary>
        /// The copy queued for a retry: the original's body and properties plus the retry bookkeeping, under a new
        /// message id - were duplicate detection ever enabled on the queue, a copy with the original's id would be
        /// discarded as a duplicate after the original was completed.
        /// </summary>
        public static ServiceBusMessage BuildRetryMessage(ServiceBusReceivedMessage original, CallQueueRetryState state)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (state == null) throw new ArgumentNullException(nameof(state));

            var copy = new ServiceBusMessage(original)
            {
                MessageId = Guid.NewGuid().ToString("N")
            };
            state.WriteTo(copy.ApplicationProperties);
            return copy;
        }

        private static string Truncate(string value, int maxLength)
        {
            return value != null && value.Length > maxLength ? value.Substring(0, maxLength) : value;
        }
    }
}
