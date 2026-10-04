import { afterEach, describe, expect, it, vi } from 'vitest';
import { cancelCopilotAuditBackfill, fetchCopilotAuditBackfill } from './copilotAuditBackfillApi';

describe('copilotAuditBackfillApi', () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('throws the server code when status cannot be loaded', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ code: 'stateUnavailable' }), { status: 503 }));

    await expect(fetchCopilotAuditBackfill()).rejects.toThrow('stateUnavailable');
  });

  it('throws the server code when cancel is refused', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(JSON.stringify({ code: 'jobNotActive' }), { status: 409 }));

    await expect(cancelCopilotAuditBackfill(42)).rejects.toThrow('jobNotActive');
  });
});
