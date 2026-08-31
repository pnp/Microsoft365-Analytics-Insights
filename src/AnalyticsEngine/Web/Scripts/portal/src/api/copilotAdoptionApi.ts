import { apiFetch } from './http';
import type {
  AdoptionFilterOptions,
  CopilotAdoptionAvailability,
  CopilotAdoptionSummary,
  LicenceOpportunityPage,
  LicensedUserFilters,
  LicensedUserPage,
  OpportunityFilters,
} from '../types/copilotAdoption';

const baseUrl = (): string =>
  window.o365AnalyticsCopilotAdoptionAPI ?? `${window.location.origin}/api/CopilotAdoption`;

/**
 * HTTP 202 from any adoption endpoint means "the analysis is still running, poll me".
 *
 * The analysis is a single shared, cached computation on the server, and on a large tenant it can
 * legitimately take longer than the ~230s at which Azure App Service kills a request - so the server
 * answers 202 rather than holding the connection open until the platform drops it. See issue #360.
 */
const STILL_BUILDING = 202;

/** Fallback poll interval when the server doesn't say. The server normally supplies one. */
const DEFAULT_RETRY_SECONDS = 5;

/**
 * How long the client keeps polling before giving up. Generous, because the first analysis on a large
 * tenant is genuinely slow, but bounded so a permanently broken backend cannot spin for ever.
 */
const POLL_CEILING_MS = 10 * 60 * 1000;

const delay = (ms: number, signal?: AbortSignal): Promise<void> =>
  new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMException('Aborted', 'AbortError'));
      return;
    }

    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);

    const onAbort = (): void => {
      clearTimeout(timer);
      reject(new DOMException('Aborted', 'AbortError'));
    };

    signal?.addEventListener('abort', onAbort, { once: true });
  });

async function getJson<T>(path: string, what: string, signal?: AbortSignal): Promise<T> {
  const giveUpAt = Date.now() + POLL_CEILING_MS;

  for (;;) {
    const response = await apiFetch(`${baseUrl()}${path}`, {
      method: 'GET',
      headers: { Accept: 'application/json' },
      signal,
    });

    if (response.status === STILL_BUILDING) {
      // Deliberately NOT treated as success: 202 is 2xx, so `response.ok` is true and parsing it as the
      // model would hand the page a "building" envelope where it expected data.
      const body = (await response.json().catch(() => null)) as { retryAfterSeconds?: number } | null;
      const waitMs = (body?.retryAfterSeconds ?? DEFAULT_RETRY_SECONDS) * 1000;

      if (Date.now() + waitMs >= giveUpAt) {
        throw new Error(
          `The Copilot adoption analysis is taking longer than expected and hasn't finished yet. ` +
            `It is still running on the server - reload the page in a few minutes.`,
        );
      }

      // Abortable, so unmounting the page or changing the period actually STOPS the loop. A plain
      // `cancelled` flag would only suppress the state update and leave this polling for ten minutes.
      await delay(waitMs, signal);
      continue;
    }

    if (!response.ok) {
      throw new Error(`Couldn't load ${what} (${response.status}).`);
    }

    return response.json() as Promise<T>;
  }
}

/** Common query parameters: every endpoint is scoped by the window and the seat-licence selection. */
function scopeParams(windowDays: number, seatLicenceTypeIds?: number[]): URLSearchParams {
  const params = new URLSearchParams({ windowDays: String(windowDays) });
  if (seatLicenceTypeIds && seatLicenceTypeIds.length > 0) {
    params.set('seatLicenceTypeIds', seatLicenceTypeIds.join(','));
  }
  return params;
}

/** Adds the licensed-user filter state to a parameter set. */
function applyLicensedUserFilters(params: URLSearchParams, filters: LicensedUserFilters): URLSearchParams {
  if (filters.search.trim()) params.set('search', filters.search.trim());
  if (filters.bands.length > 0) params.set('bands', filters.bands.join(','));
  if (filters.actions.length > 0) params.set('actions', filters.actions.join(','));
  if (filters.department) params.set('department', filters.department);
  if (filters.country) params.set('country', filters.country);
  if (filters.coworkOnly) params.set('coworkOnly', 'true');
  if (filters.disabledOnly) params.set('disabledOnly', 'true');
  params.set('sortBy', filters.sortBy);
  params.set('sortDesc', String(filters.sortDesc));
  return params;
}

