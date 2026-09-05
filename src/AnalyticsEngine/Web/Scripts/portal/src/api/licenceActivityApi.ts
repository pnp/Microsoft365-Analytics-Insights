import { apiFetch } from './http';
import type {
  LicenceActivityAvailability,
  LicenceActivityOverview,
  LicenceActivityUsers,
  OverviewParams,
  UsersParams,
} from '../types/licenceActivity';

const baseUrl = (): string =>
  window.o365AnalyticsLicenceActivityAPI ?? `${window.location.origin}/api/LicenceActivity`;

/**
 * How a Licence-activity request failed, so callers can react to the case rather than a raw status.
 *   - `busy`       503: the query hit its finite timeout on a large tenant. This tool does NOT poll
 *                       (a bounded, killable query is simpler than a long-poll); the UI offers a
 *                       manual "Try again".
 *   - `expired`    410/409/404: the cached snapshot the request referenced is gone, superseded, or no
 *                       longer contains that licence. The caller must NOT silently re-query - it must
 *                       tell the user to refresh so the exported file always matches the screen.
 *   - `forbidden`  403: the user lacks the LicenceActivity.ReadUsers role.
 *   - `precondition` 412: the licence import (GraphUsersMetadata) is disabled.
 *   - `badRequest` 400: an invalid parameter (e.g. a range outside 7..180 days).
 *   - `http`       anything else.
 */
export type LicenceActivityErrorKind =
  | 'busy'
  | 'expired'
  | 'forbidden'
  | 'precondition'
  | 'badRequest'
  | 'http';

/** A typed failure from the Licence-activity API, carrying the server's own message when it sent one. */
export class LicenceActivityApiError extends Error {
  readonly kind: LicenceActivityErrorKind;
  readonly status: number;

  constructor(kind: LicenceActivityErrorKind, status: number, message: string) {
    super(message);
    this.name = 'LicenceActivityApiError';
    this.kind = kind;
    this.status = status;
  }
}

function kindForStatus(status: number): LicenceActivityErrorKind {
  switch (status) {
    case 503:
      return 'busy';
    case 410:
    case 409:
    case 404:
      return 'expired';
    case 403:
      return 'forbidden';
    case 412:
      return 'precondition';
    case 400:
      return 'badRequest';
    default:
      return 'http';
  }
}

function fallbackMessage(kind: LicenceActivityErrorKind, status: number, what: string): string {
  switch (kind) {
    case 'busy':
      return `The server is busy and ${what} timed out. Try again in a moment, or narrow the date range.`;
    case 'expired':
      return `This snapshot has expired or was refreshed. Reload to get a current one.`;
    case 'forbidden':
      return `You do not have permission to view ${what}. Individual user detail needs the "LicenceActivity.ReadUsers" role.`;
    case 'precondition':
      return `Licence activity is not available: the licence import is disabled on this deployment.`;
    case 'badRequest':
      return `That request was rejected. Check the selected dates and filters.`;
    default:
      return `Couldn't load ${what} (${status}).`;
  }
}

/** Reads the server's `{ message }` error body without consuming the original response. */
async function readServerMessage(response: Response): Promise<string | null> {
  try {
    const body = (await response.clone().json()) as { message?: unknown } | null;
    return body && typeof body.message === 'string' ? body.message : null;
  } catch {
    return null;
  }
}

/** Turns a non-OK response into a typed error, preferring the server's message. */
async function errorFor(response: Response, what: string): Promise<LicenceActivityApiError> {
  const kind = kindForStatus(response.status);
  const message = (await readServerMessage(response)) ?? fallbackMessage(kind, response.status, what);
  return new LicenceActivityApiError(kind, response.status, message);
}

async function getJson<T>(path: string, what: string, signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(`${baseUrl()}${path}`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    signal,
  });

  if (!response.ok) {
    throw await errorFor(response, what);
  }

  return response.json() as Promise<T>;
}

/** Which parts of the tool this deployment / signed-in user can see, and the allowed window bounds. */
export function fetchAvailability(signal?: AbortSignal): Promise<LicenceActivityAvailability> {
  return getJson<LicenceActivityAvailability>('/availability', 'the licence activity availability', signal);
}

