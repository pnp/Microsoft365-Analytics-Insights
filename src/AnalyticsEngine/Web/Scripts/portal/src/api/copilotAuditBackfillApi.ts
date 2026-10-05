import { apiFetch } from './http';
import type { CopilotAuditBackfillJob, CopilotAuditBackfillStatus } from '../types/copilotAuditBackfill';

const BASE = 'api/CopilotAuditBackfill';

export async function fetchCopilotAuditBackfill(): Promise<CopilotAuditBackfillStatus> {
  const response = await apiFetch(BASE);
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { code?: unknown } | null;
    throw new Error(typeof body?.code === 'string' ? body.code : 'loadFailed');
  }
  return response.json() as Promise<CopilotAuditBackfillStatus>;
}

export async function startCopilotAuditBackfill(startDateUtc: string | null): Promise<CopilotAuditBackfillJob> {
  const response = await apiFetch(`${BASE}/start`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ startDateUtc }),
  });
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { code?: unknown } | null;
    throw new Error(typeof body?.code === 'string' ? body.code : 'startFailed');
  }
  return response.json() as Promise<CopilotAuditBackfillJob>;
}

export async function cancelCopilotAuditBackfill(id: number): Promise<CopilotAuditBackfillJob> {
  const response = await apiFetch(`${BASE}/${id}/cancel`, {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
  });
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { code?: unknown } | null;
    throw new Error(typeof body?.code === 'string' ? body.code : 'cancelFailed');
  }
  return response.json() as Promise<CopilotAuditBackfillJob>;
}
