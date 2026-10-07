import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n/catalog';
import { apiFetch } from './http';
import type {
  ActivityAnalysisAvailability,
  ActivityAnalysisPeople,
  ActivityAnalysisPeopleQuery,
  ActivityAnalysisQuery,
  ActivityAnalysisRange,
  ActivityAnalysisReport,
} from '../types/activityAnalysis';

const baseUrl = (): string => `${window.location.origin}/api/ActivityAnalysis`;

/**
 * How an Activity analysis request failed, so the page can react to the case rather than a status:
 *   - `badRequest`   400: a period, metric, range or user filter the server would not read.
 *   - `notInstalled` 412: the profiling tables this page reads do not exist on this deployment.
 *   - `busy`         503: another period is being loaded and the server's wait ran out. Retried
 *                         automatically (see `MAX_BUSY_RETRIES`) before it reaches the page.
 *   - `forbidden`    403: refused for a reason other than a portal permission - those arrive as the
 *                         shared `PortalPermissionError` from `apiFetch`.
 *   - `http`         anything else.
 */
export type ActivityAnalysisErrorKind = 'badRequest' | 'notInstalled' | 'busy' | 'forbidden' | 'http';

/** A typed failure from the Activity analysis API. Its message is always in the reader's language. */
export class ActivityAnalysisApiError extends Error {
  readonly kind: ActivityAnalysisErrorKind;
  readonly status: number;
  /** The server's stable code, when it sent one. */
  readonly code: string | null;

  constructor(kind: ActivityAnalysisErrorKind, status: number, code: string | null, message: string) {
    super(message);
    this.name = 'ActivityAnalysisApiError';
    this.kind = kind;
    this.status = status;
    this.code = code;
  }
}

/**
 * The codes `ActivityAnalysisAPIController` answers with, and the sentence for each. The server's own
 * `message` is English, so it is never shown: an English error on a Spanish page is what a reader sees
 * at the moment something has already gone wrong.
 */
export const ACTIVITY_ANALYSIS_ERROR_KEYS: ReadonlyMap<string, TranslationKey> = new Map<string, TranslationKey>([
  ['invalidPeriod', 'activityAnalysis.error.invalidPeriod'],
  ['invalidMetric', 'activityAnalysis.error.invalidMetric'],
  ['invalidRange', 'activityAnalysis.error.invalidRange'],
  ['invalidFilter', 'activityAnalysis.error.invalidFilter'],
  ['notInstalled', 'activityAnalysis.error.notInstalled'],
  ['activityAnalysisBusy', 'activityAnalysis.error.busy'],
  ['activityAnalysisFailed', 'activityAnalysis.error.failed'],
]);

/** The server's code for "another load is in progress"; answered with a Retry-After. */
const BUSY_CODE = 'activityAnalysisBusy';

/**
 * How many times a busy answer is retried before the reader is told. The server has already waited up
 * to 90 seconds for the other load before answering, so a couple of retries covers a load that was
 * nearly done without leaving the page spinning for minutes.
 */
export const MAX_BUSY_RETRIES = 2;

const DEFAULT_RETRY_SECONDS = 5;
const MAX_RETRY_SECONDS = 30;

type FailureKeys = { failed: TranslationKey };

const AVAILABILITY_KEYS: FailureKeys = { failed: 'activityAnalysis.error.availabilityFailed' };
const REPORT_KEYS: FailureKeys = { failed: 'activityAnalysis.error.reportFailed' };
const PEOPLE_KEYS: FailureKeys = { failed: 'activityAnalysis.error.peopleFailed' };

function kindForStatus(status: number): ActivityAnalysisErrorKind {
  switch (status) {
    case 400:
      return 'badRequest';
    case 412:
      return 'notInstalled';
    case 503:
      return 'busy';
    case 403:
      return 'forbidden';
    default:
      return 'http';
  }
}

async function readCode(response: Response): Promise<string | null> {
  try {
    const body = (await response.clone().json()) as { code?: unknown } | null;
    return typeof body?.code === 'string' && body.code.length > 0 ? body.code : null;
  } catch {
    return null;
  }
}

function fallbackMessage(kind: ActivityAnalysisErrorKind, status: number, keys: FailureKeys): string {
  switch (kind) {
    case 'badRequest':
      return translateActive('activityAnalysis.error.badRequest');
    case 'notInstalled':
      return translateActive('activityAnalysis.error.notInstalled');
    case 'busy':
      return translateActive('activityAnalysis.error.busy');
    case 'forbidden':
      return translateActive('activityAnalysis.error.forbidden');
    default:
      return translateActive(keys.failed, { status: String(status) });
  }
}

