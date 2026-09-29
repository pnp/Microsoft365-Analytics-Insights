import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n';
import { apiFetch } from './http';
import type { UserImportCheckpointClearResult, UserImportCheckpointStatus } from '../types/userImportCheckpoint';

const baseUrl = (): string => `${window.location.origin}/api/UserImportCheckpoint`;

/**
 * UserImportCheckpointAPIController answers a failure with a stable code and no text; these are the sentences for
 * the codes this build knows. A Map rather than an object, so a code such as `constructor` can't find a match on
 * Object.prototype. Anything unrecognised gets the generic message with the HTTP status.
 */
const ERROR_CODE_KEYS = new Map<string, TranslationKey>([
  ['redisUnavailable', 'errors.userImportCheckpoint.redisUnavailable'],
  ['redisNotConfigured', 'errors.userImportCheckpoint.redisNotConfigured'],
]);

async function failure(response: Response, fallbackKey: TranslationKey): Promise<Error> {
  let code: unknown;
  try {
    code = ((await response.json()) as { code?: unknown } | null)?.code;
  } catch {
    // No JSON body - the same-origin check's bare 403, or an error page from IIS.
  }

  const key = typeof code === 'string' ? ERROR_CODE_KEYS.get(code) : undefined;
  return new Error(key ? translateActive(key) : translateActive(fallbackKey, { status: response.status }));
}

/** Whether the Graph user import has a stored checkpoint, and when the import last completed. */
export async function fetchUserImportCheckpoint(): Promise<UserImportCheckpointStatus> {
  const response = await apiFetch(baseUrl(), {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw await failure(response, 'errors.userImportCheckpoint.loadFailed');
  }

  return (await response.json()) as UserImportCheckpointStatus;
}

/**
 * Clears the checkpoint, so the next user import reads every user again. `runOnNextCycle` also clears the
 * last-completed stamp, so that import runs on the next cycle rather than once its interval has passed.
 *
 * It must go through `apiFetch`: the server refuses a state-changing call without the `X-Requested-With` header
 * that `apiFetch` adds (its cross-site request forgery check, `RequireSameOriginXhrAttribute`).
 */
export async function clearUserImportCheckpoint(runOnNextCycle: boolean): Promise<UserImportCheckpointClearResult> {
  const response = await apiFetch(`${baseUrl()}/clear`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ runOnNextCycle }),
  });

  if (!response.ok) {
    throw await failure(response, 'errors.userImportCheckpoint.clearFailed');
  }

  return (await response.json()) as UserImportCheckpointClearResult;
}
