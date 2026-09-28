import { apiFetch } from './http';
import { translateActive } from '../i18n/runtime';
import type {
  UserOrgAttributeCatalogue,
  UserOrgBrowseQuery,
  UserOrgChangeLogPage,
  UserOrgCsvColumnChoice,
  UserOrgCsvPreview,
  UserOrgImportJob,
  UserOrgImportMode,
  UserOrgImportQueued,
  UserOrgMemberPage,
  UserOrgTestResult,
  UserOrgType,
  UserOrgTypeSave,
  UserOrgValuePage,
} from '../types/userOrgs';

const baseUrl = (): string => `${window.location.origin}/api/UserOrg`;

/**
 * An error the API answered with. `code` is a stable key the portal words itself - see
 * `UserOrgApiErrorCode` - with `values` holding the facts behind it; `message` is the server's
 * English, a fallback for a code the portal does not know.
 */
export class UserOrgApiError extends Error {
  readonly status: number;
  readonly code: string | null;
  readonly values: Record<string, string | number | null>;

  constructor(message: string, status: number, code: string | null, values: Record<string, string | number | null> | null) {
    super(message);
    this.name = 'UserOrgApiError';
    this.status = status;
    this.code = code;
    this.values = values ?? {};
  }
}

/**
 * Pulls the server's message out of an error response.
 *
 * The API answers a rejected configuration with a 400, a stable `code` and the facts behind it, and
 * the page words the code in the reader's language. The server's English is kept only beside a code,
 * as the fallback for one this build does not know; a reply without a code - a proxy's error page, or
 * a server older than its page - keeps the portal's own words, so it never reaches a Spanish page in
 * English.
 */
async function toError(response: Response): Promise<Error> {
  let message = translateActive('errors.userOrgs.requestFailed', { status: response.status });
  let code: string | null = null;
  let values: Record<string, string | number | null> | null = null;
  try {
    const body = await response.json();
    if (body && typeof body.code === 'string' && body.code.length > 0) {
      code = body.code;
      if (typeof body.message === 'string' && body.message.length > 0) {
        message = body.message;
      }
    }
    if (body && body.values && typeof body.values === 'object') {
      values = body.values;
    }
  } catch {
    /* no JSON body */
  }
  return new UserOrgApiError(message, response.status, code, values);
}

async function send<T>(url: string, init: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await apiFetch(url, init);
  } catch (e) {
    // fetch rejects with a TypeError when the request never got an answer - the network dropped, or
    // the browser could not re-read a file that changed on disk after it was chosen. Its message is
    // the browser's own English ("Failed to fetch"), so it is replaced. Aborts and an expired session
    // are the caller's business and pass through untouched.
    if (e instanceof TypeError) {
      throw new Error(translateActive('errors.userOrgs.network'));
    }
    throw e;
  }
  if (!response.ok) {
    throw await toError(response);
  }
  return response.json() as Promise<T>;
}

function json(method: string, body?: unknown): RequestInit {
  return {
    method,
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  };
}

/** Every configured org type, with its counts and last import. */
export function fetchOrgTypes(): Promise<UserOrgType[]> {
  return send<UserOrgType[]>(`${baseUrl()}/types`, json('GET'));
}

export function createOrgType(model: UserOrgTypeSave): Promise<UserOrgType> {
  return send<UserOrgType>(`${baseUrl()}/types`, json('POST', model));
}

export function updateOrgType(id: number, model: UserOrgTypeSave): Promise<UserOrgType> {
  return send<UserOrgType>(`${baseUrl()}/types/${id}`, json('PUT', model));
}

/**
 * Deletes a type. `expectedRevision` is the revision the page showed: a type someone else has saved since
 * is refused rather than deleted.
 */
export function deleteOrgType(id: number, expectedRevision?: number): Promise<{ deleted: boolean }> {
  const query = expectedRevision === undefined ? '' : `?expectedRevision=${encodeURIComponent(String(expectedRevision))}`;
  return send<{ deleted: boolean }>(`${baseUrl()}/types/${id}${query}`, json('DELETE'));
}

/**
 * Resolves an attribute for one user against live Graph.
 *
 * Deliberately takes the attribute name rather than an org type id, so an admin can validate a
 * configuration *before* saving it - which matters because Graph fails the entire user import when
 * `$select` names a property it does not recognise.
 */
export function testEntraAttribute(
  entraAttributeName: string,
  upn: string,
): Promise<UserOrgTestResult> {
  return send<UserOrgTestResult>(
    `${baseUrl()}/test-entra`,
    json('POST', { entraAttributeName, upn }),
  );
}

/** The attribute picker's contents. Directory extension discovery is best-effort. */
export function fetchAttributeCatalogue(): Promise<UserOrgAttributeCatalogue> {
  return send<UserOrgAttributeCatalogue>(`${baseUrl()}/attributes`, json('GET'));
}

