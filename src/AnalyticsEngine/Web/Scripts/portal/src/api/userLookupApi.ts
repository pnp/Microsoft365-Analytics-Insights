import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n/catalog';
import { apiFetch } from './http';
import type { UserDataSummary, UserDataDetailResponse } from '../types/userData';
import type {
  LiveLicenceComparison,
  ReportLicenceVerdict,
  UserCoverage,
  UserDiagnostics,
  UserRefreshRequest,
  UserRefreshSubmitResult,
  VerdictReport,
} from '../types/userDiagnostics';

const baseUrl = (): string =>
  window.o365AnalyticsUserLookupAPI ?? `${window.location.origin}/api/UserDataLookup`;

const ERROR_CODE_KEYS: Record<string, TranslationKey> = {
  missingUpn: 'errors.userLookup.missingUpn',
  unknownCategory: 'errors.userLookup.unknownCategory',
  categoryNoDrilldown: 'errors.userLookup.categoryNoDrilldown',
  unknownReport: 'errors.userLookup.unknownReport',
  // The report-verdict trace validates its period with the Copilot Adoption report's own rules and keys.
  'copilotAdoption.error.missingDate': 'copilotAdoption.error.missingDate',
  'copilotAdoption.error.invalidDateFormat': 'copilotAdoption.error.invalidDateFormat',
  'copilotAdoption.error.invalidDateRange': 'copilotAdoption.error.invalidDateRange',
  'copilotAdoption.error.dateTooEarly': 'copilotAdoption.error.dateTooEarly',
};

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await apiFetch(url, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    signal,
  });

  if (!response.ok) {
    let message = translateActive('errors.userLookup.requestFailed', { status: response.status });
    try {
      const body = await response.json() as { code?: unknown; message?: unknown; category?: unknown; upn?: unknown } | null;
      if (response.status === 404 && body?.code === 'userNotFound') {
        // The catalog's English is the server's sentence, rebuilt from the UPN fact; without the fact the
        // server's own sentence is still better than one that has lost who was looked up.
        if (typeof body.upn === 'string') {
          message = translateActive('errors.userLookup.notFound', { upn: body.upn });
        } else if (typeof body.message === 'string') {
          message = body.message;
        }
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

/** Stored licences with their Copilot classification, licence history, import freshness, scope and findings. */
export function fetchUserDiagnostics(upn: string, signal?: AbortSignal): Promise<UserDiagnostics> {
  return getJson<UserDiagnostics>(`${baseUrl()}/diagnostics?upn=${encodeURIComponent(upn)}`, signal);
}

/** Reads the person's licences from Microsoft Graph now and compares them with SQL. Read-only. */
export function fetchLiveLicences(upn: string, signal?: AbortSignal): Promise<LiveLicenceComparison> {
  return getJson<LiveLicenceComparison>(`${baseUrl()}/live-licences?upn=${encodeURIComponent(upn)}`, signal);
}

export interface ReportVerdictRequest {
  report: VerdictReport;
  windowDays: number;
  from?: string;
  to?: string;
  licenceTypeIds?: number[];
}

/** Why a report counts the person as licensed or unlicensed, recomputed with the report's own rule. */
export function fetchReportVerdict(upn: string, request: ReportVerdictRequest, signal?: AbortSignal): Promise<ReportLicenceVerdict> {
  const params = new URLSearchParams({ upn, report: request.report, windowDays: String(request.windowDays) });
  if (request.from && request.to) {
    params.set('from', request.from);
    params.set('to', request.to);
  }
  if (request.licenceTypeIds && request.licenceTypeIds.length > 0) {
    params.set('licenceTypeIds', request.licenceTypeIds.join(','));
  }
  return getJson<ReportLicenceVerdict>(`${baseUrl()}/report-verdict?${params}`, signal);
}

/** The newest stored record of each kind for the person, and what is known about each feeding import. */
export function fetchUserCoverage(upn: string, signal?: AbortSignal): Promise<UserCoverage> {
  return getJson<UserCoverage>(`${baseUrl()}/coverage?upn=${encodeURIComponent(upn)}`, signal);
}

/**
 * Queues a one-person refresh of the profile and licences for the importer. A refusal (out of scope, not stored, no
 * Storage account) is an answer with a reason, not an error.
 *
 * It must go through `apiFetch`: the server refuses a state-changing call without the `X-Requested-With` header that
 * `apiFetch` adds (`RequireSameOriginXhrAttribute`).
 */
export async function requestUserRefresh(upn: string): Promise<UserRefreshSubmitResult> {
  const response = await apiFetch(`${baseUrl()}/refresh`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ upn }),
  });
  if (!response.ok) {
    throw new Error(translateActive('errors.userLookup.requestFailed', { status: response.status }));
  }
  return (await response.json()) as UserRefreshSubmitResult;
}

/** One refresh request and, once finished, its result. */
export function fetchUserRefresh(id: string, signal?: AbortSignal): Promise<UserRefreshRequest> {
  return getJson<UserRefreshRequest>(`${baseUrl()}/refresh-status?id=${encodeURIComponent(id)}`, signal);
}