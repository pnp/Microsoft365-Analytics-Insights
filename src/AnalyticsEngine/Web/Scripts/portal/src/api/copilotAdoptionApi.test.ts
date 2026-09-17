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
  it('sends configured seat costs to the server instead of dropping them', async () => {
    mockedFetch.mockImplementation(async () => jsonResponse({ generatedUtc: '2026-09-01T00:00:00Z' }));

    await fetchAdoptionSummary(28, [1], undefined, [
      {
        skuPartNumber: 'Microsoft_365_Copilot',
        currency: 'GBP',
        cost: 30,
        period: 'monthly',
        effectiveDateUtc: '2026-09-01T00:00:00Z',
      },
    ]);

    const url = lastUrl();
    const query = new URL(url, 'https://contoso.example').searchParams;
    expect(query.get('windowDays')).toBe('28');
    expect(query.get('seatLicenceTypeIds')).toBe('1');
    expect(JSON.parse(query.get('seatCosts') ?? '[]')).toEqual([
      {
        skuPartNumber: 'Microsoft_365_Copilot',
        currency: 'GBP',
        cost: 30,
        period: 'monthly',
        effectiveDateUtc: '2026-09-01T00:00:00Z',
      },
    ]);
  });

  it('includes the same seat costs in workbook export URLs', () => {
    const url = workbookExportUrl(90, undefined, [
      {
        skuPartNumber: 'Microsoft_365_Copilot',
        currency: 'EUR',
        cost: 25,
        period: 'monthly',
        effectiveDateUtc: '2026-09-01T00:00:00Z',
      },
    ]);

    const query = new URL(url, 'https://contoso.example').searchParams;
    expect(JSON.parse(query.get('seatCosts') ?? '[]')[0]).toMatchObject({
      skuPartNumber: 'Microsoft_365_Copilot',
      currency: 'EUR',
    });
  });
});