async function errorFor(response: Response, keys: FailureKeys): Promise<ActivityAnalysisApiError> {
  const code = await readCode(response);
  const kind = kindForStatus(response.status);
  const codeKey = code ? ACTIVITY_ANALYSIS_ERROR_KEYS.get(code) : undefined;
  const message = codeKey ? translateActive(codeKey) : fallbackMessage(kind, response.status, keys);
  return new ActivityAnalysisApiError(kind, response.status, code, message);
}

/** Seconds to wait before retrying, from the Retry-After header (seconds form only). */
function retryAfterSeconds(response: Response): number {
  const header = Number(response.headers.get('Retry-After'));
  if (!Number.isFinite(header) || header <= 0) return DEFAULT_RETRY_SECONDS;
  return Math.min(header, MAX_RETRY_SECONDS);
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

async function getJson<T>(path: string, keys: FailureKeys, signal?: AbortSignal): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    const response = await apiFetch(`${baseUrl()}${path}`, {
      method: 'GET',
      headers: { Accept: 'application/json' },
      signal,
    });

    if (response.ok) return response.json() as Promise<T>;

    if (response.status === 503 && attempt < MAX_BUSY_RETRIES && (await readCode(response)) === BUSY_CODE) {
      // Abortable, so a reader who changes the period meanwhile stops the retry rather than waiting it out.
      await delay(retryAfterSeconds(response) * 1000, signal);
      continue;
    }

    throw await errorFor(response, keys);
  }
}

/**
 * The query string, with `,` and `:` left as they are: they separate the keys and bounds of the lists
 * below, are legal in a query string, and reading `metrics=teams.calls,teams.meetings` in a network trace
 * beats reading `%2C` between every key. Everything else is encoded by URLSearchParams.
 */
function queryString(params: URLSearchParams): string {
  return params.toString().replace(/%2C/gi, ',').replace(/%3A/gi, ':');
}

/**
 * The activity ranges as the API reads them: `key:min:max`, comma-separated, with an open bound left
 * empty (`teams.calls:5:`, `outlook.emailsSent::100`). A range with neither bound is no range at all.
 * Null when nothing is left.
 */
export function serializeRanges(ranges: readonly ActivityAnalysisRange[] | null | undefined): string | null {
  const parts = (ranges ?? [])
    .filter((range) => range.min != null || range.max != null)
    .map((range) => `${range.metric}:${boundText(range.min)}:${boundText(range.max)}`);
  return parts.length > 0 ? parts.join(',') : null;
}

function boundText(value: number | null): string {
  // Whole numbers only: the totals the ranges compare with are counts and whole seconds.
  return value == null ? '' : String(Math.max(0, Math.round(value)));
}

/** The parameters a report request carries. */
export function reportParams(query: ActivityAnalysisQuery): URLSearchParams {
  const params = new URLSearchParams();
  params.set('from', query.from);
  params.set('to', query.to);
  params.set('metrics', query.metrics.join(','));
  if (query.userFilter) params.set('userFilter', query.userFilter);
  if (query.licences && query.licences.length > 0) params.set('licences', query.licences.join(','));
  const ranges = serializeRanges(query.ranges);
  if (ranges) params.set('ranges', ranges);
  return params;
}

/** The parameters a people request carries: the report's, narrowed to a department or to none. */
export function peopleParams(query: ActivityAnalysisPeopleQuery): URLSearchParams {
  const params = reportParams(query);
  if (query.noDepartment) params.set('noDepartment', 'true');
  else if (query.department != null) params.set('department', query.department);
  if (query.sort) params.set('sort', query.sort);
  if (query.top != null) params.set('top', String(query.top));
  return params;
}

/** Whether this deployment has the profiling data, its weeks, and the metrics it can offer. */
export function fetchActivityAnalysisAvailability(signal?: AbortSignal): Promise<ActivityAnalysisAvailability> {
  return getJson<ActivityAnalysisAvailability>('/availability', AVAILABILITY_KEYS, signal);
}

/**
 * The figures for a period, the selected metrics and the applied filters. Aggregates only, so any
 * signed-in reader may ask for everyone in the period; the server hides small groups from a reader
 * without See PII. Any filter - `userFilter`, `licences` or `ranges` - needs See PII and is refused
 * (403) without it, so never send one for a reader without it.
 */
export function fetchActivityAnalysisReport(query: ActivityAnalysisQuery, signal?: AbortSignal): Promise<ActivityAnalysisReport> {
  return getJson<ActivityAnalysisReport>(`/report?${queryString(reportParams(query))}`, REPORT_KEYS, signal);
}

/**
 * Named people - a department's members, or the most active people overall. Needs See PII: never call
 * it for a reader without it, because the server refuses it.
 */
export function fetchActivityAnalysisPeople(query: ActivityAnalysisPeopleQuery, signal?: AbortSignal): Promise<ActivityAnalysisPeople> {
  return getJson<ActivityAnalysisPeople>(`/people?${queryString(peopleParams(query))}`, PEOPLE_KEYS, signal);
}
