import { describe, it, expect, beforeEach, vi } from 'vitest';
import { apiFetch } from './http';
import { LicenceActivityApiError, downloadExport, fetchOverview, fetchUsers } from './licenceActivityApi';

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

describe('licenceActivityApi query building', () => {
  it('builds the overview query with only the filters that are set (0 is a real demographic id)', async () => {
    mockedFetch.mockImplementation(async () => jsonResponse({ snapshotId: 'ov1' }));

    await fetchOverview({ from: '2026-05-01', to: '2026-05-19' });
    expect(lastUrl()).toContain('/api/LicenceActivity/overview?from=2026-05-01&to=2026-05-19');
    expect(lastUrl()).not.toContain('departmentId');

    await fetchOverview({ from: '2026-05-01', to: '2026-05-19', departmentId: 0, countryId: 7 });
    expect(lastUrl()).toContain('departmentId=0');
    expect(lastUrl()).toContain('countryId=7');
  });

  it('sends top, page, pageSize, sort and direction together and trims the search', async () => {
    mockedFetch.mockImplementation(async () => jsonResponse({ snapshotId: 'u1' }));

    await fetchUsers({
      overviewId: 'ov1',
      licenceTypeId: 3,
      workload: 'copilot',
      top: 25,
      sort: 'activity',
      direction: 'desc',
      search: '  ada  ',
      page: 2,
      pageSize: 50,
    });
    const url = lastUrl();
    expect(url).toContain('workload=copilot');
    expect(url).toContain('top=25');
    expect(url).toContain('page=2');
    expect(url).toContain('pageSize=50');
    expect(url).toContain('sort=activity');
    expect(url).toContain('direction=desc');
    expect(url).toContain('search=ada');
  });
});

describe('licenceActivityApi error mapping', () => {
  it('maps statuses to kinds and prefers the server message', async () => {
    const cases: { status: number; kind: string }[] = [
      { status: 503, kind: 'busy' },
      { status: 410, kind: 'expired' },
      { status: 409, kind: 'expired' },
      { status: 404, kind: 'expired' },
      { status: 403, kind: 'forbidden' },
      { status: 412, kind: 'precondition' },
      { status: 400, kind: 'badRequest' },
    ];

    for (const { status, kind } of cases) {
      mockedFetch.mockImplementation(async () => jsonResponse({ message: `msg ${status}` }, status));
      await expect(fetchOverview({ from: '2026-05-01', to: '2026-05-19' })).rejects.toMatchObject({
        kind,
        status,
        message: `msg ${status}`,
      });
    }
  });

  it('falls back to a generated message when the server sends none', async () => {
    mockedFetch.mockImplementation(async () => new Response(null, { status: 503 }));
    await expect(fetchOverview({ from: '2026-05-01', to: '2026-05-19' })).rejects.toMatchObject({
      kind: 'busy',
      message: expect.stringMatching(/busy/i),
    });
  });
});

describe('downloadExport', () => {
  beforeEach(() => {
    Object.defineProperty(URL, 'createObjectURL', { value: vi.fn(() => 'blob:stub'), configurable: true });
    Object.defineProperty(URL, 'revokeObjectURL', { value: vi.fn(), configurable: true });
  });

  it('omits usersId for an aggregate-only export and includes it otherwise', async () => {
    mockedFetch.mockImplementation(async () => new Response('xlsx-bytes', { status: 200 }));

    await downloadExport({ overviewId: 'ov1' });
    expect(lastUrl()).toContain('/api/LicenceActivity/export?overviewId=ov1');
    expect(lastUrl()).not.toContain('usersId');

    await downloadExport({ overviewId: 'ov1', usersId: 'us9' });
    expect(lastUrl()).toContain('usersId=us9');
    expect(URL.createObjectURL).toHaveBeenCalled();
  });

  it('surfaces an expired/mismatched snapshot instead of downloading fresh data', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ message: 'The snapshots do not match.' }, 409));
    await expect(downloadExport({ overviewId: 'ov1', usersId: 'us9' })).rejects.toMatchObject({
      kind: 'expired',
    });
    expect(URL.createObjectURL).not.toHaveBeenCalled();

    mockedFetch.mockResolvedValue(new Response(null, { status: 410 }));
    await expect(downloadExport({ overviewId: 'ov1' })).rejects.toBeInstanceOf(LicenceActivityApiError);
  });

  it('never saves a JSON error body that leaked as HTTP 200 as an .xlsx', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ message: 'not a workbook' }, 200));
    await expect(downloadExport({ overviewId: 'ov1' })).rejects.toBeInstanceOf(LicenceActivityApiError);
    expect(URL.createObjectURL).not.toHaveBeenCalled();
  });
});