function fileBody(file: File): RequestInit {
  const form = new FormData();
  form.append('file', file, file.name);
  // Content-Type is deliberately not set: the browser has to add the multipart boundary itself, and
  // setting it by hand produces a body the server cannot parse.
  return { method: 'POST', headers: { Accept: 'application/json' }, body: form };
}

/**
 * Parses the whole file, stages it as a draft and reports the blast radius of importing it. Nothing
 * is imported until `importCsv` commits the draft. `columns` overrides which columns are read, for a
 * file whose layout the server could not settle on its own.
 */
export function previewCsv(
  orgTypeId: number,
  file: File,
  columns: UserOrgCsvColumnChoice = {},
): Promise<UserOrgCsvPreview> {
  const params = new URLSearchParams({ orgTypeId: String(orgTypeId) });
  if (columns.userColumn !== undefined) params.set('userColumn', String(columns.userColumn));
  if (columns.valueColumn !== undefined) params.set('valueColumn', String(columns.valueColumn));
  return send<UserOrgCsvPreview>(`${baseUrl()}/preview-csv?${params.toString()}`, fileBody(file));
}

/**
 * Imports a previewed draft. `confirmedClearCount` is how many users the admin agreed may lose their
 * value - the number the preview showed for this mode, or 0. The server refuses if the import would
 * now clear more than that, so a confirmation can never cover a bigger wipe than the one it was
 * given for.
 */
export function importCsv(
  orgTypeId: number,
  draftId: number,
  mode: UserOrgImportMode,
  confirmedClearCount: number,
): Promise<UserOrgImportQueued> {
  const params = new URLSearchParams({
    orgTypeId: String(orgTypeId),
    draftId: String(draftId),
    mode,
    confirmedClearCount: String(Math.max(0, Math.floor(confirmedClearCount))),
  });
  return send<UserOrgImportQueued>(`${baseUrl()}/import-csv?${params.toString()}`, json('POST'));
}

/** Import progress. */
export function fetchImportJob(jobId: number, signal?: AbortSignal): Promise<UserOrgImportJob> {
  return send<UserOrgImportJob>(`${baseUrl()}/jobs/${jobId}`, { ...json('GET'), signal });
}

/** An org type's most recent imports, newest first. Previews that were never imported are not listed. */
export function fetchImportHistory(orgTypeId: number, take = 10, signal?: AbortSignal): Promise<UserOrgImportJob[]> {
  return send<UserOrgImportJob[]>(`${baseUrl()}/types/${orgTypeId}/imports?take=${take}`, { ...json('GET'), signal });
}

/** The most changes the server returns in one page. */
export const MAX_CHANGE_PAGE_SIZE = 1000;

/**
 * One page of what an import changed, user by user, in user principal name order. `search` keeps only
 * the users whose UPN starts with it; `continuation` is the previous page's, for the next one.
 */
export function fetchImportChanges(
  jobId: number,
  query: { search?: string; continuation?: string | null; pageSize?: number } = {},
  signal?: AbortSignal,
): Promise<UserOrgChangeLogPage> {
  const params = new URLSearchParams();
  if (query.search) params.set('search', query.search);
  if (query.continuation) params.set('continuation', query.continuation);
  if (query.pageSize) params.set('pageSize', String(Math.min(query.pageSize, MAX_CHANGE_PAGE_SIZE)));
  const qs = params.toString();
  return send<UserOrgChangeLogPage>(`${baseUrl()}/jobs/${jobId}/changes${qs ? `?${qs}` : ''}`, { ...json('GET'), signal });
}

function browseQueryString(query: UserOrgBrowseQuery): string {
  const params = new URLSearchParams();
  if (query.search && query.search.trim()) params.set('search', query.search.trim());
  if (query.page) params.set('page', String(query.page));
  if (query.pageSize) params.set('pageSize', String(query.pageSize));
  const text = params.toString();
  return text ? `?${text}` : '';
}

/** One page of an org type's organisations, largest first, each with how many users are in it. */
export function fetchOrgValues(
  orgTypeId: number,
  query: UserOrgBrowseQuery = {},
  signal?: AbortSignal,
): Promise<UserOrgValuePage> {
  return send<UserOrgValuePage>(`${baseUrl()}/types/${orgTypeId}/values${browseQueryString(query)}`, {
    ...json('GET'),
    signal,
  });
}

/** One page of the users in one organisation, by user principal name. */
export function fetchOrgMembers(
  orgTypeId: number,
  valueId: number,
  query: UserOrgBrowseQuery = {},
  signal?: AbortSignal,
): Promise<UserOrgMemberPage> {
  return send<UserOrgMemberPage>(
    `${baseUrl()}/types/${orgTypeId}/values/${valueId}/members${browseQueryString(query)}`,
    { ...json('GET'), signal },
  );
}
