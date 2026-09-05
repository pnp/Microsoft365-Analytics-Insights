import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, fireEvent, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import UsersDrillDown from './UsersDrillDown';
import { fetchUsers } from '../../api/licenceActivityApi';
import type {
  LicenceActivityCoverage,
  LicenceActivitySku,
  LicenceActivityUser,
  LicenceActivityUsers,
  UsersParams,
} from '../../types/licenceActivity';

vi.mock('../../api/licenceActivityApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/licenceActivityApi')>();
  return { ...actual, fetchUsers: vi.fn() };
});

const mockUsers = vi.mocked(fetchUsers);

const licence: LicenceActivitySku = {
  licenceTypeId: 10,
  name: 'E5',
  skuId: 'ENTERPRISEPREMIUM',
  assignedUsers: 100,
  workloads: [],
};

function user(upn: string, workload: string): LicenceActivityUser {
  return {
    userId: upn.length,
    userPrincipalName: upn,
    department: 'Engineering',
    country: 'Greece',
    accountEnabled: true,
    workloads: [
      {
        workload,
        status: 'known',
        band: 'high',
        source: 'Usage reports',
        measure: 'active days',
        activeSamples: 10,
        observedSamples: 20,
        expectedSamples: 20,
        averageActions: 3.5,
        lastActivityUtc: '2026-05-19T00:00:00Z',
      },
    ],
  };
}

function usersResponse(params: UsersParams): LicenceActivityUsers {
  return {
    snapshotId: `snap-${params.page}-${params.top}-${params.workload}`,
    generatedUtc: '2026-05-20T00:00:00Z',
    expiresUtc: '2026-05-20T00:02:00Z',
    overviewId: params.overviewId,
    query: {
      from: '2026-05-01',
      to: '2026-05-19',
      departmentId: null,
      countryId: null,
      licenceTypeId: params.licenceTypeId,
      workload: params.workload,
      search: params.search ?? '',
      sort: params.sort,
      direction: params.direction,
      top: params.top,
      page: params.page,
      pageSize: params.pageSize,
    },
    totalUsers: 120,
    rankedUsers: 120,
    mostActive: [user('ada@contoso.com', params.workload)],
    leastActive: [user('zoe@contoso.com', params.workload)],
    users: [user('ada@contoso.com', params.workload)],
    messages: [],
  };
}

function lastParams(): UsersParams {
  const calls = mockUsers.mock.calls;
  return calls[calls.length - 1][0];
}

beforeEach(() => {
  mockUsers.mockReset();
  mockUsers.mockImplementation(async (params) => usersResponse(params));
});

function coverageEntry(over: Partial<LicenceActivityCoverage> = {}): LicenceActivityCoverage {
  return {
    workload: 'teams',
    status: 'notImported',
    source: null,
    measure: null,
    granularity: null,
    message: null,
    effectiveFromUtc: null,
    effectiveToUtc: null,
    latestImportUtc: null,
    lagDays: 0,
    reportPeriodDays: null,
    expectedSamples: 0,
    observedSamples: 0,
    unmatchedUsers: 0,
    snapshotDates: [],
    ...over,
  };
}

function renderDrill(coverage: LicenceActivityCoverage[] = []) {
  return renderWithProvider(
    <UsersDrillDown
      overviewId="ov1"
      licence={licence}
      coverage={coverage}
      onUsersSnapshot={vi.fn()}
      onRefreshOverview={vi.fn()}
    />,
  );
}