function overviewQuery(params: OverviewParams): URLSearchParams {
  const qs = new URLSearchParams({ from: params.from, to: params.to });
  // == null covers null and undefined; 0 is a valid demographic id ("unknown" bucket) and must survive.
  if (params.departmentId != null) qs.set('departmentId', String(params.departmentId));
  if (params.countryId != null) qs.set('countryId', String(params.countryId));
  return qs;
}

/** The executive overview: SKU assignments, five workload distributions and per-workload coverage. */
export function fetchOverview(params: OverviewParams, signal?: AbortSignal): Promise<LicenceActivityOverview> {
  return getJson<LicenceActivityOverview>(
    `/overview?${overviewQuery(params)}`,
    'the licence activity overview',
    signal,
  );
}

function usersQuery(params: UsersParams): URLSearchParams {
  const qs = new URLSearchParams({
    overviewId: params.overviewId,
    licenceTypeId: String(params.licenceTypeId),
    workload: params.workload,
    top: String(params.top),
    sort: params.sort,
    direction: params.direction,
    page: String(params.page),
    pageSize: String(params.pageSize),
  });
  if (params.search && params.search.trim()) qs.set('search', params.search.trim());
  return qs;
}

/**
 * The users snapshot for the selected licence: one request returns the bounded most/least active
 * lists and the current browse page together. Individual user data needs the
 * `LicenceActivity.ReadUsers` role; a caller without it gets a `forbidden` error.
 */
export function fetchUsers(params: UsersParams, signal?: AbortSignal): Promise<LicenceActivityUsers> {
  return getJson<LicenceActivityUsers>(`/users?${usersQuery(params)}`, 'the licensed users', signal);
}

/** Pulls a filename out of a Content-Disposition header, if the server set one. */
function filenameFromResponse(response: Response): string | null {
  const header = response.headers.get('Content-Disposition');
  if (!header) return null;
  const utf8 = /filename\*=UTF-8''([^;]+)/i.exec(header);
  if (utf8?.[1]) return decodeURIComponent(utf8[1]);
  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain?.[1] ?? null;
}

/** Saves a blob to disk via a transient object URL (download without navigating). */
function saveBlob(blob: Blob, filename: string): void {
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = filename;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}

export interface ExportParams {
  /** The overview snapshot to export. Required - the export is always at least the aggregate. */
  overviewId: string;
  /** The current users snapshot. Omitted for an aggregate-only workbook. */
  usersId?: string;
}

/**
 * Downloads the Excel snapshot of the current report.
 *
 * Fetched and saved as a blob rather than pointed at with a plain `<a href>` DELIBERATELY: the server
 * answers 410 when a referenced snapshot has expired and 409 when the users and overview snapshots no
 * longer match, and we must surface that as "refresh the page" instead of letting the browser show a
 * raw error. The backend builds the workbook from the EXACT cached overview (and, when `usersId` is
 * given, the exact cached user rows) - it does not re-run the analysis. Omit `usersId` for an
 * aggregate-only export.
 */
export async function downloadExport(params: ExportParams, signal?: AbortSignal): Promise<void> {
  const qs = new URLSearchParams({ overviewId: params.overviewId });
  if (params.usersId) qs.set('usersId', params.usersId);

  const response = await apiFetch(`${baseUrl()}/export?${qs}`, {
    method: 'GET',
    headers: { Accept: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' },
    signal,
  });

  if (!response.ok) {
    throw await errorFor(response, 'the Excel export');
  }

  // Belt and braces: never save a JSON error body as an .xlsx. The server returns the spreadsheet
  // content type on success; anything JSON here is an error that leaked as 200, so surface it
  // visibly rather than handing the user a "workbook" that is really an error document.
  const contentType = response.headers.get('Content-Type') ?? '';
  if (contentType.includes('application/json')) {
    throw await errorFor(response, 'the Excel export');
  }

  const blob = await response.blob();
  saveBlob(blob, filenameFromResponse(response) ?? 'licence-activity.xlsx');
}
