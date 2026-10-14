import { apiFetch } from './http';
import type { LeadershipCohortStatus } from '../types/leadershipCohort';

const BASE = 'api/LeadershipCohort';

/** The server's error code (`LeadershipCohortErrorCodes`), or the fallback when the reply carries none. */
async function failure(response: Response, fallback: string): Promise<Error> {
  const body = await response.json().catch(() => null) as { code?: unknown } | null;
  return new Error(typeof body?.code === 'string' ? body.code : fallback);
}

export async function fetchLeadershipCohort(): Promise<LeadershipCohortStatus> {
  const response = await apiFetch(BASE);
  if (!response.ok) throw await failure(response, 'loadFailed');
  return response.json() as Promise<LeadershipCohortStatus>;
}

/** Saves the group (an empty id turns the comparison off) and returns the status after its first refresh. */
export async function saveLeadershipCohort(groupId: string): Promise<LeadershipCohortStatus> {
  const response = await apiFetch(BASE, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ groupId }),
  });
  if (!response.ok) throw await failure(response, 'saveFailed');
  return response.json() as Promise<LeadershipCohortStatus>;
}

export async function refreshLeadershipCohort(): Promise<LeadershipCohortStatus> {
  const response = await apiFetch(`${BASE}/refresh`, {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
  });
  if (!response.ok) throw await failure(response, 'refreshFailed');
  return response.json() as Promise<LeadershipCohortStatus>;
}
