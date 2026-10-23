using Common.Entities.Models;
using Common.Entities.UserScope;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>What importing one call notification did.</summary>
    public enum CallImportOutcome
    {
        /// <summary>Graph returned nothing usable, so nothing was imported: retry later.</summary>
        NotImported,

        /// <summary>The call was saved to SQL.</summary>
        Saved,

        /// <summary>
        /// Processed but deliberately not saved - no organiser email, or nobody on it in <c>UserGroupsFilter</c>.
        /// SQL was never touched, so this says nothing about whether saving works.
        /// </summary>
        ProcessedWithoutSaving
    }

    /// <summary>The result of <see cref="CallRecordImporter.Import"/>.</summary>
    public sealed class CallImportResult
    {
        public static readonly CallImportResult NotImported = new CallImportResult(CallImportOutcome.NotImported, null);

        public CallImportResult(CallImportOutcome outcome, CallRecordDTO call)
        {
            Outcome = outcome;
            Call = call;
        }

        public CallImportOutcome Outcome { get; }

        /// <summary>The call record read from Graph; null when <see cref="CallImportOutcome.NotImported"/>.</summary>
        public CallRecordDTO Call { get; }
    }

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
        private readonly IUserImportScopeProvider _userScopeProvider;

        /// <param name="userScopeProvider">
        /// The process-lifetime <c>UserGroupsFilter</c> scope. Null means unfiltered.
        /// </param>
        public CallRecordImporter(ICallRecordSourceLoader source, ICallRecordPersistenceManager store, ILogger logger,
            IUserImportScopeProvider userScopeProvider = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userScopeProvider = userScopeProvider;
        }

        /// <summary>
        /// Load the call the notification refers to and save it.
        /// </summary>
        /// <returns>
        /// The call record when the notification was processed successfully, or <c>null</c> when it
        /// wasn't - the caller uses that to decide whether to complete the queue message or retry it later.
        /// Whether the call was actually saved is reported by <see cref="Import"/>.
        /// </returns>
        /// <exception cref="CallRecordSessionPagingException">
        /// Not all of the call's sessions could be read; nothing was saved. See <see cref="CallQueueMessageHandler"/>
        /// for how the queue message is then retried or dead-lettered.
        /// </exception>
        /// <remarks>
        /// A call with no organiser email is deliberately NOT saved but IS reported as processed: the
        /// organiser is a required foreign key on the call record, and a call we can't resolve an
        /// organiser for will never become resolvable, so retrying it forever would block the queue.
        /// This is pre-existing behaviour, preserved. A call with nobody on it in the <c>UserGroupsFilter</c>
        /// scope is handled the same way: processed, not saved.
        /// </remarks>
        public async Task<CallRecordDTO> ImportFromNotification(GraphChangeNotification change)
        {
            return (await Import(change)).Call;
        }

        /// <summary>
        /// As <see cref="ImportFromNotification"/>, also saying whether the call was saved or only processed.
        /// </summary>
        public async Task<CallImportResult> Import(GraphChangeNotification change)
        {
            string callId = change?.ResourceData.Id;
            if (string.IsNullOrEmpty(callId))
            {
                _logger.LogInformation("ServiceBus error: couldn't find call ID in JSon. Ignoring event.");
                return CallImportResult.NotImported;
            }

            var callResponse = await _source.LoadCallRecord(callId);
            if (callResponse == null)
            {
                _logger.LogWarning($"Could not load call record '{callId}' from Graph. Skipping.");
                return CallImportResult.NotImported;
            }

            if (!string.IsNullOrEmpty(callResponse.OrganizerEmail))
            {
                var userScope = _userScopeProvider == null ? UserImportScope.Unfiltered : await _userScopeProvider.GetScopeAsync();
                if (!CallRecordScopeRules.AnyParticipantInScope(callResponse, userScope))
                {
                    _logger.LogInformation($"Call record '{callId}': nobody on the call is in UserGroupsFilter, so it is not saved.");
                    return new CallImportResult(CallImportOutcome.ProcessedWithoutSaving, callResponse);
                }

                await _store.SaveOrReplaceCallRecord(callResponse, userScope);
                return new CallImportResult(CallImportOutcome.Saved, callResponse);
            }

            return new CallImportResult(CallImportOutcome.ProcessedWithoutSaving, callResponse);
        }
    }
}
