import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n/catalog';
import { apiFetch } from './http';
import { filenameFromResponse, saveBlob } from './download';
import type {
  WebActivityAvailability,
  WebActivityExportSection,
  WebActivityGeography,
  WebActivityJourneys,
  WebActivityOverview,
  WebActivityPages,
  WebActivitySearch,
  WebActivityTechnology,
  WebActivityVisits,
} from '../types/webActivity';

const baseUrl = (): string => `${window.location.origin}/api/WebActivity`;

const WEB_ACTIVITY_EXPORT_FAILURE_KEYS: Record<WebActivityExportSection, TranslationKey> = {
  pages: 'errors.webActivity.exportPagesFailed',
  'quiet-pages': 'errors.webActivity.exportQuietPagesFailed',
  'slow-pages': 'errors.webActivity.exportSlowPagesFailed',
  'entry-pages': 'errors.webActivity.exportEntryPagesFailed',
  'exit-pages': 'errors.webActivity.exportExitPagesFailed',
  transitions: 'errors.webActivity.exportTransitionsFailed',
  flows: 'errors.webActivity.exportFlowsFailed',
  'search-terms': 'errors.webActivity.exportSearchTermsFailed',
  technology: 'errors.webActivity.exportTechnologyFailed',
};

async function getJson<T>(path: string, failureKey: TranslationKey, signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(`${baseUrl()}/${path}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    signal,
  });

  if (!response.ok) {
    throw new Error(translateActive(failureKey, { status: response.status }));
  }

  return response.json() as Promise<T>;
}

/** Which web-traffic sources are switched on, and what to tell the admin about the ones that are not. */
export function fetchWebActivityAvailability(signal?: AbortSignal): Promise<WebActivityAvailability> {
  return getJson<WebActivityAvailability>('availability', 'errors.webActivity.dataSourcesFailed', signal);
}

export function fetchWebActivityOverview(days: number, signal?: AbortSignal): Promise<WebActivityOverview> {
  return getJson<WebActivityOverview>(`overview?days=${days}`, 'errors.webActivity.overviewFailed', signal);
}

export function fetchWebActivityVisits(days: number, signal?: AbortSignal): Promise<WebActivityVisits> {
  return getJson<WebActivityVisits>(`visits?days=${days}`, 'errors.webActivity.visitsFailed', signal);
}

export function fetchWebActivityPages(days: number, signal?: AbortSignal): Promise<WebActivityPages> {
  return getJson<WebActivityPages>(`pages?days=${days}`, 'errors.webActivity.pagesFailed', signal);
}

export function fetchWebActivityJourneys(days: number, signal?: AbortSignal): Promise<WebActivityJourneys> {
  return getJson<WebActivityJourneys>(`journeys?days=${days}`, 'errors.webActivity.journeysFailed', signal);
}

export function fetchWebActivityGeography(days: number, signal?: AbortSignal): Promise<WebActivityGeography> {
  return getJson<WebActivityGeography>(`geography?days=${days}`, 'errors.webActivity.geographyFailed', signal);
}

export function fetchWebActivitySearch(days: number, signal?: AbortSignal): Promise<WebActivitySearch> {
  return getJson<WebActivitySearch>(`search?days=${days}`, 'errors.webActivity.searchFailed', signal);
}

export function fetchWebActivityTechnology(days: number, signal?: AbortSignal): Promise<WebActivityTechnology> {
  return getJson<WebActivityTechnology>(`technology?days=${days}`, 'errors.webActivity.technologyFailed', signal);
}

/**
 * Downloads one section as CSV.
 *
 * Fetched and saved as a blob rather than pointed at with a plain `<a href>`, for the same reason
 * the Teams Explorer and Licence activity exports are: a plain link cannot send the
 * `X-Requested-With` header `apiFetch` uses, so an expired session would answer with a redirect to
 * the sign-in page and the browser would helpfully save that HTML as a `.csv`.
 */
export async function downloadWebActivityExport(
  section: WebActivityExportSection,
  days: number,
  top = 500,
  signal?: AbortSignal,
): Promise<void> {
  const qs = new URLSearchParams({ days: String(days), top: String(top) });

  const response = await apiFetch(`${baseUrl()}/export/${section}?${qs}`, {
    method: 'GET',
    headers: { Accept: 'text/csv' },
    signal,
  });

  if (!response.ok) {
    throw new Error(translateActive(WEB_ACTIVITY_EXPORT_FAILURE_KEYS[section], { status: response.status }));
  }

  const blob = await response.blob();
  saveBlob(blob, filenameFromResponse(response) ?? `web-activity-${section}.csv`);
}
