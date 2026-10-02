import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n';
import { apiFetch } from './http';
import type { GlobalFilterAdmin, GlobalFilterEffective } from '../types/globalFilter';

/**
 * The administrator's global report filter: how it applies to the signed-in reader, and the editor's
 * read, save and preview.
 *
 * Only describes and edits the filter. Every report's own endpoint applies it on the server, so nothing
 * here - or anywhere in the portal - is what keeps a reader inside it.
 */
const baseUrl = (): string => `${window.location.origin}/api/GlobalFilter`;

/**
 * `GlobalFilterAPIController` answers failures with stable codes; these are the sentences for the codes
 * this build knows. Anything unrecognised gets the generic message with the HTTP status.
 */
export const GLOBAL_FILTER_ERROR_KEYS: ReadonlyMap<string, TranslationKey> = new Map<string, TranslationKey>([
  ['invalidFilter', 'errors.globalFilter.invalidFilter'],
  ['revisionConflict', 'errors.globalFilter.revisionConflict'],
  ['storageUnavailable', 'errors.globalFilter.storageUnavailable'],
  ['saveFailed', 'errors.globalFilter.saveFailed'],
]);

/** A refusal from the editor's endpoints, keeping the server's code so the page can react to the case. */
export class GlobalFilterApiError extends Error {
  readonly code: string | null;
  readonly status: number;

  constructor(message: string, code: string | null, status: number) {
    super(message);
    this.name = 'GlobalFilterApiError';
    this.code = code;
    this.status = status;
  }
}

async function failure(response: Response, fallbackKey: TranslationKey): Promise<GlobalFilterApiError> {
  let code: unknown;
  try {
    code = ((await response.json()) as { code?: unknown } | null)?.code;
  } catch {
    // No JSON body - the same-origin check's bare 403, or an error page from IIS.
  }

  const known = typeof code === 'string' ? code : null;
  const key = known ? GLOBAL_FILTER_ERROR_KEYS.get(known) : undefined;
  return new GlobalFilterApiError(
    key ? translateActive(key) : translateActive(fallbackKey, { status: response.status }),
    known,
    response.status,
  );
}

/** The global filter as it applies to the signed-in reader, with their own values filled in. */
export async function fetchEffectiveGlobalFilter(signal?: AbortSignal): Promise<GlobalFilterEffective> {
  const response = await apiFetch(`${baseUrl()}/effective`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
    signal,
  });
  if (!response.ok) throw await failure(response, 'errors.globalFilter.effectiveFailed');
  return (await response.json()) as GlobalFilterEffective;
}

/** The definition, for the administrator's editor. */
export async function fetchGlobalFilter(signal?: AbortSignal): Promise<GlobalFilterAdmin> {
  const response = await apiFetch(baseUrl(), {
    method: 'GET',
    headers: { Accept: 'application/json' },
    signal,
  });
  if (!response.ok) throw await failure(response, 'errors.globalFilter.loadFailed');
  return (await response.json()) as GlobalFilterAdmin;
}

/**
 * Replaces the global filter. `filter` is the wire form (`serializeGlobalFilter`); an empty string removes
 * it. `revision` is the one the editor opened, so a save made over someone else's change is refused (409).
 *
 * Through `apiFetch`, which adds the `X-Requested-With` header the server's cross-site request forgery
 * check (`RequireSameOriginXhrAttribute`) insists on.
 */
export async function saveGlobalFilter(filter: string, revision: number): Promise<GlobalFilterAdmin> {
  const response = await apiFetch(baseUrl(), {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ filter, revision }),
  });
  if (!response.ok) throw await failure(response, 'errors.globalFilter.saveFailedStatus');
  return (await response.json()) as GlobalFilterAdmin;
}

/** A draft evaluated for the signed-in administrator, as if it were saved. Saves nothing. */
export async function previewGlobalFilter(filter: string, signal?: AbortSignal): Promise<GlobalFilterEffective> {
  const response = await apiFetch(`${baseUrl()}/preview`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    body: JSON.stringify({ filter }),
    signal,
  });
  if (!response.ok) throw await failure(response, 'errors.globalFilter.previewFailed');
  return (await response.json()) as GlobalFilterEffective;
}