describe('UsersDrillDown', () => {
  it('issues one activity-ranked request by default, and re-requests when top changes (clamped 1..100)', async () => {
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());
    expect(lastParams()).toMatchObject({
      workload: 'teams',
      top: 10,
      sort: 'activity',
      direction: 'desc',
      page: 1,
      pageSize: 50,
    });

    const topInput = screen.getByLabelText('Number of users in each list');
    fireEvent.change(topInput, { target: { value: '25' } });
    fireEvent.blur(topInput);
    await waitFor(() => expect(lastParams()).toMatchObject({ top: 25 }));

    fireEvent.change(topInput, { target: { value: '999' } });
    fireEvent.blur(topInput);
    await waitFor(() => expect(lastParams()).toMatchObject({ top: 100 }));
  });

  it('changes the workload', async () => {
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());
    fireEvent.change(screen.getByLabelText('Workload'), { target: { value: 'outlook' } });
    await waitFor(() => expect(lastParams()).toMatchObject({ workload: 'outlook' }));
  });

  it('browses with server-side search, sort and paging', async () => {
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    const search = screen.getByLabelText('Search users');
    fireEvent.change(search, { target: { value: 'ada' } });
    fireEvent.keyDown(search, { key: 'Enter' });
    await waitFor(() => expect(lastParams()).toMatchObject({ search: 'ada' }));

    fireEvent.change(screen.getByLabelText('Sort users'), { target: { value: 'upn:asc' } });
    await waitFor(() => expect(lastParams()).toMatchObject({ sort: 'upn', direction: 'asc' }));

    // total 120, pageSize 50 -> 3 pages.
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(lastParams()).toMatchObject({ page: 2 }));
  });

  // The server (LicenceActivityQuery.Create) throws -> HTTP 400 for a search over 100 characters or
  // containing ANY control character. A single-line <input> strips CR/LF itself (the HTML value
  // sanitization algorithm, in jsdom and real browsers alike), so a newline is NOT the reachable
  // case - the other C0/C1 controls are, and .trim() does not remove one from the middle of a paste.
  // Because the browse controls only render while `data` is non-null, a 400 caused by the search used
  // to unmount the very box holding the offending term, while every surviving control preserved that
  // term - an unrecoverable dead end. Each case gets its own render: this component submits a search
  // once per mount, so chaining several submissions into one test does not exercise what it claims.

  it('strips control characters from a search instead of submitting a guaranteed 400', async () => {
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    const search = screen.getByLabelText('Search users');
    fireEvent.change(search, { target: { value: 'ada\u0007lovelace' } });
    fireEvent.keyDown(search, { key: 'Enter' });

    await waitFor(() => expect(lastParams()).toMatchObject({ search: 'ada lovelace' }));
    expect(lastParams().search).not.toMatch(/[\u0000-\u001f\u007f]/);
  });

  it('caps an over-long search at the server limit', async () => {
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Search users'), { target: { value: 'a'.repeat(250) } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() => expect(lastParams().search).toHaveLength(100));
  });

  it('leaves an ordinary search term completely untouched', async () => {
    // The positive direction of the two guards above: sanitising must not mangle a real UPN search.
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Search users'), { target: { value: 'ada@contoso.com' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() => expect(lastParams()).toMatchObject({ search: 'ada@contoso.com' }));
  });

  it('keeps a way to clear the search when a failed load hides the browse controls', async () => {
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    mockUsers.mockRejectedValueOnce(new Error('boom'));
    fireEvent.change(screen.getByLabelText('Search users'), { target: { value: 'zzz' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));

    const clear = await screen.findByRole('button', { name: /Clear search and try again/ });
    fireEvent.click(clear);
    await waitFor(() => expect(lastParams()).toMatchObject({ search: '' }));
  });

  it('lets a space be typed mid-term (sanitising must not fight the space bar)', async () => {
    // Regression guard for a defect introduced by the sanitisation fix itself: trimming on every
    // change, with searchDraft fed back into the controlled input, erased a trailing space as soon as
    // it was typed, so "ada smith" could never be entered. Typing is simulated one keystroke at a
    // time on purpose - supplying the whole value in a single change event does NOT exercise this.
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    const search = screen.getByLabelText('Search users') as HTMLInputElement;
    for (const ch of 'ada smith') {
      fireEvent.change(search, { target: { value: search.value + ch } });
    }
    expect(search.value).toBe('ada smith');

    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => expect(lastParams()).toMatchObject({ search: 'ada smith' }));
  });

  it('still trims a term when it is committed', async () => {
    // The other direction: trimming did not disappear, it just moved to commit time.
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Search users'), { target: { value: '  ada@contoso.com  ' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));

    await waitFor(() => expect(lastParams()).toMatchObject({ search: 'ada@contoso.com' }));
  });

  it('explains why a workload cannot be ranked when its coverage is incomplete', async () => {
    renderDrill([coverageEntry({ workload: 'teams', status: 'notImported' })]);
    // The selected workload defaults to Teams, whose coverage is notImported here.
    expect(await screen.findByText(/Not imported/)).toBeInTheDocument();
  });

  it('renders API messages (coverage / ranking limitations)', async () => {
    mockUsers.mockImplementation(async (params) => ({
      ...usersResponse(params),
      messages: ['Teams ranking is limited by partial coverage.'],
    }));
    renderDrill();
    expect(await screen.findByText(/limited by partial coverage/)).toBeInTheDocument();
  });
});
