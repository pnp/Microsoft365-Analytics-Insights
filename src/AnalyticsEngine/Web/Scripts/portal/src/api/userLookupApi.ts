import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n/catalog';
import { apiFetch } from './http';
import type { UserDataSummary, UserDataDetailResponse } from '../types/userData';

const baseUrl = (): string =>
  window.o365AnalyticsUserLookupAPI ?? `${window.location.origin}/api/UserDataLookup`;

const ERROR_CODE_KEYS: Record<string, TranslationKey> = {
  missingUpn: 'errors.userLookup.missingUpn',
  unknownCategory: 'errors.userLookup.unknownCategory',
  categoryNoDrilldown: 'errors.userLookup.categoryNoDrilldown',
};

async function getJson<T>(url: string): Promise<T> {
  const response = await apiFetch(url, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    let message = translateActive('errors.userLookup.requestFailed', { status: response.status });
    try {
      const body = await response.json() as { code?: unknown; message?: unknown; category?: unknown } | null;
      if (response.status === 404 && body?.code === 'userNotFound') {
        message = translateActive('errors.userLookup.notFound');
      } else if (response.status === 400 && typeof body?.code === 'string' && ERROR_CODE_KEYS[body.code]) {
        message = translateActive(ERROR_CODE_KEYS[body.code], {
          category: typeof body.category === 'string' ? body.category : '',
        });
      } else if (response.status !== 404 && body && typeof body.message === 'string') {
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
