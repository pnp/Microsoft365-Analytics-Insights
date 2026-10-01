import { describe, it, expect, vi, afterEach } from 'vitest';
import { fetchMsGraph, GraphRequestError, GRAPH_ENDPOINTS } from './graph';

/** Just the parts of a fetch Response that fetchMsGraph reads. */
function graphResponse(status: number, body: unknown) {
  return { ok: status >= 200 && status < 300, status, json: async () => body } as unknown as Response;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('fetchMsGraph', () => {
  it('returns the body of a successful call, sending the access token', async () => {
    const token = 'synthetic-token';
    const fetchMock = vi.fn().mockResolvedValue(graphResponse(200, { displayName: 'Ada Contoso' }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(fetchMsGraph(GRAPH_ENDPOINTS.ME, token)).resolves.toEqual({ displayName: 'Ada Contoso' });
    expect(fetchMock).toHaveBeenCalledWith(GRAPH_ENDPOINTS.ME, {
      headers: { Authorization: `Bearer ${token}` },
    });
  });

  // A rejected token or a missing permission used to come back as data. The Teams permissions page then read
  // the error body as "you are in no Teams" and never said the call had failed.
  it.each([401, 403, 500])('rejects on HTTP %i rather than returning the error body as data', async (status) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(graphResponse(status, { error: { code: 'InvalidAuthenticationToken' } })));

    const call = fetchMsGraph(GRAPH_ENDPOINTS.JOINED_TEAMS, 'synthetic-token');

    await expect(call).rejects.toBeInstanceOf(GraphRequestError);
    await expect(call).rejects.toMatchObject({ status });
  });
});
