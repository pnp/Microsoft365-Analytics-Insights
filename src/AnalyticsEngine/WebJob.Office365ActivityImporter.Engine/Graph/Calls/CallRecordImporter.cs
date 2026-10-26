using Common.Entities.Models;
using Common.Entities.UserScope;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// Processed but deliberately not saved - the organiser can't be resolved to a user of this tenant, or nobody on
        /// it is in <c>UserGroupsFilter</c>. SQL was never touched, so this says nothing about whether saving works.
        /// </summary>
        ProcessedWithoutSaving
    }

    /// <summary>The result of <see cref="CallRecordImporter.Import"/>.</summary>
    public sealed class CallImportResult
    {
        public static readonly CallImportResult NotImported = new CallImportResult(CallImportOutcome.NotImported, null);

        public CallImportResult(CallImportOutcome outcome, CallRecordDTO call, string reason = null)
        {
            Outcome = outcome;
            Call = call;
            Reason = reason;
        }

        public CallImportOutcome Outcome { get; }

        /// <summary>The call record read from Graph; null when <see cref="CallImportOutcome.NotImported"/>.</summary>
        public CallRecordDTO Call { get; }

        /// <summary>Why a call was <see cref="CallImportOutcome.ProcessedWithoutSaving"/>, e.g. <c>OutOfScope</c>.</summary>
        public string Reason { get; }
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
        private readonly WarningRateLimiter _unresolvedOrganiserWarnings;

        /// <summary>How often the warning for calls whose organiser can't be resolved is let through, per reason.</summary>
        public static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(10);

        /// <param name="userScopeProvider">
        /// The process-lifetime <c>UserGroupsFilter</c> scope. Null means unfiltered.
        /// </param>
        public CallRecordImporter(ICallRecordSourceLoader source, ICallRecordPersistenceManager store, ILogger logger,
            IUserImportScopeProvider userScopeProvider = null, IClock clock = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _userScopeProvider = userScopeProvider;
            _unresolvedOrganiserWarnings = new WarningRateLimiter(WarningInterval, clock);
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
        /// A call whose organiser can't be resolved to a user of this tenant (an external organiser, a deleted account) is
        /// deliberately NOT saved but IS reported as processed: the organiser is a required foreign key on the call record,
        /// and such a call will never become resolvable, so retrying it forever would block the queue. A call with nobody
        /// on it in the <c>UserGroupsFilter</c> scope is handled the same way: processed, not saved.
        /// A call whose payload names no organiser at all is different: Graph's schema has changed, so it throws
        /// <see cref="CallRecordPayloadException"/> and is retried, then dead-lettered.
        /// </remarks>
        public async Task<CallRecordDTO> ImportFromNotification(GraphChangeNotification change)
        {
            return (await Import(change)).Call;
        }

        /// <summary>
        /// As <see cref="ImportFromNotification"/>, also saying whether the call was saved or only processed.
        /// </summary>
        /// <exception cref="CallRecordPayloadException">
        /// The call record has no organiser identity in either schema: that is a Graph schema problem, so the message is
        /// retried and then dead-lettered rather than completed (see <see cref="CallQueueMessageHandler"/>).
        /// </exception>
        public async Task<CallImportResult> Import(GraphChangeNotification change)
        {
            var stopwatch = Stopwatch.StartNew();
            string callId = change?.ResourceData?.Id;
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
                    LogOutcome(callId, "NotSaved:OutOfScope", callResponse, stopwatch);
                    return new CallImportResult(CallImportOutcome.ProcessedWithoutSaving, callResponse, "OutOfScope");
                }

                await _store.SaveOrReplaceCallRecord(callResponse, userScope);
                LogOutcome(callId, "Saved", callResponse, stopwatch);
                return new CallImportResult(CallImportOutcome.Saved, callResponse);
            }

            if (callResponse.OrganiserStatus == CallOrganiserStatus.NoIdentity)
            {
                // Neither organizer_v2 nor the retired organizer: Graph's payload has changed under us. Completing the
                // message as processed would lose every call silently (Graph keeps a call record for only 30 days), so
                // it is a failure - retried, as a later version of the record can carry more, then dead-lettered.
                _logger.LogWarning($"Call record '{callId}' has no organiser identity in the payload (neither 'organizer_v2' nor 'organizer'), so its organiser cannot be " +
                    "resolved. If this is every call, Graph's call record schema has changed. The call is retried, then dead-lettered as " +
                    $"{CallRecordPayloadException.NoOrganiserIdentityReason}.");
                throw new CallRecordPayloadException(callId, CallRecordPayloadException.NoOrganiserIdentityReason,
                    "The call record has no organiser identity (neither 'organizer_v2' nor 'organizer' is in the payload).");
            }

            // An identity is there but can't be turned into a user of this tenant: an external organiser, a deleted
            // account. It never will, so the call is processed, not saved, and the reason is said once in a while.
            var reason = callResponse.OrganiserStatus == CallOrganiserStatus.NotEvaluated ? "OrganiserNotResolved" : $"OrganiserNotResolved:{callResponse.OrganiserStatus}";
            LogOutcome(callId, $"NotSaved:{reason}", callResponse, stopwatch);
            if (_unresolvedOrganiserWarnings.TryAcquire(reason, out var suppressed))
            {
                _logger.LogWarning($"Call record '{callId}' is not saved: its organiser could not be resolved to a user of this tenant ({callResponse.OrganiserStatus}). " +
                    $"Calls like this are not saved; this is logged at most once every {WarningInterval.TotalMinutes:0} minutes per reason ({suppressed} similar call(s) since the last).");
            }
            return new CallImportResult(CallImportOutcome.ProcessedWithoutSaving, callResponse, reason);
        }

        /// <summary>The one line a call's import leaves behind, for the outcomes decided here. See <see cref="CallImportLog"/>.</summary>
        private void LogOutcome(string callId, string outcome, CallRecordDTO call, Stopwatch stopwatch)
        {
            _logger.LogInformation(CallImportLog.Outcome(callId, outcome, call, stopwatch.ElapsedMilliseconds));
        }
    }

    /// <summary>The wording of the one outcome line per call, so it can be searched for in a log export.</summary>
    public static class CallImportLog
    {
        /// <summary>Every call's outcome line starts with this.</summary>
        public const string Prefix = "CallImport outcome:";

        public static string Outcome(string callId, string outcome, CallRecordDTO call, long elapsedMs, string failure = null)
        {
            var stats = call?.LoadStats;
            return $"{Prefix} call='{callId}' outcome={outcome}" +
                (call == null ? string.Empty
                    : $" sessions={call.Sessions?.Count ?? 0} pages={stats.SessionPages} users={stats.DistinctUsers} external={stats.ExternalUsers} unresolved={stats.UnresolvedUsers} resolveMs={stats.ResolveMs}") +
                (failure == null ? string.Empty : $" failure={failure}") +
                $" elapsedMs={elapsedMs}";
        }
    }

    /// <summary>Lets a warning through once per interval per key, saying how many were held back.</summary>
    internal sealed class WarningRateLimiter
    {
        private readonly TimeSpan _interval;
        private readonly IClock _clock;
        private readonly object _lock = new object();
        private readonly Dictionary<string, KeyValuePair<DateTime, int>> _state = new Dictionary<string, KeyValuePair<DateTime, int>>(StringComparer.Ordinal);

        public WarningRateLimiter(TimeSpan interval, IClock clock)
        {
            _interval = interval;
            _clock = clock ?? SystemClock.Instance;
        }

        /// <param name="suppressed">When allowed, how many were held back since the last one let through.</param>
        public bool TryAcquire(string key, out int suppressed)
        {
            lock (_lock)
            {
                var now = _clock.UtcNow;
                if (_state.TryGetValue(key, out var last) && now - last.Key < _interval)
                {
                    _state[key] = new KeyValuePair<DateTime, int>(last.Key, last.Value + 1);
                    suppressed = 0;
                    return false;
                }

                suppressed = _state.TryGetValue(key, out last) ? last.Value : 0;
                _state[key] = new KeyValuePair<DateTime, int>(now, 0);
                return true;
            }
        }
    }
}
