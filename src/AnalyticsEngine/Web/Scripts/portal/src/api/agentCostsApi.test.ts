import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import { fetchSummary } from './agentCostsApi';
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

describe('agentCostsApi errors', () => {
  it('uses catalogued text for the known server-side load failure', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    mockedFetch.mockResolvedValue(jsonResponse({
      code: 'agentCostsLoadFailed',
      message: 'A changed English server message that must not be matched by prose.',
    }, 500));

    await expect(fetchSummary({ from: '2026-09-01', to: '2026-09-25' })).rejects.toThrow(
      'Si sigue ocurriendo, compruebe que la base de datos sea accesible',
    );
    await expect(fetchSummary({ from: '2026-09-01', to: '2026-09-25' })).rejects.not.toThrow(
      'The agent cost figures could not be loaded',
    );
  });

  it('keeps a server message it does not recognise', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ message: 'A future Agent Costs error.' }, 500));

    await expect(fetchSummary({ from: '2026-09-01', to: '2026-09-25' })).rejects.toThrow('A future Agent Costs error.');
  });

  it('does not translate a 500 only because its English message has the old prefix', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({
      message: 'The agent cost figures could not be loaded. A different server error.',
    }, 500));

    await expect(fetchSummary({ from: '2026-09-01', to: '2026-09-25' })).rejects.toThrow(
      'The agent cost figures could not be loaded. A different server error.',
    );
  });

  it('translates a known code whatever the status, so a coded reply is enough to reach the reader', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'agentCostsLoadFailed', message: 'Server English.' }, 503));

    await expect(fetchSummary({ from: '2026-09-01', to: '2026-09-25' })).rejects.toThrow('No se han podido cargar las cifras de costes de agentes.');
  });
});
