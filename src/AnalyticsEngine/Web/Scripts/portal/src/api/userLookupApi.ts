import { apiFetch } from './http';
import type { UserDataSummary, UserDataDetailResponse } from '../types/userData';

const baseUrl = (): string =>
  window.o365AnalyticsUserLookupAPI ?? `${window.location.origin}/api/UserDataLookup`;

async function getJson<T>(url: string): Promise<T> {
  const response = await apiFetch(url, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    let message = `Request failed (${response.status})`;
    try {
      const body = await response.json();
      if (body && typeof body.message === 'string') {
        message = body.message;
      }
    } catch {
      /* response had no JSON body */
    }
    throw new Error(message);
  }

  return response.json() as Promise<T>;
}

/** Fetch the profile + per-category counts for a user by UPN. */
export function fetchUserSummary(upn: string): Promise<UserDataSummary> {
  const url = `${baseUrl()}/summary?upn=${encodeURIComponent(upn)}`;
  return getJson<UserDataSummary>(url);
}

/** Fetch the most recent rows for one category for a user by UPN. */
export function fetchUserDetail(
  upn: string,
  category: string,
  take = 50,
): Promise<UserDataDetailResponse> {
  const url =
    `${baseUrl()}/detail?upn=${encodeURIComponent(upn)}` +
    `&category=${encodeURIComponent(category)}&take=${encodeURIComponent(String(take))}`;
  return getJson<UserDataDetailResponse>(url);
}
