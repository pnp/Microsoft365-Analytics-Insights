/**
 * `GET api/CallDeadLetters` and `POST api/CallDeadLetters/requests`: the dead-lettered Teams calls card on Administration >
 * Service configuration. The server sends facts and stable codes only; the card writes every sentence.
 */

/** What an administrator can ask the importer to do with the dead-letter queue. */
export type CallDeadLetterAction = 'reprocess' | 'clear';

/** Where the latest request stands. `notPickedUp` and `notFinished` are stale: a new request may replace them. */
export type CallDeadLetterRequestState = 'waiting' | 'running' | 'finished' | 'notPickedUp' | 'notFinished';

export type CallDeadLetterOutcome = 'succeeded' | 'partial' | 'failed';

/** What became of the messages one request dealt with that were dead-lettered with one reason. */
export interface CallDeadLetterReasonCount {
  /** The dead-letter reason Service Bus holds - a code such as `CallImportRetriesExhausted` - or null for none. */
  reason: string | null;
  replayed: number;
  deleted: number;
  /** Left in the dead-letter queue: skipped on purpose, or failed. */
  left: number;
}

export interface CallDeadLetterRunResult {
  passes: number;
  /** Looked at, added up across passes: a message left in the queue is counted once per pass. */
  scanned: number;
  replayed: number;
  replayedButNotRemoved: number;
  deleted: number;
  sendFailed: number;
  deleteFailed: number;
  /** Left in the dead-letter queue on purpose, by code (`neverImportable`, `callRecordNoLongerInGraph`, ...). */
  skipped: Record<string, number>;
  reasons: CallDeadLetterReasonCount[];
  stopCode: string | null;
}

export interface CallDeadLetterRequestView {
  id: string;
  /** `reprocess` or `clear`; typed as a string because a newer server may send one this build doesn't know. */
  action: CallDeadLetterAction | string;
  state: CallDeadLetterRequestState | string;
  requestedUtc: string;
  claimedUtc: string | null;
  finishedUtc: string | null;
  outcome: CallDeadLetterOutcome | string | null;
  failureCode: string | null;
  result: CallDeadLetterRunResult | null;
}

export interface CallDeadLetterStatus {
  available: boolean;
  /** `callsImportDisabled`, `serviceBusNotConfigured` or `stateStoreInMemory`; null when available. */
  unavailableReason: string | null;
  deadLetterCount: number | null;
  activeCount: number | null;
  countUnavailable: boolean;
  latest: CallDeadLetterRequestView | null;
  latestUnavailable: boolean;
  canRequest: boolean;
  pickUpWithinMinutes: number;
  finishWithinMinutes: number;
  runBudgetMinutes: number;
}