/** Adds the licence-opportunity filter state to a parameter set. */
function applyOpportunityFilters(params: URLSearchParams, filters: OpportunityFilters): URLSearchParams {
  if (filters.search.trim()) params.set('search', filters.search.trim());
  if (filters.department) params.set('department', filters.department);
  if (filters.country) params.set('country', filters.country);
  if (filters.recommendedOnly) params.set('recommendedOnly', 'true');
  if (filters.existingCopilotUsersOnly) params.set('existingCopilotUsersOnly', 'true');
  params.set('sortBy', filters.sortBy);
  params.set('sortDesc', String(filters.sortDesc));
  return params;
}

export function fetchAdoptionAvailability(): Promise<CopilotAdoptionAvailability> {
  return getJson<CopilotAdoptionAvailability>('/availability', 'the Copilot adoption availability');
}

export function fetchAdoptionSummary(
  windowDays: number,
  seatLicenceTypeIds?: number[],
  signal?: AbortSignal,
): Promise<CopilotAdoptionSummary> {
  return getJson<CopilotAdoptionSummary>(
    `/summary?${scopeParams(windowDays, seatLicenceTypeIds)}`,
    'the Copilot adoption summary',
    signal,
  );
}

export function fetchAdoptionFilters(
  windowDays: number,
  seatLicenceTypeIds?: number[],
  signal?: AbortSignal,
): Promise<AdoptionFilterOptions> {
  return getJson<AdoptionFilterOptions>(
    `/filters?${scopeParams(windowDays, seatLicenceTypeIds)}`,
    'the Copilot adoption filters',
    signal,
  );
}

export function fetchLicensedUsers(
  windowDays: number,
  filters: LicensedUserFilters,
  skip: number,
  take: number,
  seatLicenceTypeIds?: number[],
  signal?: AbortSignal,
): Promise<LicensedUserPage> {
  const params = applyLicensedUserFilters(scopeParams(windowDays, seatLicenceTypeIds), filters);
  params.set('skip', String(skip));
  params.set('take', String(take));

  return getJson<LicensedUserPage>(`/licensed-users?${params}`, 'the licensed Copilot users', signal);
}

export function fetchOpportunities(
  windowDays: number,
  filters: OpportunityFilters,
  skip: number,
  take: number,
  seatLicenceTypeIds?: number[],
  signal?: AbortSignal,
): Promise<LicenceOpportunityPage> {
  const params = applyOpportunityFilters(scopeParams(windowDays, seatLicenceTypeIds), filters);
  params.set('skip', String(skip));
  params.set('take', String(take));

  return getJson<LicenceOpportunityPage>(`/opportunities?${params}`, 'the Copilot licence opportunities', signal);
}

export function fetchAdoptionSql(
  windowDays: number,
  seatLicenceTypeIds?: number[],
  signal?: AbortSignal,
): Promise<Record<string, string>> {
  return getJson<Record<string, string>>(
    `/sql?${scopeParams(windowDays, seatLicenceTypeIds)}`,
    'the Copilot adoption queries',
    signal,
  );
}

/**
 * URL of the CSV export for the current filters.
 *
 * Returned as a URL for a plain link rather than fetched and turned into a blob, so the browser's
 * own download UI handles it and the session cookie is sent automatically. The export takes the
 * same filter parameters as the list, so the file always matches what is on screen.
 */
export function licensedUsersExportUrl(
  windowDays: number,
  filters: LicensedUserFilters,
  seatLicenceTypeIds?: number[],
): string {
  const params = applyLicensedUserFilters(scopeParams(windowDays, seatLicenceTypeIds), filters);
  return `${baseUrl()}/licensed-users/export?${params}`;
}

export function opportunitiesExportUrl(
  windowDays: number,
  filters: OpportunityFilters,
  seatLicenceTypeIds?: number[],
): string {
  const params = applyOpportunityFilters(scopeParams(windowDays, seatLicenceTypeIds), filters);
  return `${baseUrl()}/opportunities/export?${params}`;
}

/**
 * URL of the full Excel workbook export.
 *
 * The whole report - every figure, table and chart on the page - in one .xlsx, built from the same
 * cached analysis the screen is rendered from. Its purpose is the point-in-time snapshot: run it
 * before an enablement programme starts and again afterwards, and the two files are directly
 * comparable in a way a screenshot never is.
 */
export function workbookExportUrl(windowDays: number, seatLicenceTypeIds?: number[]): string {
  return `${baseUrl()}/export/workbook?${scopeParams(windowDays, seatLicenceTypeIds)}`;
}
