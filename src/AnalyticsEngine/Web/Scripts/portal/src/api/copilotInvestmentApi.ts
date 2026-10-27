import { apiFetch } from './http';
import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n/catalog';
import type {
  CopilotInvestmentAvailability,
  CopilotInvestmentSummary,
  InvestmentAssumptions,
  InvestmentAssumptionsChange,
  InvestmentPeopleFilters,
  InvestmentPeoplePage,
  InvestmentPersonDetail,
  InvestmentScenarioOverrides,
  InvestmentTrend,
} from '../types/copilotInvestment';

const BASE = 'api/CopilotInvestment';
const SETTINGS_BASE = 'api/CopilotInvestmentSettings';

/** The server is still building the shared Copilot evidence behind the report; poll. */
const STILL_BUILDING = 202;
const DEFAULT_RETRY_SECONDS = 5;
const POLL_CEILING_MS = 10 * 60 * 1000;

/** What one request asks for: a rolling window, the reader's own filter, a grouping and any what-if figures. */
export interface InvestmentQuery {
  windowDays: number;
  userFilter?: string | null;
  dimension?: string | null;
  scenario?: InvestmentScenarioOverrides | null;
}

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

/** A refusal the page can word: `scenarioInvalid` names the what-if fields that broke a rule. */
export class InvestmentRequestError extends Error {
  readonly code: string;
  readonly validationErrors: string[];

  constructor(code: string, message: string, validationErrors: string[] = []) {
    super(message);
    this.name = 'InvestmentRequestError';
    this.code = code;
    this.validationErrors = validationErrors;
  }
}

async function getJson<T>(path: string, failureKey: TranslationKey, signal?: AbortSignal): Promise<T> {
  const giveUpAt = Date.now() + POLL_CEILING_MS;
  for (;;) {
    const response = await apiFetch(path, { method: 'GET', headers: { Accept: 'application/json' }, signal });
    if (response.status === STILL_BUILDING) {
      // 202 is 2xx: treated as "wait", never parsed as the report.
      const body = (await response.json().catch(() => null)) as { retryAfterSeconds?: number } | null;
      const waitMs = (body?.retryAfterSeconds ?? DEFAULT_RETRY_SECONDS) * 1000;
      if (Date.now() + waitMs >= giveUpAt) throw new Error(translateActive('copilotInvestment.error.stillBuilding'));
      await delay(waitMs, signal);
      continue;
    }
    if (!response.ok) {
      const body = (await response.clone().json().catch(() => null)) as { code?: unknown; validationErrors?: unknown } | null;
      const code = typeof body?.code === 'string' ? body.code : null;
      if (code === 'scenarioInvalid') {
        const fields = Array.isArray(body?.validationErrors) ? body.validationErrors.filter((v): v is string => typeof v === 'string') : [];
        throw new InvestmentRequestError(code, translateActive('copilotInvestment.error.scenarioInvalid'), fields);
      }
      if (code === 'personNotFound') throw new InvestmentRequestError(code, translateActive('copilotInvestment.error.personNotFound'));
      throw new Error(translateActive(failureKey, { status: response.status }));
    }
    return response.json() as Promise<T>;
  }
}

const SCENARIO_PARAMS: (keyof InvestmentScenarioOverrides)[] = [
  'currency',
  'seatPrice',
  'creditPrice',
  'studioCreditPrice',
  'hourlyCost',
  'minutesLow',
  'minutesBase',
  'minutesHigh',
  'realisationLow',
  'realisationBase',
  'realisationHigh',
];

/**
 * The query string every endpoint takes. What-if numbers are written with `String(number)`, which is
 * culture-free ("0.5", never "0,5"): the server parses them with the invariant culture.
 */
export function queryParams(query: InvestmentQuery): URLSearchParams {
  const params = new URLSearchParams({ windowDays: String(query.windowDays) });
  if (query.userFilter) params.set('userFilter', query.userFilter);
  if (query.dimension) params.set('dimension', query.dimension);
  for (const name of SCENARIO_PARAMS) {
    const value = query.scenario?.[name];
    if (value === undefined || value === null || value === '') continue;
    params.set(name, typeof value === 'number' ? String(value) : value);
  }
  return params;
}

export function fetchInvestmentAvailability(signal?: AbortSignal): Promise<CopilotInvestmentAvailability> {
  return getJson(`${BASE}/availability`, 'copilotInvestment.error.availabilityFailed', signal);
}

export function fetchInvestmentSummary(query: InvestmentQuery, signal?: AbortSignal): Promise<CopilotInvestmentSummary> {
  return getJson(`${BASE}/summary?${queryParams(query)}`, 'copilotInvestment.error.summaryFailed', signal);
}

export function fetchInvestmentTrend(query: InvestmentQuery, signal?: AbortSignal): Promise<InvestmentTrend> {
  const params = queryParams({ windowDays: query.windowDays, userFilter: query.userFilter });
  return getJson(`${BASE}/trend?${params}`, 'copilotInvestment.error.trendFailed', signal);
}

