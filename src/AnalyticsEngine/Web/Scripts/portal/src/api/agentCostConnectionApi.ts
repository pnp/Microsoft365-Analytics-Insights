import { apiFetch } from './http';
import { translateActive } from '../i18n/runtime';
import type { TranslationKey } from '../i18n';

export type AgentCostConnectionStatus = {
  state: 'connected' | 'disconnected' | 'reconnectNeeded' | 'storageNotConfigured';
};

const errorKeys = new Map<string, TranslationKey>([
  ['storageNotConfigured', 'admin.agentCostConnection.storageNotConfigured'],
  ['storageUnavailable', 'admin.agentCostConnection.storageUnavailable'],
  ['identityMismatch', 'admin.agentCostConnection.identityMismatch'],
]);

async function request<T>(path: string, method = 'GET'): Promise<T> {
  const response = await apiFetch(`/api/AgentCostConnection${path}`, {
    method,
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) {
    let code: unknown;
    try { code = ((await response.json()) as { code?: unknown })?.code; } catch { /* Non-JSON error. */ }
    const key = typeof code === 'string' ? errorKeys.get(code) : undefined;
    throw new Error(translateActive(key ?? 'admin.agentCostConnection.failed'));
  }
  return await response.json() as T;
}

export const fetchAgentCostConnection = (): Promise<AgentCostConnectionStatus> => request('');
export const disconnectAgentCostConnection = (): Promise<AgentCostConnectionStatus> => request('/disconnect', 'POST');
export async function beginAgentCostConnection(): Promise<string> {
  const result = await request<{ url: string }>('/begin', 'POST');
  const url = new URL(result.url, window.location.origin);
  if (url.origin !== window.location.origin || url.pathname !== '/Account/ConnectAgentCosts') {
    throw new Error(translateActive('admin.agentCostConnection.failed'));
  }
  return url.href;
}
