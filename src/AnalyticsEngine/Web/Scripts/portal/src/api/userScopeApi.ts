import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n';
import { apiFetch } from './http';
import type { UserScopePurgeJob, UserScopeStatus } from '../types/userScope';

const baseUrl = (): string => `${window.location.origin}/api/UserScope`;

/**
 * UserScopeAPIController answers failures with stable codes and no text; these are the sentences for the codes
 * this build knows. A Map rather than an object, so a code such as `constructor` can't find a match on
 * Object.prototype. Anything unrecognised gets the generic message with the HTTP status.
 */
const ERROR_CODE_KEYS = new Map<string, TranslationKey>([
  ['acknowledgementRequired', 'errors.userScope.acknowledgementRequired'],
  ['scopeNotFiltered', 'errors.userScope.scopeNotFiltered'],
  ['scopeUnavailable', 'errors.userScope.scopeUnavailable'],
  ['scopeEmpty', 'errors.userScope.scopeEmpty'],
  ['nothingToPurge', 'errors.userScope.nothingToPurge'],
  ['purgeAlreadyRunning', 'errors.userScope.purgeAlreadyRunning'],
  ['jobNotFound', 'errors.userScope.jobNotFound'],
  ['jobNotActive', 'errors.userScope.jobNotActive'],
  ['databaseUnavailable', 'errors.userScope.databaseUnavailable'],
  ['storageUnavailable', 'errors.userScope.storageUnavailable'],
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

/** Current user-scope filter resolution, database counts and the latest purge job if one exists. */
export async function fetchUserScope(): Promise<UserScopeStatus> {
  const response = await apiFetch(baseUrl(), {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw await failure(response, 'errors.userScope.loadFailed');
  }

  return (await response.json()) as UserScopeStatus;
}

/**
 * Re-reads the configured Entra ID groups from Microsoft Graph now, rather than waiting for the next importer cycle.
 *
 * It must go through `apiFetch`: the server refuses a state-changing call without the `X-Requested-With` header
 * that `apiFetch` adds (its cross-site request forgery check, `RequireSameOriginXhrAttribute`).
 */
export async function refreshUserScope(): Promise<UserScopeStatus> {
  const response = await apiFetch(`${baseUrl()}/refresh`, {
    method: 'POST',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw await failure(response, 'errors.userScope.refreshFailed');
  }

  return (await response.json()) as UserScopeStatus;
}

/**
 * Starts the irreversible background purge of people outside the resolved scope.
 *
 * The explicit acknowledgement is part of the server contract; the page only calls this after the admin has ticked
 * the confirmation checkbox.
 */
export async function startUserScopePurge(): Promise<UserScopePurgeJob> {
  const response = await apiFetch(`${baseUrl()}/purge`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ acknowledged: true }),
  });

  if (!response.ok) {
    throw await failure(response, 'errors.userScope.startFailed');
  }

  return (await response.json()) as UserScopePurgeJob;
}

/** Latest state of a specific user-scope purge job. */
export async function fetchUserScopePurge(id: number): Promise<UserScopePurgeJob> {
  const response = await apiFetch(`${baseUrl()}/purge/${id}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw await failure(response, 'errors.userScope.pollFailed');
  }

  return (await response.json()) as UserScopePurgeJob;
}

/**
 * Requests cancellation of a queued or running purge. Rows already removed stay removed; the worker stops after its
 * current batch and returns the updated job state.
 */
export async function cancelUserScopePurge(id: number): Promise<UserScopePurgeJob> {
  const response = await apiFetch(`${baseUrl()}/purge/${id}/cancel`, {
    method: 'POST',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw await failure(response, 'errors.userScope.cancelFailed');
  }

  return (await response.json()) as UserScopePurgeJob;
}
