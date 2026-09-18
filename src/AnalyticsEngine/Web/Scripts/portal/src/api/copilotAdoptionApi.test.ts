import { describe, it, expect, beforeEach, vi } from 'vitest';
import { apiFetch } from './http';
import { fetchAdoptionSummary, workbookExportUrl } from './copilotAdoptionApi';

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

  it('keeps the workbook export on the same scope, with no cost parameters', () => {
    const query = new URL(workbookExportUrl(90, [2], 'previousClosedPeriod'), 'https://contoso.example')
      .searchParams;

    expect(query.get('windowDays')).toBe('90');
    expect(query.get('seatLicenceTypeIds')).toBe('2');
    expect(query.get('comparisonMode')).toBe('previousClosedPeriod');
    expect([...query.keys()].some((k) => /cost|currency|price/i.test(k))).toBe(false);
  });
});
