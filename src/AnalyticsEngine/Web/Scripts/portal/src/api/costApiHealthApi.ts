import { apiFetch } from './http';
import { translateActive } from '../i18n/runtime';

export interface CostApiConnectionHealth {
  source: 'graph' | 'studio';
  status: string;
  lastAttempt?: string | null;
  lastSuccessfulRead?: string | null;
  failureSince?: string | null;
  failureCode?: string | null;
}

export async function fetchCostApiHealth(signal: AbortSignal): Promise<CostApiConnectionHealth[]> {
  const response = await apiFetch('/api/CopilotCostHealth', { signal });
  if (!response.ok) throw new Error(translateActive('health.costApi.checkFailed'));
  return response.json();
}
