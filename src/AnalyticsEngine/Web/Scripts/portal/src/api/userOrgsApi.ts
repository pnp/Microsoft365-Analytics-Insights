import { apiFetch } from './http';
import type {
  UserOrgAttributeCatalogue,
  UserOrgCsvPreview,
  UserOrgImportJob,
  UserOrgImportMode,
  UserOrgImportQueued,
  UserOrgTestResult,
  UserOrgType,
  UserOrgTypeSave,
} from '../types/userOrgs';

const baseUrl = (): string => `${window.location.origin}/api/UserOrg`;

/**
 * Pulls the server's message out of an error response.
 *
 * The API answers a rejected configuration with a 400 and a message written for an IT admin - which
 * attribute is wrong and why. Falling back to "Request failed (400)" would throw away the only
 * useful part, and this page is almost entirely about getting a configuration right.
 */
async function toError(response: Response): Promise<Error> {
  let message = `Request failed (${response.status})`;
  try {
    const body = await response.json();
    if (body && typeof body.message === 'string' && body.message.length > 0) {
      message = body.message;
    }
  } catch {
    /* no JSON body */
  }
  return new Error(message);
}

async function send<T>(url: string, init: RequestInit): Promise<T> {
  const response = await apiFetch(url, init);
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

export function deleteOrgType(id: number): Promise<{ deleted: boolean }> {
  return send<{ deleted: boolean }>(`${baseUrl()}/types/${id}`, json('DELETE'));
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

/** Parses the first rows of a file. Persists nothing. */
export function previewCsv(file: File): Promise<UserOrgCsvPreview> {
  return send<UserOrgCsvPreview>(`${baseUrl()}/preview-csv`, fileBody(file));
}

/** Stages a file and queues the background import. */
export function importCsv(
  orgTypeId: number,
  mode: UserOrgImportMode,
  file: File,
): Promise<UserOrgImportQueued> {
  const url = `${baseUrl()}/import-csv?orgTypeId=${orgTypeId}&mode=${encodeURIComponent(mode)}`;
  return send<UserOrgImportQueued>(url, fileBody(file));
}

/** Import progress. */
export function fetchImportJob(jobId: number, signal?: AbortSignal): Promise<UserOrgImportJob> {
  return send<UserOrgImportJob>(`${baseUrl()}/jobs/${jobId}`, { ...json('GET'), signal });
}
