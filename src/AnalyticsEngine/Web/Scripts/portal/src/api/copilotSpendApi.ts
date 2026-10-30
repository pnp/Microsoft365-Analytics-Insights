import { apiFetch } from './http';
import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n/catalog';
import type { Language } from '../i18n/languages';
import type { CopilotSpendSummary, SpendPeopleFilters, SpendPeoplePage, SpendPersonDetail, SpendTrend } from '../types/copilotSpend';

const BASE = 'api/CopilotSpend';

/** What one request asks for: a rolling window, the reader's own filter and a grouping. There are no prices to send. */
export interface SpendQuery {
  windowDays: number;
  userFilter?: string | null;
  dimension?: string | null;
}

async function getJson<T>(path: string, failureKey: TranslationKey, signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(path, { method: 'GET', headers: { Accept: 'application/json' }, signal });
  if (!response.ok) {
    const body = (await response.clone().json().catch(() => null)) as { code?: unknown } | null;
    if (body?.code === 'personNotFound') throw new Error(translateActive('copilotSpend.error.personNotFound'));
    throw new Error(translateActive(failureKey, { status: response.status }));
  }
  return response.json() as Promise<T>;
}

export function spendParams(query: SpendQuery): URLSearchParams {
  const params = new URLSearchParams({ windowDays: String(query.windowDays) });
  if (query.userFilter) params.set('userFilter', query.userFilter);
  if (query.dimension) params.set('dimension', query.dimension);
  return params;
}

export function fetchSpendSummary(query: SpendQuery, signal?: AbortSignal): Promise<CopilotSpendSummary> {
  return getJson(`${BASE}/summary?${spendParams(query)}`, 'copilotSpend.error.summaryFailed', signal);
}

export function fetchSpendTrend(query: SpendQuery, signal?: AbortSignal): Promise<SpendTrend> {
  const params = spendParams({ windowDays: query.windowDays, userFilter: query.userFilter });
  return getJson(`${BASE}/trend?${params}`, 'copilotSpend.error.trendFailed', signal);
}

export function fetchSpendPeople(query: SpendQuery, filters: SpendPeopleFilters, skip: number, take: number, signal?: AbortSignal): Promise<SpendPeoplePage> {
  const params = spendParams({ windowDays: query.windowDays, userFilter: query.userFilter });
  if (filters.search.trim()) params.set('search', filters.search.trim());
  if (filters.origin !== 'all') params.set('source', filters.origin);
  params.set('sortBy', filters.sortBy);
  params.set('sortDesc', String(filters.sortDesc));
  params.set('skip', String(skip));
  params.set('take', String(take));
  return getJson(`${BASE}/people?${params}`, 'copilotSpend.error.peopleFailed', signal);
}

export function fetchSpendPerson(query: SpendQuery, userId: number, signal?: AbortSignal): Promise<SpendPersonDetail> {
  const params = spendParams({ windowDays: query.windowDays, userFilter: query.userFilter });
  return getJson(`${BASE}/person/${encodeURIComponent(String(userId))}?${params}`, 'copilotSpend.error.personFailed', signal);
}

/** A plain link, so the browser's own download handles it, on the page's scope and in the reader's language. */
export function spendWorkbookUrl(query: SpendQuery, language: Language): string {
  const params = spendParams(query);
  params.set('language', language);
  return `${BASE}/export/workbook?${params}`;
}
