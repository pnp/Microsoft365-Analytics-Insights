import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import { clearUserImportCheckpoint, fetchUserImportCheckpoint } from './userImportCheckpointApi';
import { loadCatalog } from '../i18n';
import { setActiveLanguage } from '../i18n/runtime';

vi.mock('./http', () => ({ apiFetch: vi.fn() }));

const mockedFetch = vi.mocked(apiFetch);

function jsonResponse(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  mockedFetch.mockReset();
  setActiveLanguage('en');
});

describe('userImportCheckpointApi', () => {
  it('clears with a JSON POST through apiFetch', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ checkpointCleared: true, lastCompletedCleared: false }, 200));

    await expect(clearUserImportCheckpoint(false)).resolves.toEqual({ checkpointCleared: true, lastCompletedCleared: false });

    const [url, init] = mockedFetch.mock.calls[0];
    expect(url).toMatch(/\/api\/UserImportCheckpoint\/clear$/);
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ runOnNextCycle: false });
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
  });

  it('reads the status with a GET', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ redisConfigured: true, checkpointStored: false }, 200));

    await expect(fetchUserImportCheckpoint()).resolves.toMatchObject({ redisConfigured: true, checkpointStored: false });
    expect(mockedFetch.mock.calls[0][0]).toMatch(/\/api\/UserImportCheckpoint$/);
    expect(mockedFetch.mock.calls[0][1]?.method).toBe('GET');
  });

  it("turns the server's error code into a sentence in the reader's language", async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'redisUnavailable' }, 503));

    await expect(fetchUserImportCheckpoint()).rejects.toThrow('No se ha podido conectar con Azure Cache for Redis');
  });

  it('explains a deployment with no Redis', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'redisNotConfigured' }, 409));

    await expect(clearUserImportCheckpoint(true)).rejects.toThrow(
      "Azure Cache for Redis isn't configured for this deployment, so there is no checkpoint to clear.",
    );
  });

  it("falls back to the HTTP status for anything else, including the same-origin check's bare 403", async () => {
    mockedFetch.mockResolvedValue(new Response('', { status: 403 }));

    await expect(clearUserImportCheckpoint(true)).rejects.toThrow("Couldn't clear the user import checkpoint (403).");
  });

  it('ignores codes it does not know, even ones that look like object properties', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'constructor' }, 500));

    await expect(fetchUserImportCheckpoint()).rejects.toThrow("Couldn't load the user import checkpoint (500).");
  });
});
