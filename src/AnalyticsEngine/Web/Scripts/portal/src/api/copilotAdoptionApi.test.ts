import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { apiFetch } from './http';
import { fetchAdoptionSummary, workbookExportUrl } from './copilotAdoptionApi';
import { loadCatalog, setActiveLanguage } from '../i18n';

vi.mock('./http', () => ({ apiFetch: vi.fn() }));

const mockedFetch = vi.mocked(apiFetch);

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function lastUrl(): string {
  const calls = mockedFetch.mock.calls;
  return String(calls[calls.length - 1][0]);
}

beforeEach(() => {
  mockedFetch.mockReset();
});

describe('copilotAdoptionApi query building', () => {
  /**
   * The Copilot Adoption report deliberately carries no money.
   *
   * It used to take per-SKU seat prices from the page header and turn idle seats into an "idle
   * licence spend" figure. That was withdrawn: the product reports what it can observe - who is and
   * is not using a seat - and the only value estimate it makes is the Cowork time saving, in hours.
   * A price typed into a report header is not a source of truth about what a tenant pays, and a
   * money figure derived from one gets quoted as though it were.
   *
   * This asserts the request surface stays clean, because that is where it would come back first.
   */
  it('never sends a seat price or a currency to the server', async () => {
    mockedFetch.mockImplementation(async () => jsonResponse({ generatedUtc: '2026-09-01T00:00:00Z' }));

    await fetchAdoptionSummary(28, [1]);

    const query = new URL(lastUrl(), 'https://contoso.example').searchParams;
    expect(query.get('windowDays')).toBe('28');
    expect(query.get('seatLicenceTypeIds')).toBe('1');
    expect(query.get('seatCosts')).toBeNull();
    expect([...query.keys()].some((k) => /cost|currency|price/i.test(k))).toBe(false);
  });

  it('keeps the workbook export on the same scope, with no cost or comparison parameters', () => {
    const query = new URL(workbookExportUrl(90, [2]), 'https://contoso.example').searchParams;

    expect(query.get('windowDays')).toBe('90');
    expect(query.get('seatLicenceTypeIds')).toBe('2');
    // Comparison is done by diffing two exported workbooks, so the export takes no comparison
    // parameter and the server stores no period to compare against.
    expect(query.get('comparisonMode')).toBeNull();
    expect([...query.keys()].some((k) => /cost|currency|price/i.test(k))).toBe(false);
  });
});

describe('copilotAdoptionApi polling', () => {
  const runId = '00000000000000000000000000000009';

  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    setActiveLanguage('en');
  });

  /** Polls a still-building analysis until the client's ten-minute ceiling, and returns the error. */
  async function giveUpError(body: Record<string, unknown>): Promise<Error> {
    mockedFetch.mockImplementation(async () => jsonResponse(body, 202));

    const pending = fetchAdoptionSummary(28).then(
      () => new Error('the poll should have given up'),
      (error: Error) => error,
    );
    await vi.advanceTimersByTimeAsync(11 * 60 * 1000);
    return pending;
  }

  it('quotes the run the server named, so a screenshot identifies it in telemetry', async () => {
    const error = await giveUpError({ status: 'building', retryAfterSeconds: 5, runId });

    expect(error.message).toContain(runId);
    expect(mockedFetch.mock.calls.length).toBeGreaterThan(1);
  });

  it('keeps the plain message when the server names no run', async () => {
    const error = await giveUpError({ status: 'building', retryAfterSeconds: 5 });

    expect(error.message).not.toContain('{runId}');
    expect(error.message).toMatch(/still running/i);
  });

  it('quotes the reference in Spanish too', async () => {
    // Loaded before the fake clock matters: the Spanish catalog is a lazily imported chunk, and until it
    // is in memory translateActive falls back to English.
    await loadCatalog('es');
    setActiveLanguage('es');
    const error = await giveUpError({ status: 'building', retryAfterSeconds: 5, runId });

    expect(error.message).toContain(runId);
    expect(error.message).toMatch(/referencia/);
  });
});
