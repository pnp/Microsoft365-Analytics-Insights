import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import {
  InvestmentRequestError,
  InvestmentSettingsError,
  fetchInvestmentPeople,
  fetchInvestmentSummary,
  fetchInvestmentTrend,
  saveInvestmentSettings,
} from './copilotInvestmentApi';
import { loadCatalog, setActiveLanguage } from '../i18n';

vi.mock('./http', () => ({ apiFetch: vi.fn() }));

const mockedFetch = vi.mocked(apiFetch);

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function url(call: number): URL {
  return new URL(String(mockedFetch.mock.calls[call][0]), 'https://contoso.example');
}

beforeEach(() => {
  mockedFetch.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
  setActiveLanguage('en');
});

describe('copilotInvestmentApi', () => {
  it('waits while the shared evidence is building and never reads the 202 body as the report', async () => {
    vi.useFakeTimers();
    mockedFetch
      .mockResolvedValueOnce(jsonResponse({ status: 'building', retryAfterSeconds: 3 }, 202))
      .mockResolvedValueOnce(jsonResponse({ windowDays: 28 }));

    const pending = fetchInvestmentSummary({ windowDays: 28 });
    await vi.advanceTimersByTimeAsync(2_900);
    expect(mockedFetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(200);

    await expect(pending).resolves.toEqual({ windowDays: 28 });
    expect(mockedFetch).toHaveBeenCalledTimes(2);
  });

  it('names the what-if fields the server refused, in the reader\u2019s language', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'scenarioInvalid', validationErrors: ['hourlyCostOutOfRange'] }, 400));

    const error = await fetchInvestmentSummary({ windowDays: 28, scenario: { hourlyCost: 99999 } }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(InvestmentRequestError);
    expect((error as InvestmentRequestError).validationErrors).toEqual(['hourlyCostOutOfRange']);
    expect((error as Error).message).not.toMatch(/scenarioInvalid/);
    expect(url(0).searchParams.get('hourlyCost')).toBe('99999');
  });

  it('asks for the balance history without what-if figures or a grouping, which do not change it', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ points: [], snapshotDate: null, failureCode: null }));

    await fetchInvestmentTrend({ windowDays: 90, userFilter: 'f', dimension: 'manager', scenario: { seatPrice: 30 } });

    const query = url(0).searchParams;
    expect(url(0).pathname).toBe('/api/CopilotInvestment/trend');
    expect(query.get('windowDays')).toBe('90');
    expect(query.get('userFilter')).toBe('f');
    expect(query.get('dimension')).toBeNull();
    expect(query.get('seatPrice')).toBeNull();
  });

  it('pages people on the server with the drill-down filters', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ total: 0, rows: [], currency: null, valueAvailable: false }));

    await fetchInvestmentPeople(
      { windowDays: 28, scenario: { realisationBase: 0.45 } },
      { search: '  \u0395\u03bb\u03ad\u03bd\u03b7 ', signals: ['noEvidence', 'belowBreakEven'], flag: 'reclaimCandidate', seat: 'licensed', sortBy: 'cost', sortDesc: true },
      50,
      50,
    );

    const query = url(0).searchParams;
    expect(query.get('search')).toBe('\u0395\u03bb\u03ad\u03bd\u03b7');
    expect(query.get('signals')).toBe('noEvidence,belowBreakEven');
    expect(query.get('flag')).toBe('reclaimCandidate');
    expect(query.get('seat')).toBe('licensed');
    expect(query.get('skip')).toBe('50');
    expect(query.get('take')).toBe('50');
    expect(query.get('realisationBase')).toBe('0.45');
  });

  it('saves the assumptions as a same-origin request against the version that was read', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'versionConflict' }, 409));
    const assumptions = {
      currency: 'EUR',
      seatPricePerMonth: 30,
      creditPrice: null,
      studioCreditPrice: null,
      hourlyCost: 50,
      minutesPerActiveDay: { conservative: 5, base: 10, upside: 20 },
      realisation: { conservative: 0.3, base: 0.5, upside: 0.7 },
      note: null,
    };

    const error = await saveInvestmentSettings(4, assumptions).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(InvestmentSettingsError);
    expect((error as InvestmentSettingsError).code).toBe('versionConflict');
    const init = mockedFetch.mock.calls[0][1]!;
    expect(init.method).toBe('POST');
    expect((init.headers as Record<string, string>)['X-Requested-With']).toBe('XMLHttpRequest');
    expect(JSON.parse(String(init.body))).toEqual({ expectedVersion: 4, assumptions });
  });
});
