import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import {
  cancelUserScopePurge,
  fetchUserScope,
  fetchUserScopePurge,
  refreshUserScope,
  startUserScopePurge,
} from './userScopeApi';
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

describe('userScopeApi', () => {
  it('reads the status with a GET through apiFetch', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ filtered: false }, 200));

    await expect(fetchUserScope()).resolves.toMatchObject({ filtered: false });

    const [url, init] = mockedFetch.mock.calls[0];
    expect(url).toMatch(/\/api\/UserScope$/);
    expect(init?.method).toBe('GET');
  });

  it('refreshes the scope with a POST through apiFetch', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ filtered: true }, 200));

    await expect(refreshUserScope()).resolves.toMatchObject({ filtered: true });

    const [url, init] = mockedFetch.mock.calls[0];
    expect(url).toMatch(/\/api\/UserScope\/refresh$/);
    expect(init?.method).toBe('POST');
    expect(init?.body).toBeUndefined();
  });

  it('starts a purge with the acknowledgement and what was confirmed: the count shown and the filter it was shown under', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ id: 12, state: 'queued' }, 202));

    await expect(startUserScopePurge(25, 'sha256:0000')).resolves.toMatchObject({ id: 12, state: 'queued' });

    const [url, init] = mockedFetch.mock.calls[0];
    expect(url).toMatch(/\/api\/UserScope\/purge$/);
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      acknowledged: true,
      confirmedOutsideScopeCount: 25,
      confirmedFilterFingerprint: 'sha256:0000',
    });
    expect((init?.headers as Record<string, string>)['Content-Type']).toBe('application/json');
  });

  it('keeps the code and the numbers as they are now when a purge is refused because the scope changed', async () => {
    const database = { totalUsers: 120, inScopeUsers: 80, outOfScopeUsers: 40 };
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'purgeExceedsConfirmed', database, confirmedOutsideScopeCount: 25 }, 409));

    const error = await startUserScopePurge(25, 'sha256:0000').catch((e: unknown) => e);

    expect(error).toMatchObject({ name: 'UserScopeApiError', code: 'purgeExceedsConfirmed', database });
    expect((error as Error).message).toMatch(/More people are outside the scope now than the number you confirmed/);
  });

  it('polls a purge job by id', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ id: 12, state: 'running' }, 200));

    await expect(fetchUserScopePurge(12)).resolves.toMatchObject({ id: 12, state: 'running' });
    expect(mockedFetch.mock.calls[0][0]).toMatch(/\/api\/UserScope\/purge\/12$/);
    expect(mockedFetch.mock.calls[0][1]?.method).toBe('GET');
  });

  it('cancels a purge job by id', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ id: 12, cancelRequested: true }, 200));

    await expect(cancelUserScopePurge(12)).resolves.toMatchObject({ id: 12, cancelRequested: true });
    expect(mockedFetch.mock.calls[0][0]).toMatch(/\/api\/UserScope\/purge\/12\/cancel$/);
    expect(mockedFetch.mock.calls[0][1]?.method).toBe('POST');
  });

  it.each([
    ['acknowledgementRequired', 'Tick the acknowledgement before starting the purge.'],
    ['scopeNotFiltered', 'UserGroupsFilter is not limiting imports'],
    ['scopeUnavailable', 'The user scope is unavailable.'],
    ['scopeEmpty', 'The configured filter currently contains nobody'],
    ['nothingToPurge', 'There are no database users outside the current scope.'],
    ['purgeAlreadyRunning', 'A user-scope purge is already running.'],
    ['jobNotFound', 'That purge job no longer exists.'],
    ['jobNotActive', 'That purge job is no longer running.'],
    ['databaseUnavailable', 'The analytics database is unavailable.'],
    ['storageUnavailable', "Couldn't reach Azure Table storage, where purges keep their progress."],
    ['purgeExceedsConfirmed', 'More people are outside the scope now than the number you confirmed'],
    ['filterChanged', 'UserGroupsFilter has changed since this page was loaded'],
  ])("turns server error code '%s' into its catalog sentence", async (code, expected) => {
    mockedFetch.mockResolvedValue(jsonResponse({ code }, 409));

    await expect(startUserScopePurge(25, 'sha256:0000')).rejects.toThrow(expected);
  });

  it("falls back to the HTTP status for unknown codes and the same-origin check's bare response", async () => {
    mockedFetch.mockResolvedValue(new Response('', { status: 403 }));
    await expect(fetchUserScope()).rejects.toThrow("Couldn't load the user scope (403).");

    mockedFetch.mockResolvedValue(jsonResponse({ code: 'constructor' }, 500));
    await expect(cancelUserScopePurge(12)).rejects.toThrow("Couldn't stop the purge (500).");
  });
});
