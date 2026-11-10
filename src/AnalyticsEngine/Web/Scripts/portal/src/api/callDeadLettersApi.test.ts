import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import { fetchCallDeadLetters, requestCallDeadLetterAction } from './callDeadLettersApi';
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

describe('callDeadLettersApi', () => {
  it('asks with a JSON POST through apiFetch, which the server requires of a state-changing call', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ available: true, canRequest: false }, 202));

    await expect(requestCallDeadLetterAction('clear')).resolves.toMatchObject({ available: true, canRequest: false });

    const [url, init] = mockedFetch.mock.calls[0];
    expect(url).toMatch(/\/api\/CallDeadLetters\/requests$/);
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ action: 'clear' });
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
  });

  it('reads the status with an uncached GET, as the card polls it', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ available: true, deadLetterCount: 3 }, 200));

    await expect(fetchCallDeadLetters()).resolves.toMatchObject({ deadLetterCount: 3 });
    expect(mockedFetch.mock.calls[0][0]).toMatch(/\/api\/CallDeadLetters$/);
    expect(mockedFetch.mock.calls[0][1]?.method).toBe('GET');
    expect(mockedFetch.mock.calls[0][1]?.cache).toBe('no-store');
  });

  it("turns the server's refusal code into a sentence in the reader's language", async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'requestPending' }, 409));
    await expect(requestCallDeadLetterAction('reprocess')).rejects.toThrow('Another request is already waiting or running.');

    await loadCatalog('es');
    setActiveLanguage('es');
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'stateStoreInMemory' }, 409));
    await expect(requestCallDeadLetterAction('reprocess')).rejects.toThrow('Esta implementación no tiene cadena de conexión de Azure Storage');
  });

  it('falls back to the HTTP status for a code it does not know, or no code at all', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'constructor' }, 409));
    await expect(requestCallDeadLetterAction('clear')).rejects.toThrow("Couldn't pass the request to the importer (409).");

    mockedFetch.mockResolvedValue(new Response('', { status: 403 }));
    await expect(requestCallDeadLetterAction('clear')).rejects.toThrow("Couldn't pass the request to the importer (403).");

    mockedFetch.mockResolvedValue(new Response('', { status: 500 }));
    await expect(fetchCallDeadLetters()).rejects.toThrow("Couldn't load the dead-lettered calls (500).");
  });
});
