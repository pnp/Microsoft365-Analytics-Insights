import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import { fetchUserSummary } from './userLookupApi';
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

describe('userLookupApi errors', () => {
  it('uses the portal language for a known not-found response instead of the server English UPN message', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    mockedFetch.mockResolvedValue(jsonResponse({
      code: 'userNotFound',
      message: "No user found with UPN 'missing@contoso.com'.",
    }, 404));

    await expect(fetchUserSummary('missing@contoso.com')).rejects.toThrow('No se encontró ningún usuario coincidente.');
    await expect(fetchUserSummary('missing@contoso.com')).rejects.not.toThrow('No user found');
  });

  it('keeps the server message for an unrecognised failure', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ message: 'A newer server-side validation failed.' }, 422));

    await expect(fetchUserSummary('ada@contoso.com')).rejects.toThrow('A newer server-side validation failed.');
  });

  it('does not turn every 404 into a missing-user message', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ message: 'Route missing.' }, 404));

    await expect(fetchUserSummary('ada@contoso.com')).rejects.toThrow('Request failed (404)');
  });
});
