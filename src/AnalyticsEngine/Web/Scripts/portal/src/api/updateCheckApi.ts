import { apiFetch } from './http';
import type { UpdateCheck } from '../types/updateCheck';

const baseUrl = (): string => window.o365AnalyticsUpdateCheckAPI ?? `${window.location.origin}/api/UpdateCheck`;

/**
 * Asks the server to compare the running build against the latest published GitHub release.
 *
 * Only called when the admin presses the button - nothing polls. The server caches GitHub's answer
 * briefly, so repeated presses don't burn GitHub's anonymous rate limit (which the installer shares).
 */
export async function fetchUpdateCheck(): Promise<UpdateCheck> {
  const response = await apiFetch(baseUrl(), {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(`Couldn't check for updates (${response.status}).`);
  }

  return response.json() as Promise<UpdateCheck>;
}
