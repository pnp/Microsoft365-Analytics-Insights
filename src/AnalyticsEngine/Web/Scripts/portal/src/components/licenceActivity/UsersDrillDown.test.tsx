import { describe, it, expect, beforeEach, vi } from 'vitest';
import type { ComponentProps } from 'react';
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
    snapshotId: `snap-${params.overviewId}-${params.page}-${params.top}-${params.workload}`,
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

function renderDrill(
  coverage: LicenceActivityCoverage[] = [],
  props: Partial<ComponentProps<typeof UsersDrillDown>> = {},
) {
  return renderWithProvider(
    <UsersDrillDown
      overviewId="ov1"
      overviewScope="scope-a"
      licence={licence}
      coverage={coverage}
      onUsersSnapshot={vi.fn()}
      onRefreshOverview={vi.fn()}
      {...props}
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

  it('strips C1 controls too, but leaves accented letters alone', async () => {
    // .NET char.IsControl - which is what the server tests - covers C0, DEL AND C1 (U+0080-U+009F).
    // The first version of this sanitiser stopped at DEL, so a pasted U+0085 NEL still reached the
    // server and still produced the 400 the sanitiser exists to prevent. The positive direction
    // matters just as much: U+00A0 and the accented Latin-1 letters just above the C1 block are NOT
    // controls, and eating them would corrupt perfectly valid non-English UPN searches.
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Search users'), { target: { value: 'ada\u0085smith' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => expect(lastParams()).toMatchObject({ search: 'ada smith' }));
  });

  it('leaves non-ASCII letters in a search untouched', async () => {
    renderDrill();
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Search users'), { target: { value: 'ándré.Καλημέρα@contoso.com' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => expect(lastParams()).toMatchObject({ search: 'ándré.Καλημέρα@contoso.com' }));
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

  // Regression for the expiry-recovery page-reset bug. `overviewId` is a per-snapshot generation id:
  // an expired snapshot is re-fetched under a NEW id for the SAME logical query. Keying the page
  // reset off `overviewId` bounced an admin browsing page 2 straight back to page 1 every time the
  // snapshot re-minted. The reset must key off `overviewScope` (the logical window/demographic
  // identity), which is unchanged by a re-mint.
  it('keeps the browse page - and the workload/search/sort - when the overview re-mints under a NEW id for the SAME scope', async () => {
    const onUsersSnapshot = vi.fn();
    const { rerender } = renderDrill([], { overviewId: 'ov1', overviewScope: 'scope-a', onUsersSnapshot });
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());

    // Move well away from the defaults: a non-default workload, sort and search, then page 2.
    fireEvent.change(screen.getByLabelText('Workload'), { target: { value: 'outlook' } });
    await waitFor(() => expect(lastParams()).toMatchObject({ workload: 'outlook', page: 1 }));
    fireEvent.change(screen.getByLabelText('Sort users'), { target: { value: 'upn:asc' } });
    await waitFor(() => expect(lastParams()).toMatchObject({ sort: 'upn', direction: 'asc' }));
    fireEvent.change(screen.getByLabelText('Search users'), { target: { value: 'ada' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => expect(lastParams()).toMatchObject({ search: 'ada', page: 1 }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() =>
      expect(lastParams()).toMatchObject({
        overviewId: 'ov1',
        page: 2,
        workload: 'outlook',
        sort: 'upn',
        direction: 'asc',
        search: 'ada',
      }),
    );

    const callsBefore = mockUsers.mock.calls.length;
    onUsersSnapshot.mockClear();

    // The overview snapshot expired and was re-minted under a new id - but the reporting window,
    // demographics and licence (its logical scope) are unchanged.
    rerender(
      <UsersDrillDown
        overviewId="ov2"
        overviewScope="scope-a"
        licence={licence}
        coverage={[]}
        onUsersSnapshot={onUsersSnapshot}
        onRefreshOverview={vi.fn()}
      />,
    );

    // The new id forces a fresh users fetch, but the admin stays on page 2 with the same view...
    await waitFor(() => expect(mockUsers.mock.calls.length).toBeGreaterThan(callsBefore));
    expect(lastParams()).toMatchObject({
      overviewId: 'ov2',
      page: 2,
      workload: 'outlook',
      sort: 'upn',
      direction: 'asc',
      search: 'ada',
    });

    // ...and the export snapshot follows the freshly minted list (a NEW snapshot bound to ov2). The
    // final reported snapshot is the ov2 one, so the export can only reference the fresh list.
    const freshSnapshot = `snap-ov2-2-10-outlook`;
    await waitFor(() => {
      const calls = onUsersSnapshot.mock.calls;
      expect(calls[calls.length - 1][0]).toBe(freshSnapshot);
    });
  });

  // The opposite direction: a genuine logical-scope change (a new reporting window or demographic
  // filter, surfaced here as a changed `overviewScope`) MUST reset to page 1, and the fresh fetch
  // must go against the new snapshot - no old page, no old rows carried over.
  it('resets to page 1 when the logical scope actually changes', async () => {
    const { rerender } = renderDrill([], { overviewId: 'ov1', overviewScope: 'scope-a' });
    await waitFor(() => expect(mockUsers).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(lastParams()).toMatchObject({ overviewId: 'ov1', page: 2 }));

    const callsBefore = mockUsers.mock.calls.length;
    // A new reporting window / demographic filter: BOTH the snapshot id and the logical scope change.
    rerender(
      <UsersDrillDown
        overviewId="ov2"
        overviewScope="scope-b"
        licence={licence}
        coverage={[]}
        onUsersSnapshot={vi.fn()}
        onRefreshOverview={vi.fn()}
      />,
    );
    await waitFor(() => expect(mockUsers.mock.calls.length).toBeGreaterThan(callsBefore));
    expect(lastParams()).toMatchObject({ overviewId: 'ov2', page: 1 });
  });
});
