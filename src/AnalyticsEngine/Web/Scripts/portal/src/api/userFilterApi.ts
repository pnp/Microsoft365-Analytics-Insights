import { translateActive } from '../i18n/runtime';
import { apiFetch } from './http';
import type { UserFilterDimensionList, UserFilterValuePage } from '../types/userFilter';

/**
 * The user filter's picker API: which attributes a report can be filtered on, and the values each
 * one holds. Shared by every report that adopts the filter - the reports themselves only take the
 * serialised filter as a `userFilter` query parameter (see `serializeUserFilter`).
 */
const baseUrl = (): string => `${window.location.origin}/api/UserFilter`;

async function getJson<T>(url: string, failureKey: 'errors.userFilter.dimensionsFailed' | 'errors.userFilter.valuesFailed', signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(url, { method: 'GET', headers: { Accept: 'application/json' }, signal });
  if (!response.ok) {
    throw new Error(translateActive(failureKey, { status: response.status }));
  }
  return response.json() as Promise<T>;
}

export function fetchUserFilterDimensions(signal?: AbortSignal): Promise<UserFilterDimensionList> {
  return getJson<UserFilterDimensionList>(`${baseUrl()}/dimensions`, 'errors.userFilter.dimensionsFailed', signal);
}

/** One dimension's values, largest first. `search` matches anywhere in the value. */
export function fetchUserFilterValues(
  dimension: string,
  search: string,
  take: number,
  signal?: AbortSignal,
): Promise<UserFilterValuePage> {
  const params = new URLSearchParams({ dimension, take: String(take) });
  if (search.trim()) params.set('search', search.trim());
  return getJson<UserFilterValuePage>(`${baseUrl()}/values?${params}`, 'errors.userFilter.valuesFailed', signal);
}
