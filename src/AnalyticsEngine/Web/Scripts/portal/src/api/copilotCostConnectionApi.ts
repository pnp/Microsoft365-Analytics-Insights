import { apiFetch } from './http';
import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n/catalog';

export interface CostConnectionStatus {
  status: string;
  csrfToken?: string;
  connectedAt?: string;
  tokenAcquiredAt?: string;
  probe?: {
    phase: string;
    directoryUsers: number;
    inventoryComplete: boolean;
    observed: number;
    measured: number;
    empty: number;
    partial: number;
    unevaluated: number;
    failed: number;
    denied: number;
    lastOutcome: string;
  };
}

export interface CostCollectionStatus {
  status: string;
  enabled: boolean;
  phase: string;
  directoryUsers: number;
  inventoryComplete: boolean;
  observed: number;
  pending: number;
  retrying: number;
  measured: number;
  empty: number;
  partial: number;
  unevaluated: number;
  failed: number;
  denied: number;
  lastAttempt?: string;
  lastCompleted?: string;
  lastOutcome?: string;
}

const COLLECTION_PHASE_KEYS: Record<string, TranslationKey> = {
  idle: 'admin.costConnection.collectionIdle',
  context: 'admin.costConnection.collectionContext',
  inventory: 'admin.costConnection.collectionInventory',
  users: 'admin.costConnection.collectionUsers',
  complete: 'admin.costConnection.collectionComplete',
  paused: 'admin.costConnection.collectionPaused',
};

export function costCollectionPhaseText(phase: string): string {
  return translateActive(COLLECTION_PHASE_KEYS[phase] ?? 'admin.costConnection.collectionUnknown');
}

const STATUS_KEYS: Record<string, TranslationKey> = {
  disabled: 'admin.costConnection.disabled',
  storageRequired: 'admin.costConnection.storageRequired',
  configurationRequired: 'admin.costConnection.configurationRequired',
  operatorRequired: 'admin.costConnection.operatorRequired',
  disconnected: 'admin.costConnection.disconnected',
  connected: 'admin.costConnection.connected',
  reconnectRequired: 'admin.costConnection.reconnectRequired',
  permissionDenied: 'admin.costConnection.permissionDenied',
  storageUnavailable: 'admin.costConnection.storageUnavailable',
  storageCorrupt: 'admin.costConnection.storageUnavailable',
  busy: 'admin.costConnection.busy',
  invalidRequest: 'admin.costConnection.invalidRequest',
  invalidCallback: 'admin.costConnection.invalidCallback',
  accountMismatch: 'admin.costConnection.accountMismatch',
  identityUnavailable: 'admin.costConnection.identityUnavailable',
  directoryUnavailable: 'admin.costConnection.directoryUnavailable',
  throttled: 'admin.costConnection.throttled',
  sourceUnavailable: 'admin.costConnection.sourceUnavailable',
  sourceFailure: 'admin.costConnection.sourceUnavailable',
  notFound: 'admin.costConnection.sourceNotFound',
  invalidResponse: 'admin.costConnection.invalidResponse',
  invalidPage: 'admin.costConnection.invalidResponse',
  pageLimit: 'admin.costConnection.pageLimit',
  cacheTooLarge: 'admin.costConnection.cacheTooLarge',
  sliceCompleted: 'admin.costConnection.sliceCompleted',
  databaseUnavailable: 'admin.costConnection.databaseUnavailable',
  observed: 'admin.costConnection.collectionObserved',
  partial: 'admin.costConnection.collectionPartial',
  complete: 'admin.costConnection.collectionComplete',
  inventory: 'admin.costConnection.collectionInventory',
  context: 'admin.costConnection.collectionContext',
  idle: 'admin.costConnection.collectionIdle',
  sliceTimedOut: 'admin.costConnection.collectionTimedOut',
};

export function costConnectionText(status: string): string {
  return translateActive(STATUS_KEYS[status] ?? 'admin.costConnection.unknown');
}

export async function fetchCostConnection(): Promise<CostConnectionStatus> {
  const response = await apiFetch('/api/CopilotCostConnection', { headers: { Accept: 'application/json' } });
  const result = await readResult<CostConnectionStatus>(response);
  if (!response.ok) throw new Error(result.status ? costConnectionText(result.status) : translateActive('admin.costConnection.requestFailed'));
  return result;
}

export async function fetchCostCollection(): Promise<CostCollectionStatus> {
  const response = await apiFetch('/api/CopilotCostConnection/collection', { headers: { Accept: 'application/json' } });
  const result = await readResult<CostCollectionStatus>(response);
  if (!response.ok) throw new Error(result.status ? costConnectionText(result.status) : translateActive('admin.costConnection.requestFailed'));
  return result;
}

export async function changeCostConnection(action: 'connect' | 'disconnect' | 'probe' | 'resetProbe', csrfToken: string): Promise<{ authorizationUrl?: string }> {
  const response = await apiFetch(`/api/CopilotCostConnection/${action}`, {
    method: 'POST',
    headers: { Accept: 'application/json', 'X-Copilot-Cost-CSRF': csrfToken },
  });
  const result = await readResult<{ status?: string; authorizationUrl?: string }>(response);
  if (!response.ok) throw new Error(result.status ? costConnectionText(result.status) : translateActive('admin.costConnection.requestFailed'));
  return result;
}

async function readResult<T>(response: Response): Promise<T> {
  try { return await response.json(); }
  catch { throw new Error(translateActive('admin.costConnection.requestFailed')); }
}
