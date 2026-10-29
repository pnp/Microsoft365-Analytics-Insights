import { beforeEach, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import { changeCostConnection, fetchCostCollection, fetchCostConnection } from './copilotCostConnectionApi';
import { setActiveLanguage } from '../i18n/runtime';

vi.mock('./http', () => ({ apiFetch: vi.fn() }));
beforeEach(() => { vi.clearAllMocks(); setActiveLanguage('en'); });

it('preserves actionable storage failures and never exposes an unknown server message', async () => {
  vi.mocked(apiFetch).mockResolvedValueOnce(new Response(JSON.stringify({ status: 'storageUnavailable' }), { status: 503 }));
  await expect(fetchCostConnection()).rejects.toThrow(/Azure Table/);
  vi.mocked(apiFetch).mockResolvedValueOnce(new Response(JSON.stringify({ status: 'untrusted-payload' }), { status: 503 }));
  await expect(changeCostConnection('probe', 'csrf')).rejects.toThrow(/unrecognised connection state/);
  expect(apiFetch).toHaveBeenLastCalledWith('/api/CopilotCostConnection/probe', expect.objectContaining({
    method: 'POST', headers: expect.objectContaining({ 'X-Copilot-Cost-CSRF': 'csrf' }),
  }));
});

it('does not render an invalid JSON error body as operator-facing text', async () => {
  vi.mocked(apiFetch).mockResolvedValue(new Response('<html>untrusted-payload</html>', { status: 503 }));
  await expect(fetchCostConnection()).rejects.toThrow('The connection request failed');
  await expect(changeCostConnection('disconnect', 'csrf')).rejects.toThrow('The connection request failed');
});

it('reports a failed scheduled-status read without starting a collection', async () => {
  vi.mocked(apiFetch).mockResolvedValue(new Response(JSON.stringify({ status: 'databaseUnavailable' }), { status: 503 }));
  await expect(fetchCostCollection()).rejects.toThrow(/preview database migration/);
  expect(apiFetch).toHaveBeenCalledWith('/api/CopilotCostConnection/collection', { headers: { Accept: 'application/json' } });
});

it('distinguishes an unreadable response from an unsafe paging link', async () => {
  vi.mocked(apiFetch).mockResolvedValueOnce(new Response(JSON.stringify({ status: 'invalidResponse' }), { status: 503 }));
  await expect(changeCostConnection('probe', 'csrf')).rejects.toThrow(/missing required fields/);
  vi.mocked(apiFetch).mockResolvedValueOnce(new Response(JSON.stringify({ status: 'invalidPage' }), { status: 503 }));
  await expect(changeCostConnection('probe', 'csrf')).rejects.toThrow(/unexpected or repeated link/);
});
