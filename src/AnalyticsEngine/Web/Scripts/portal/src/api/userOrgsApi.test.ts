import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import { UserOrgApiError, fetchImportHistory } from './userOrgsApi';
import { loadCatalog, type TFunction } from '../i18n';
import { EN_CATALOG } from '../i18n/catalog';
import { setActiveLanguage, translateActive } from '../i18n/runtime';
import { userOrgErrorMessage } from '../components/userOrgs/userOrgShared';

vi.mock('./http', () => ({ apiFetch: vi.fn() }));

const mockedFetch = vi.mocked(apiFetch);

function jsonResponse(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

async function rejection(): Promise<UserOrgApiError> {
  try {
    await fetchImportHistory(7);
  } catch (e) {
    return e as UserOrgApiError;
  }
  throw new Error('The request was expected to fail.');
}

beforeEach(() => {
  mockedFetch.mockReset();
  setActiveLanguage('en');
});

describe('userOrgsApi errors', () => {
  it("keeps the portal's own words for a reply that carries no code", async () => {
    // A proxy's error page or a server older than the page: nothing on the page can word its English,
    // so a Spanish reader would get it verbatim.
    await loadCatalog('es');
    setActiveLanguage('es');
    mockedFetch.mockResolvedValue(jsonResponse({ message: 'Something went wrong handling that request.' }, 500));

    const error = await rejection();

    expect(error).toBeInstanceOf(UserOrgApiError);
    expect(error.code).toBeNull();
    expect(error.message).toBe('La solicitud ha fallado (500)');
  });

  it('keeps the server English beside a code, as the fallback for one this build does not know', async () => {
    mockedFetch.mockResolvedValue(
      jsonResponse({ code: 'aFutureCode', message: 'A newer server says this.', values: { name: 'Contoso' } }, 400),
    );

    const error = await rejection();

    expect(error.code).toBe('aFutureCode');
    expect(error.message).toBe('A newer server says this.');
    expect(error.values).toEqual({ name: 'Contoso' });
  });

  it("keeps the portal's words for a reply that is not JSON at all", async () => {
    mockedFetch.mockResolvedValue(new Response('<html>Bad gateway</html>', { status: 502, headers: { 'Content-Type': 'text/html' } }));

    expect((await rejection()).message).toBe('Request failed (502)');
  });

  it("words the server's fault and not-found codes in the reader's language", async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    const t: TFunction = translateActive;

    mockedFetch.mockResolvedValue(jsonResponse({ code: 'unexpected', message: 'Server English.' }, 500));
    expect(userOrgErrorMessage(await rejection(), t, 'errors.userOrgs.loadFailed')).toBe(
      'Se ha producido un error al procesar la solicitud. Consulte los registros del servicio para obtener más detalles.',
    );

    for (const [code, key] of [
      ['typeGone', 'userOrgs.message.typeGone'],
      ['jobGone', 'userOrgs.message.jobGone'],
      ['valueGone', 'userOrgs.message.valueGone'],
      ['notFromPortal', 'userOrgs.message.notFromPortal'],
    ] as const) {
      mockedFetch.mockResolvedValue(jsonResponse({ code, message: 'Server English.' }, code === 'notFromPortal' ? 400 : 404));

      const worded = userOrgErrorMessage(await rejection(), t, 'errors.userOrgs.loadFailed');
      expect(worded, code).toBe(translateActive(key));
      expect(worded, code).not.toBe(EN_CATALOG[key]);
    }
  });
});
