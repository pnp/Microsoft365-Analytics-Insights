import { describe, it, expect, beforeEach, vi } from 'vitest';
import { apiFetch } from './http';
import { LicenceActivityApiError, downloadExport, fetchOverview, fetchUsers } from './licenceActivityApi';
import { loadCatalog } from '../i18n';
import { setActiveLanguage } from '../i18n/runtime';

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
  setActiveLanguage('en');
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
  it('maps known statuses to kinds and uses catalogued messages instead of server English', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
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
      mockedFetch.mockImplementation(async () => jsonResponse({ message: `The server wrote English ${status}.` }, status));
      await expect(fetchOverview({ from: '2026-05-01', to: '2026-05-19' })).rejects.toMatchObject({
        kind,
        status,
        message: expect.not.stringContaining('The server wrote English'),
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

  it('surfaces cold-range admission pressure as the localised busy condition without polling', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    mockedFetch.mockResolvedValue(jsonResponse({
      code: 'anotherReportPreparing',
      message: 'Another licence report snapshot is loading. Retry in a few seconds.',
    }, 503));
    await expect(fetchOverview({ from: '2026-05-01', to: '2026-05-19' })).rejects.toMatchObject({
      kind: 'busy',
      message: 'Se está preparando otro informe de licencias ahora mismo. Inténtelo de nuevo en unos segundos.',
    });
    expect(mockedFetch).toHaveBeenCalledTimes(1);
  });

  it('uses coded validation messages instead of the generic bad-request text', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({
      code: 'dateRange',
      message: 'Choose 7 to 180 inclusive UTC dates, ending before today. Custom ranges are never rounded.',
    }, 400));

    await expect(fetchOverview({ from: '2026-05-01', to: '2026-05-19' })).rejects.toMatchObject({
      kind: 'badRequest',
      message: 'Choose 7 to 180 inclusive UTC dates, ending before today. Custom ranges are never rounded.',
    });
  });

  it('keeps the generic localised message when a non-http status has no recognised code', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ message: 'A future licence error.' }, 410));

    await expect(fetchOverview({ from: '2026-05-01', to: '2026-05-19' })).rejects.toMatchObject({
      kind: 'expired',
      message: 'These figures are no longer being held. Refresh the report to bring back an up-to-date set.',
    });
  });

  it('keeps the failed run reference in either language when a snapshot could not be loaded', async () => {
    // LicenceActivityFailedException's reply: the English message still carries the reference, and the
    // code and reference travel beside it so a Spanish reader gets both the sentence and the reference.
    const serverMessage = 'Licence activity could not be loaded. Retry the request. Reference: run-7f3a';
    mockedFetch.mockImplementation(async () => jsonResponse({ code: 'loadFailed', message: serverMessage, reference: 'run-7f3a' }, 503));

    setActiveLanguage('en');
    await expect(fetchOverview({ from: '2026-05-01', to: '2026-05-19' })).rejects.toMatchObject({
      kind: 'busy',
      message: serverMessage,
    });

    await loadCatalog('es');
    setActiveLanguage('es');
    await expect(fetchOverview({ from: '2026-05-01', to: '2026-05-19' })).rejects.toMatchObject({
      kind: 'busy',
      message: 'No se pudo cargar la actividad de licencias. Vuelva a intentar la solicitud. Referencia: run-7f3a',
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
