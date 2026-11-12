import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n';
import { apiFetch } from './http';
import type { CallDeadLetterAction, CallDeadLetterStatus } from '../types/callDeadLetters';

const baseUrl = (): string => `${window.location.origin}/api/CallDeadLetters`;

/**
 * CallDeadLettersAPIController answers a refusal with a stable code and no text; these are the sentences for the codes
 * this build knows. A Map rather than an object, so a code such as `constructor` can't find a match on Object.prototype.
 * Anything unrecognised gets the generic message with the HTTP status. `src/i18n/lint/callDeadLetterServerCodes.test.ts`
 * checks this against the C#.
 */
export const CALL_DEAD_LETTER_ERROR_KEYS = new Map<string, TranslationKey>([
  ['unknownAction', 'callDeadLetters.error.unknownAction'],
  ['requestPending', 'callDeadLetters.error.requestPending'],
  ['stateStoreUnavailable', 'callDeadLetters.error.stateStoreUnavailable'],
  ['callsImportDisabled', 'callDeadLetters.unavailable.callsImportDisabled'],
  ['serviceBusNotConfigured', 'callDeadLetters.unavailable.serviceBusNotConfigured'],
  ['stateStoreInMemory', 'callDeadLetters.unavailable.stateStoreInMemory'],
]);

async function failure(response: Response, fallbackKey: TranslationKey): Promise<Error> {
  let code: unknown;
  try {
    code = ((await response.json()) as { code?: unknown } | null)?.code;
  } catch {
    // No JSON body - the same-origin check's bare 403, or an error page from IIS.
  }

  const key = typeof code === 'string' ? CALL_DEAD_LETTER_ERROR_KEYS.get(code) : undefined;
  return new Error(key ? translateActive(key) : translateActive(fallbackKey, { status: response.status }));
}

/** The dead-letter count, whether a request can reach the importer, and the latest request and its result. */
export async function fetchCallDeadLetters(): Promise<CallDeadLetterStatus> {
  const response = await apiFetch(baseUrl(), {
    method: 'GET',
    cache: 'no-store',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw await failure(response, 'callDeadLetters.error.loadFailed');
  }

  return (await response.json()) as CallDeadLetterStatus;
}

/**
 * Asks the importer to reprocess or clear the dead-letter queue, and answers with the status as it now stands. Through
 * `apiFetch`, as every state-changing call must be: the server refuses one without the `X-Requested-With` header it adds.
 */
export async function requestCallDeadLetterAction(action: CallDeadLetterAction): Promise<CallDeadLetterStatus> {
  const response = await apiFetch(`${baseUrl()}/requests`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ action }),
  });

  if (!response.ok) {
    throw await failure(response, 'callDeadLetters.error.requestFailed');
  }

  return (await response.json()) as CallDeadLetterStatus;
}
