using Common.Entities.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Turns a Graph change notification about a finished Teams call into a saved call record.
    /// Extracted from <see cref="CallQueueProcessor"/> so the import logic is separated from the
    /// Service Bus plumbing and can be tested with no Graph, no SQL and no Service Bus.
    /// See issue #378.
    /// </summary>
    public class CallRecordImporter
    {
        private readonly ICallRecordSourceLoader _source;
        private readonly ICallRecordPersistenceManager _store;
        private readonly ILogger _logger;

        public CallRecordImporter(ICallRecordSourceLoader source, ICallRecordPersistenceManager store, ILogger logger)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Load the call the notification refers to and save it.
        /// </summary>
        /// <returns>
        /// The call record when the notification was processed successfully, or <c>null</c> when it
        /// wasn't - the caller uses that to decide whether to complete or abandon the queue message.
        /// </returns>
        /// <remarks>
        /// A call with no organiser email is deliberately NOT saved but IS reported as processed: the
        /// organiser is a required foreign key on the call record, and a call we can't resolve an
        /// organiser for will never become resolvable, so retrying it forever would block the queue.
        /// This is pre-existing behaviour, preserved.
        /// </remarks>
        public async Task<CallRecordDTO> ImportFromNotification(GraphChangeNotification change)
        {
            string callId = change?.ResourceData.Id;
            if (string.IsNullOrEmpty(callId))
            {
                _logger.LogInformation("ServiceBus error: couldn't find call ID in JSon. Ignoring event.");
                return null;
            }

            var callResponse = await _source.LoadCallRecord(callId);
            if (callResponse == null)
            {
                _logger.LogWarning($"Could not load call record '{callId}' from Graph. Skipping.");
                return null;
            }

            if (!string.IsNullOrEmpty(callResponse.OrganizerEmail))
            {
                await _store.SaveOrReplaceCallRecord(callResponse);
            }

            return callResponse;
        }
    }
}
