import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import {
  ActivityAnalysisApiError,
  MAX_BUSY_RETRIES,
  fetchActivityAnalysisAvailability,
  fetchActivityAnalysisPeople,
  fetchActivityAnalysisReport,
  peopleParams,
  reportParams,
  serializeRanges,
} from './activityAnalysisApi';
import { loadCatalog } from '../i18n';
import { setActiveLanguage } from '../i18n/runtime';
import type { ActivityAnalysisQuery } from '../types/activityAnalysis';

vi.mock('./http', () => ({ apiFetch: vi.fn() }));

const mockedFetch = vi.mocked(apiFetch);

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json', ...headers } });
}

function lastUrl(): URL {
  const calls = mockedFetch.mock.calls;
  return new URL(String(calls[calls.length - 1][0]));
}

const BASE_QUERY: ActivityAnalysisQuery = {
  from: '2025-10-06',
  to: '2026-09-28',
  metrics: ['teams.calls', 'teams.meetings'],
};

beforeEach(() => {
  mockedFetch.mockReset();
  setActiveLanguage('en');
});

afterEach(() => {
  vi.useRealTimers();
});

describe('activityAnalysisApi query building', () => {
  it('sends the period and the metrics, and nothing that was not set', () => {
    const params = reportParams(BASE_QUERY);
    expect(params.get('from')).toBe('2025-10-06');
    expect(params.get('to')).toBe('2026-09-28');
    expect(params.get('metrics')).toBe('teams.calls,teams.meetings');
    expect(params.has('userFilter')).toBe(false);
    expect(params.has('licences')).toBe(false);
    expect(params.has('ranges')).toBe(false);

    const empty = reportParams({ ...BASE_QUERY, userFilter: null, licences: [], ranges: [{ metric: 'teams.calls', min: null, max: null }] });
    expect(empty.has('userFilter')).toBe(false);
    expect(empty.has('licences')).toBe(false);
    expect(empty.has('ranges')).toBe(false);
  });

  it('writes each range as key:min:max with an open bound left empty', () => {
    expect(
      serializeRanges([
        { metric: 'teams.calls', min: 5, max: null },
        { metric: 'outlook.emailsSent', min: null, max: 100 },
        { metric: 'teams.audioDuration', min: 3600, max: 7200 },
        { metric: 'teams.meetings', min: null, max: null },
      ]),
    ).toBe('teams.calls:5:,outlook.emailsSent::100,teams.audioDuration:3600:7200');
    expect(serializeRanges([])).toBeNull();
    expect(serializeRanges(undefined)).toBeNull();
  });

  it('sends the user filter, licences and ranges together on the report request', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({}));
    const userFilter = JSON.stringify([{ d: 'department', v: ['Sales, EMEA', 'Καλημέρα κόσμε'] }]);

    await fetchActivityAnalysisReport({
      ...BASE_QUERY,
      userFilter,
      licences: [7, 12],
      ranges: [
        { metric: 'teams.calls', min: 5, max: null },
        { metric: 'outlook.emailsSent', min: null, max: 100 },
      ],
    });

    const url = lastUrl();
    expect(url.pathname).toBe('/api/ActivityAnalysis/report');
    expect(url.searchParams.get('metrics')).toBe('teams.calls,teams.meetings');
    expect(url.searchParams.get('licences')).toBe('7,12');
    expect(url.searchParams.get('ranges')).toBe('teams.calls:5:,outlook.emailsSent::100');
    // The filter survives the round trip exactly, commas, non-Latin text and all.
    expect(url.searchParams.get('userFilter')).toBe(userFilter);
  });

  it('leaves the list separators readable in the address', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({}));
    await fetchActivityAnalysisReport({ ...BASE_QUERY, ranges: [{ metric: 'teams.calls', min: 5, max: null }] });

    const raw = String(mockedFetch.mock.calls[0][0]);
    expect(raw).toContain('metrics=teams.calls,teams.meetings');
    expect(raw).toContain('ranges=teams.calls:5:');
  });

  it('narrows a people request to a department, the "not set" bucket or nobody', async () => {
    const department = peopleParams({ ...BASE_QUERY, department: 'Sales', sort: 'teams.meetings', top: 100 });
    expect(department.get('department')).toBe('Sales');
    expect(department.has('noDepartment')).toBe(false);
    expect(department.get('sort')).toBe('teams.meetings');
    expect(department.get('top')).toBe('100');

    const notSet = peopleParams({ ...BASE_QUERY, department: undefined, noDepartment: true });
    expect(notSet.get('noDepartment')).toBe('true');
    expect(notSet.has('department')).toBe(false);

    const everyone = peopleParams({ ...BASE_QUERY, top: 10 });
    expect(everyone.has('department')).toBe(false);
    expect(everyone.has('noDepartment')).toBe(false);
    expect(everyone.has('sort')).toBe(false);

    mockedFetch.mockResolvedValue(jsonResponse({ people: [] }));
    await fetchActivityAnalysisPeople({ ...BASE_QUERY, department: 'Καλημέρα κόσμε', top: 25 });
    expect(lastUrl().pathname).toBe('/api/ActivityAnalysis/people');
    expect(lastUrl().searchParams.get('department')).toBe('Καλημέρα κόσμε');
  });
});

