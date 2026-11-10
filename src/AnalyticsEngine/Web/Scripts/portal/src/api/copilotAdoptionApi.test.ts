import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { apiFetch, PortalPermissionError } from './http';
import {
  AdoptionGenerationChangedError, AdoptionRequestError, canRetryAdoptionRequest,
  fetchAdoptionFilters, fetchAdoptionSummary, rebuildAdoption, workbookExportUrl,
} from './copilotAdoptionApi';
import { loadCatalog, setActiveLanguage } from '../i18n';

vi.mock('./http', async importOriginal => ({
  ...await importOriginal<typeof import('./http')>(),
  apiFetch: vi.fn(),
}));

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

  it('reports real progress and retains the after-generation hint without starting another rebuild', async () => {
    const onProgress = vi.fn();
    const progress = { generationId: runId, phase: 'analysing', elapsedMs: 5000, percentComplete: 40, steps: [] };
    mockedFetch.mockResolvedValueOnce(jsonResponse({ retryAfterSeconds: 1, progress }, 202))
      .mockResolvedValueOnce(jsonResponse({ generatedUtc: '2026-01-01T00:00:00Z' }));
    const result = fetchAdoptionSummary(28, undefined, undefined, null, null, null, { after: runId, onProgress });
    await vi.advanceTimersByTimeAsync(1000);
    await result;
    expect(onProgress).toHaveBeenCalledWith(progress);
    for (const [url, init] of mockedFetch.mock.calls) {
      expect(new URL(String(url)).searchParams.get('after')).toBe(runId);
      expect(new URL(String(url)).searchParams.get('includeProgress')).toBe('true');
      expect(init?.method).toBe('GET');
    }
  });

  it('aborts progress polling without issuing a new request', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ retryAfterSeconds: 5 }, 202));
    const controller = new AbortController();
    const result = fetchAdoptionSummary(28, undefined, controller.signal).catch(error => error);
    await vi.advanceTimersByTimeAsync(0);
    controller.abort();
    expect((await result).name).toBe('AbortError');
    await vi.advanceTimersByTimeAsync(10_000);
    expect(mockedFetch).toHaveBeenCalledTimes(1);
  });

  it('uses a one-shot POST for rebuild and preserves typed cooldown errors', async () => {
    mockedFetch.mockResolvedValueOnce(jsonResponse({ generationId: runId }, 202));
    await rebuildAdoption(28, runId);
    expect(mockedFetch).toHaveBeenCalledTimes(1);
    expect(mockedFetch.mock.calls[0][1]).toMatchObject({
      method: 'POST', body: JSON.stringify({ expectedGenerationId: runId }),
    });
    mockedFetch.mockResolvedValueOnce(jsonResponse({ code: 'copilotAdoption.error.rebuildCooldown', retryAfterSeconds: 45 }, 429));
    const error = await rebuildAdoption(28, runId).catch(error => error);
    expect(error).toBeInstanceOf(AdoptionRequestError);
    expect(error.retryAfterSeconds).toBe(45);
    expect(canRetryAdoptionRequest(error)).toBe(true);
    expect(canRetryAdoptionRequest(new PortalPermissionError('seePii', 'Portal.SeePII'))).toBe(false);
  });

  it.each([200, 503])('returns a safe retryable error for non-JSON rebuild responses (HTTP %i)', async status => {
    mockedFetch.mockResolvedValueOnce(new Response('<html>Temporary failure</html>', { status }));
    const error = await rebuildAdoption(28, runId).catch(error => error);
    expect(error).toBeInstanceOf(AdoptionRequestError);
    expect(error.message).not.toContain('<html>');
    expect(canRetryAdoptionRequest(error)).toBe(true);
    expect(mockedFetch).toHaveBeenCalledTimes(1);
  });

  it('rejects a supporting response from a different report generation', async () => {
    const response = jsonResponse({ emailDomains: ['contoso.com'] });
    response.headers.set('X-CopilotAdoption-GenerationId', '00000000000000000000000000000010');
    mockedFetch.mockResolvedValueOnce(response);
    await expect(fetchAdoptionFilters(28, undefined, undefined, null, { generationId: runId }))
      .rejects.toBeInstanceOf(AdoptionGenerationChangedError);
  });
});