export function fetchInvestmentPeople(
  query: InvestmentQuery,
  filters: InvestmentPeopleFilters,
  skip: number,
  take: number,
  signal?: AbortSignal,
): Promise<InvestmentPeoplePage> {
  const params = queryParams(query);
  if (filters.search.trim()) params.set('search', filters.search.trim());
  if (filters.signals.length > 0) params.set('signals', filters.signals.join(','));
  if (filters.flag) params.set('flag', filters.flag);
  if (filters.seat !== 'all') params.set('seat', filters.seat);
  params.set('sortBy', filters.sortBy);
  params.set('sortDesc', String(filters.sortDesc));
  params.set('skip', String(skip));
  params.set('take', String(take));
  return getJson(`${BASE}/people?${params}`, 'copilotInvestment.error.peopleFailed', signal);
}

export function fetchInvestmentPerson(query: InvestmentQuery, userId: number, signal?: AbortSignal): Promise<InvestmentPersonDetail> {
  return getJson(`${BASE}/person/${encodeURIComponent(String(userId))}?${queryParams(query)}`, 'copilotInvestment.error.personFailed', signal);
}

/** A plain link, so the browser's own download handles it; it carries the same scope and what-if figures as the page. */
export function investmentWorkbookUrl(query: InvestmentQuery): string {
  return `${BASE}/export/workbook?${queryParams(query)}`;
}

/** Every code the assumptions API can answer with (`CopilotInvestmentErrorCodes`), plus two client fallbacks. */
export const COPILOT_INVESTMENT_SETTINGS_ERROR_KEYS: ReadonlyMap<string, TranslationKey> = new Map<string, TranslationKey>([
  ['invalidRequest', 'copilotInvestment.settings.error.invalidRequest'],
  ['versionConflict', 'copilotInvestment.settings.error.versionConflict'],
  ['storageNotConfigured', 'copilotInvestment.settings.error.storageNotConfigured'],
  ['stateUnavailable', 'copilotInvestment.settings.error.stateUnavailable'],
  ['currencyInvalid', 'copilotInvestment.settings.error.currencyInvalid'],
  ['currencyRequired', 'copilotInvestment.settings.error.currencyRequired'],
  ['seatPriceOutOfRange', 'copilotInvestment.settings.error.seatPriceOutOfRange'],
  ['creditPriceOutOfRange', 'copilotInvestment.settings.error.creditPriceOutOfRange'],
  ['studioCreditPriceOutOfRange', 'copilotInvestment.settings.error.studioCreditPriceOutOfRange'],
  ['hourlyCostOutOfRange', 'copilotInvestment.settings.error.hourlyCostOutOfRange'],
  ['minutesOutOfRange', 'copilotInvestment.settings.error.minutesOutOfRange'],
  ['minutesIncomplete', 'copilotInvestment.settings.error.minutesIncomplete'],
  ['minutesOrder', 'copilotInvestment.settings.error.minutesOrder'],
  ['realisationOutOfRange', 'copilotInvestment.settings.error.realisationOutOfRange'],
  ['realisationIncomplete', 'copilotInvestment.settings.error.realisationIncomplete'],
  ['realisationOrder', 'copilotInvestment.settings.error.realisationOrder'],
  ['noteTooLong', 'copilotInvestment.settings.error.noteTooLong'],
  ['scenarioInvalid', 'copilotInvestment.settings.error.scenarioInvalid'],
  ['loadFailed', 'copilotInvestment.settings.error.loadFailed'],
  ['saveFailed', 'copilotInvestment.settings.error.saveFailed'],
]);

export class InvestmentSettingsError extends Error {
  readonly code: string;
  readonly validationErrors: string[];

  constructor(code: string, validationErrors: string[]) {
    super(code);
    this.name = 'InvestmentSettingsError';
    this.code = code;
    this.validationErrors = validationErrors;
  }
}

export interface InvestmentSettingsModel {
  durable: boolean;
  version: number;
  assumptions: InvestmentAssumptions;
  updatedBy: string | null;
  updatedUtc: string | null;
  history: InvestmentAssumptionsChange[];
}

async function settingsFailure(response: Response, fallback: string): Promise<InvestmentSettingsError> {
  const body = (await response.json().catch(() => null)) as { code?: unknown; validationErrors?: unknown } | null;
  const code = typeof body?.code === 'string' ? body.code : fallback;
  const errors = Array.isArray(body?.validationErrors) ? body.validationErrors.filter((e): e is string => typeof e === 'string') : [];
  return new InvestmentSettingsError(code, errors);
}

export async function fetchInvestmentSettings(): Promise<InvestmentSettingsModel> {
  const response = await apiFetch(SETTINGS_BASE);
  if (!response.ok) throw await settingsFailure(response, 'loadFailed');
  return response.json() as Promise<InvestmentSettingsModel>;
}

export async function saveInvestmentSettings(expectedVersion: number, assumptions: InvestmentAssumptions): Promise<InvestmentSettingsModel> {
  const response = await apiFetch(SETTINGS_BASE, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ expectedVersion, assumptions }),
  });
  if (!response.ok) throw await settingsFailure(response, 'saveFailed');
  return response.json() as Promise<InvestmentSettingsModel>;
}
