import { afterEach, describe, expect, it, vi } from 'vitest';
import { clearUserImportCheckpoint } from './userImportCheckpointApi';

/**
 * Deliberately NOT mocking `./http`: this proves the request that actually leaves the browser carries what the
 * server's cross-site request forgery check demands (`RequireSameOriginXhrAttribute`). If the clear were ever
 * rewritten to call `fetch` directly, the header would silently go missing and every clear would get a 403.
 */
describe('userImportCheckpointApi on the wire', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sends the X-Requested-With header the server requires of a state-changing call, with the session cookie', async () => {
    const fetchMock = vi.fn(async () =>
      new Response(JSON.stringify({ checkpointCleared: true, lastCompletedCleared: true }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await clearUserImportCheckpoint(true);

    const init = (fetchMock.mock.calls[0] as unknown as [string, RequestInit])[1];
    const headers = init.headers as Record<string, string>;
    expect(headers['X-Requested-With']).toBe('XMLHttpRequest');
    expect(init.credentials).toBe('same-origin');
    expect(init.method).toBe('POST');
  });
});