describe('activityAnalysisApi errors', () => {
  it('words every coded refusal in the reader’s language, never in the server’s English', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    const cases: { status: number; code: string; kind: string; text: RegExp }[] = [
      { status: 400, code: 'invalidPeriod', kind: 'badRequest', text: /periodo no es válido/ },
      { status: 400, code: 'invalidMetric', kind: 'badRequest', text: /métricas seleccionadas ya no está disponible/ },
      { status: 400, code: 'invalidRange', kind: 'badRequest', text: /rangos de actividad no es válido/ },
      { status: 400, code: 'invalidFilter', kind: 'badRequest', text: /filtro de personas/ },
      { status: 412, code: 'notInstalled', kind: 'notInstalled', text: /no está instalado/ },
    ];

    for (const { status, code, kind, text } of cases) {
      mockedFetch.mockResolvedValue(jsonResponse({ code, message: 'The server wrote English.' }, status));
      const error = await fetchActivityAnalysisReport(BASE_QUERY).catch((e: unknown) => e);
      expect(error).toBeInstanceOf(ActivityAnalysisApiError);
      expect(error).toMatchObject({ kind, status, code });
      expect((error as Error).message).toMatch(text);
      expect((error as Error).message).not.toContain('The server wrote English');
    }
  });

  it('falls back to the status when the server sends no code it knows', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'somethingNew', message: 'Unknown.' }, 400));
    await expect(fetchActivityAnalysisReport(BASE_QUERY)).rejects.toMatchObject({
      kind: 'badRequest',
      message: 'The request wasn’t valid. Reload the page and try again.',
    });

    mockedFetch.mockResolvedValue(new Response(null, { status: 412 }));
    await expect(fetchActivityAnalysisReport(BASE_QUERY)).rejects.toMatchObject({ kind: 'notInstalled' });

    mockedFetch.mockResolvedValue(new Response('oops', { status: 500 }));
    await expect(fetchActivityAnalysisReport(BASE_QUERY)).rejects.toMatchObject({
      kind: 'http',
      message: 'Couldn’t load the activity analysis (500).',
    });

    mockedFetch.mockResolvedValue(new Response(null, { status: 502 }));
    await expect(fetchActivityAnalysisAvailability()).rejects.toMatchObject({
      message: 'Couldn’t check whether activity analysis is available (502).',
    });

    mockedFetch.mockResolvedValue(new Response(null, { status: 500 }));
    await expect(fetchActivityAnalysisPeople(BASE_QUERY)).rejects.toMatchObject({
      message: 'Couldn’t load the people (500).',
    });
  });

  it('retries a busy answer after the time the server asks for, then succeeds', async () => {
    vi.useFakeTimers();
    mockedFetch
      .mockResolvedValueOnce(jsonResponse({ code: 'activityAnalysisBusy', message: 'Busy.' }, 503, { 'Retry-After': '5' }))
      .mockResolvedValueOnce(jsonResponse({ matchingPeople: 3 }));

    const promise = fetchActivityAnalysisReport(BASE_QUERY);
    await vi.advanceTimersByTimeAsync(4900);
    expect(mockedFetch).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(200);

    await expect(promise).resolves.toMatchObject({ matchingPeople: 3 });
    expect(mockedFetch).toHaveBeenCalledTimes(2);
  });

  it('gives up after a bounded number of busy answers and says so', async () => {
    vi.useFakeTimers();
    mockedFetch.mockImplementation(async () => jsonResponse({ code: 'activityAnalysisBusy' }, 503, { 'Retry-After': '1' }));

    const promise = fetchActivityAnalysisReport(BASE_QUERY).catch((e: unknown) => e);
    await vi.advanceTimersByTimeAsync(10_000);

    const error = await promise;
    expect(error).toMatchObject({ kind: 'busy', code: 'activityAnalysisBusy' });
    expect((error as Error).message).toBe('Another period of activity is being loaded right now. Try again in a few seconds.');
    expect(mockedFetch).toHaveBeenCalledTimes(MAX_BUSY_RETRIES + 1);
  });

  it('does not retry a 503 that is not the busy answer', async () => {
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'somethingElse' }, 503));
    await expect(fetchActivityAnalysisReport(BASE_QUERY)).rejects.toMatchObject({ kind: 'busy' });
    expect(mockedFetch).toHaveBeenCalledTimes(1);
  });

  it('stops waiting to retry when the request is abandoned', async () => {
    vi.useFakeTimers();
    mockedFetch.mockResolvedValue(jsonResponse({ code: 'activityAnalysisBusy' }, 503, { 'Retry-After': '5' }));
    const controller = new AbortController();

    const promise = fetchActivityAnalysisReport(BASE_QUERY, controller.signal).catch((e: unknown) => e);
    await vi.advanceTimersByTimeAsync(1000);
    controller.abort();

    expect(await promise).toMatchObject({ name: 'AbortError' });
    expect(mockedFetch).toHaveBeenCalledTimes(1);
  });
});
