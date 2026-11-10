/**
 * The dead-lettered Teams calls card on Administration > Service configuration (`CallDeadLettersCard`).
 *
 * The server sends codes and counts only - the action, the request's state, an outcome, a failure code, skip codes and the
 * dead-letter reasons Service Bus holds - and every sentence is written here. A dead-letter reason this build doesn't map
 * is shown as the code it is.
 */
export const callDeadLetters = {
  'callDeadLetters.title': 'Dead-lettered Teams calls',
  'callDeadLetters.description':
    "Call notifications the importer couldn't import end up in the Teams calls queue's dead-letter queue: those it can never import, and those whose retries ran out - after Graph or SQL was unavailable for hours, say. Reprocess sends them back to the queue for the importer to try again; Clear deletes them.",
  'callDeadLetters.ariaLabel': 'Dead-lettered Teams calls',
  'callDeadLetters.loading': 'Loading the dead-lettered calls...',
  'callDeadLetters.countLabel': 'Dead-lettered calls',
  'callDeadLetters.countUnavailable': "Service Bus didn't give the count. The Service health page says why.",
  'callDeadLetters.activeLabel': 'Waiting on the queue',
  'callDeadLetters.empty': 'The dead-letter queue is empty, so there is nothing to reprocess or clear.',
  'callDeadLetters.latestLabel': 'Latest request',
  'callDeadLetters.latestNone': 'None yet.',
  'callDeadLetters.latestUnavailable':
    "Couldn't read the latest request from Azure Table storage, so a new one can't be made just now. Refresh in a moment.",

  'callDeadLetters.action.reprocess': 'Reprocess',
  'callDeadLetters.action.clear': 'Clear...',
  'callDeadLetters.action.requesting': 'Asking the importer...',
  'callDeadLetters.actionName.reprocess': 'Reprocess',
  'callDeadLetters.actionName.clear': 'Clear',
  'callDeadLetters.reprocessHint':
    'Reprocess sends back every dead-lettered call the importer could still import. Those it can never import, and those first queued over 30 days ago (Graph no longer has them), stay where they are.',
  'callDeadLetters.pendingHint': 'A request is waiting or running. You can ask again once it has finished.',

  'callDeadLetters.state.waiting': 'Waiting for the importer',
  'callDeadLetters.state.running': 'Running',
  'callDeadLetters.state.succeeded': 'Finished',
  'callDeadLetters.state.partial': 'Partly done',
  'callDeadLetters.state.failed': 'Failed',
  'callDeadLetters.state.notPickedUp': 'Not picked up',
  'callDeadLetters.state.notFinished': 'Not finished',
  'callDeadLetters.state.unknown': 'Status {state}',

  'callDeadLetters.detail.waiting': 'Asked for at {time}. The importer checks for new requests every minute.',
  'callDeadLetters.detail.running':
    'The importer picked it up at {time}. It works on a request for at most {minutes} minutes, then records what it did.',
  'callDeadLetters.detail.notPickedUp':
    "Asked for at {time}, but no importer picked it up within {minutes} minutes, so it won't run. Check that the Office 365 importer WebJob is running and uses this deployment's storage account, then ask again.",
  'callDeadLetters.detail.notFinished':
    "The importer picked it up at {time} but didn't finish it within {hours} hours: it was probably stopped or restarted part-way, so some calls may have been dealt with. You can ask again.",
  'callDeadLetters.detail.finished': 'Finished at {time}.',

  'callDeadLetters.failure.queueUnavailable':
    "The importer couldn't open the Teams calls queue, so nothing was done. The Service health page shows whether Service Bus can be reached.",
  'callDeadLetters.failure.readFailed': "Reading the dead-letter queue failed. The importer's log has the error.",
  'callDeadLetters.failure.sendFailures':
    "Sending calls back to the queue kept failing, so the importer stopped. The importer's log has the error.",
  'callDeadLetters.failure.sendFailed':
    "Some calls couldn't be sent back to the queue, so they stay in the dead-letter queue. Ask again to retry them.",
  'callDeadLetters.failure.deleteFailed':
    "Some calls couldn't be deleted, so they stay in the dead-letter queue. Ask again to retry them.",
  'callDeadLetters.failure.lockedElsewhere':
    "Some dead-lettered calls were locked by something else - another replay, or Service Bus Explorer, say - so the importer couldn't reach them. Ask again once they are free.",
  'callDeadLetters.failure.locksLapsing':
    'The calls left at the head of the dead-letter queue kept losing their locks before the importer could get past them. Clear the queue, or ask again.',
  'callDeadLetters.failure.browseRepeated': "Reading the dead-letter queue went wrong. The importer's log has the detail.",
  'callDeadLetters.failure.cancelled': 'The importer was stopped before it finished.',
  'callDeadLetters.failure.timeBudget':
    'It stopped after {minutes} minutes, the longest one request may take, with more left to do. Ask again to carry on.',
  'callDeadLetters.failure.scanLimit':
    'It looked at as many dead-lettered calls as one request may without reaching the end of the dead-letter queue. Ask again to carry on - or, if most of what is left can never be imported, clear the queue.',
  'callDeadLetters.failure.scanLimitClear': 'It deleted as many dead-lettered calls as one request may, and there are more. Ask again to carry on.',
  'callDeadLetters.failure.unexpectedError': "Something unexpected went wrong. The importer's log has the error.",
  'callDeadLetters.failure.unknown': "It didn't finish cleanly ({code}). The importer's log has the detail.",

  'callDeadLetters.result.replayed': 'Sent back to the queue: {count}',
  'callDeadLetters.result.deleted': 'Deleted: {count}',
  'callDeadLetters.result.left': 'Left in the dead-letter queue: {count}',
  'callDeadLetters.result.sendFailed': "Couldn't be sent back: {count}",
  'callDeadLetters.result.deleteFailed': "Couldn't be deleted: {count}",
  'callDeadLetters.result.replayedButNotRemoved':
    "{count} of the calls sent back couldn't then be removed from the dead-letter queue, so a later request may send them again. That is harmless: saving a call replaces any copy already saved.",
  'callDeadLetters.skipped.neverImportable': 'Can never be imported as they stand: {count}',
  'callDeadLetters.skipped.callRecordNoLongerInGraph': 'First queued over 30 days ago, so Graph no longer has them: {count}',
  'callDeadLetters.skipped.replayLimitReached': 'Reprocessed as many times as allowed already: {count}',
  'callDeadLetters.skipped.replayedInThisRun': 'Sent back by this request and dead-lettered again already: {count}',
  'callDeadLetters.skipped.otherReason': 'Dead-lettered for a reason this request left alone: {count}',
  'callDeadLetters.skipped.unknown': 'Left for another reason ({code}): {count}',

  'callDeadLetters.reasons.title': 'By dead-letter reason',
  'callDeadLetters.reasons.ariaLabel': 'What became of the dead-lettered calls, by dead-letter reason',
  'callDeadLetters.reasons.reason': 'Dead-letter reason',
  'callDeadLetters.reasons.replayed': 'Sent back',
  'callDeadLetters.reasons.deleted': 'Deleted',
  'callDeadLetters.reasons.left': 'Left',
  'callDeadLetters.reason.InvalidCallNotification': "Not a Graph call notification",
  'callDeadLetters.reason.MissingCallId': 'No call id in the notification',
  'callDeadLetters.reason.CallImportNotRetryable': 'Failed the same way every time',
  'callDeadLetters.reason.CallImportRetriesExhausted': 'Retries ran out',
  'callDeadLetters.reason.CallImportSystemicRetriesExhausted': 'Retries ran out during an outage',
  'callDeadLetters.reason.CallImportRetryWindowExpired': 'Still failing when its retry window closed',
  'callDeadLetters.reason.MaxDeliveryCountExceeded': 'Service Bus gave up after too many deliveries',
  'callDeadLetters.reason.NoOrganiserIdentity': 'The call record names no organiser',
  'callDeadLetters.reason.NoSessions': 'The call record has no sessions',
  'callDeadLetters.reason.NoUsableSessions': "None of the call record's sessions could be used",
  'callDeadLetters.reason.NoCallType': 'The call record has no call type',
  'callDeadLetters.reason.none': 'No reason recorded',

  'callDeadLetters.unavailable.callsImportDisabled': 'The Teams calls import is switched off, so there is no calls queue to work on.',
  'callDeadLetters.unavailable.serviceBusNotConfigured': "Service Bus isn't configured on this deployment, so there is no Teams calls queue.",
  'callDeadLetters.unavailable.stateStoreInMemory':
    "This deployment has no Azure Storage connection string, so the portal can't pass a request to the importer, which runs as a separate WebJob. An operator can still reprocess or clear the dead-letter queue by running the importer with --replayCallDeadLetters from the WebJob's console.",
  'callDeadLetters.unavailable.unknown': 'Not available on this deployment ({reason}).',

  'callDeadLetters.error.loadFailed': "Couldn't load the dead-lettered calls ({status}).",
  'callDeadLetters.error.loadFailedGeneric': "Couldn't load the dead-lettered calls.",
  'callDeadLetters.error.requestFailed': "Couldn't pass the request to the importer ({status}).",
  'callDeadLetters.error.requestFailedGeneric': "Couldn't pass the request to the importer.",
  'callDeadLetters.error.unknownAction': "The server didn't recognise the request. Reload the page and try again.",
  'callDeadLetters.error.requestPending':
    'Another request is already waiting or running. You can ask again once it has finished, or once it has gone stale.',
  'callDeadLetters.error.stateStoreUnavailable': "Couldn't reach Azure Table storage, where requests are kept. Try again in a moment.",

  'callDeadLetters.confirm.title': 'Clear the dead-letter queue?',
  'callDeadLetters.confirm.body':
    'This permanently deletes every dead-lettered Teams call notification, whatever its reason or age - including those the importer could still import. Those calls will never be imported, and this cannot be undone.',
  'callDeadLetters.confirm.count': 'At the last count there were {count}.',
  'callDeadLetters.confirm.alternative': 'To have the importer try them again instead, use Reprocess.',
  'callDeadLetters.confirm.clear': 'Delete them permanently',
} as const;

export default callDeadLetters;
