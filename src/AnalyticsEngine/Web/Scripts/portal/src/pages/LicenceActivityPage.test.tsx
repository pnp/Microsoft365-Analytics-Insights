import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, fireEvent, waitFor, act } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import LicenceActivityPage from './LicenceActivityPage';
import {
  fetchAvailability,
  fetchOverview,
  fetchUsers,
  downloadExport,
  LicenceActivityApiError,
} from '../api/licenceActivityApi';
import { WORKLOADS } from '../types/licenceActivity';
import type {
  LicenceActivityAvailability,
  LicenceActivityDistribution,
  LicenceActivityOverview,
  LicenceActivityQueryEcho,
  LicenceActivityUser,
  LicenceActivityUsers,
  WorkloadKey,
} from '../types/licenceActivity';

vi.mock('../api/licenceActivityApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/licenceActivityApi')>();
  return {
    ...actual,
    fetchAvailability: vi.fn(),
    fetchOverview: vi.fn(),
    fetchUsers: vi.fn(),
    downloadExport: vi.fn(),
  };
});

const mockAvailability = vi.mocked(fetchAvailability);
const mockOverview = vi.mocked(fetchOverview);
const mockUsers = vi.mocked(fetchUsers);
const mockDownload = vi.mocked(downloadExport);

const echo: LicenceActivityQueryEcho = {
  from: '2026-04-22',
  to: '2026-05-19',
  departmentId: null,
  countryId: null,
  licenceTypeId: 10,
  workload: 'teams',
  search: '',
  sort: 'activity',
  direction: 'desc',
  top: 10,
  page: 1,
  pageSize: 50,
};

function dist(workload: WorkloadKey): LicenceActivityDistribution {
  return { workload, high: 5, moderate: 3, low: 2, zero: 4, unknown: 1 };
}

function availability(over: Partial<LicenceActivityAvailability> = {}): LicenceActivityAvailability {
  return { available: true, canViewUsers: true, minimumDays: 7, maximumDays: 180, messages: [], ...over };
}

function overview(over: Partial<LicenceActivityOverview> = {}): LicenceActivityOverview {
  return {
    snapshotId: 'ov1',
    generatedUtc: '2026-05-20T10:00:00Z',
    expiresUtc: '2026-05-20T10:05:00Z',
    query: echo,
    distinctAssignedUsers: 120,
    licences: [
      { licenceTypeId: 10, name: 'E5', skuId: 'ENTERPRISEPREMIUM', assignedUsers: 100, workloads: WORKLOADS.map((w) => dist(w.key)) },
      { licenceTypeId: 20, name: 'F3', skuId: 'DESKLESSPACK', assignedUsers: 50, workloads: WORKLOADS.map((w) => dist(w.key)) },
    ],
    coverage: [
      {
        workload: 'teams',
        status: 'ok',
        source: 'Usage reports',
        measure: 'active days',
        granularity: 'daily',
        message: null,
        effectiveFromUtc: '2026-04-22T00:00:00Z',
        effectiveToUtc: '2026-05-19T00:00:00Z',
        latestImportUtc: '2026-05-20T00:00:00Z',
        lagDays: 1,
        reportPeriodDays: 28,
        expectedSamples: 100,
        observedSamples: 95,
        unmatchedUsers: 2,
        snapshotDates: [],
      },
    ],
    departments: [{ id: 1, name: 'Μηχανικοί', assignedUsers: 60, workloads: [] }],
    countries: [{ id: 1, name: 'Ελλάδα', assignedUsers: 60, workloads: [] }],
    demographicsTruncated: false,
    messages: [],
    ...over,
  };
}

function user(): LicenceActivityUser {
  return {
    userId: 1,
    userPrincipalName: 'ada@contoso.com',
    department: 'Engineering',
    country: 'Greece',
    accountEnabled: true,
    workloads: [
      {
        workload: 'teams',
        status: 'known',
        band: 'high',
        source: 'Usage reports',
        measure: 'active days',
        activeSamples: 12,
        observedSamples: 20,
        expectedSamples: 20,
        averageActions: 4.2,
        lastActivityUtc: '2026-05-19T00:00:00Z',
      },
    ],
  };
}

function usersResponse(): LicenceActivityUsers {
  return {
    snapshotId: 'us1',
    generatedUtc: '2026-05-20T10:00:00Z',
    expiresUtc: '2026-05-20T10:02:00Z',
    overviewId: 'ov1',
    query: echo,
    totalUsers: 1,
    rankedUsers: 1,
    mostActive: [user()],
    leastActive: [user()],
    users: [user()],
    messages: [],
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockOverview.mockResolvedValue(overview());
  mockUsers.mockResolvedValue(usersResponse());
  mockDownload.mockResolvedValue(undefined);
});

describe('LicenceActivityPage - availability', () => {
  it('shows an unavailable message and no export when the report cannot run', async () => {
    mockAvailability.mockResolvedValue(
      availability({ available: false, canViewUsers: false, messages: ['Enable GraphUsersMetadata.'] }),
    );
    renderWithProvider(<LicenceActivityPage />);

    expect(await screen.findByText(/not available on this deployment/i)).toBeInTheDocument();
    expect(screen.getByText('Enable GraphUsersMetadata.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Export to Excel/i })).not.toBeInTheDocument();
    expect(mockOverview).not.toHaveBeenCalled();
  });

  it('surfaces an availability check failure', async () => {
    mockAvailability.mockRejectedValue(new Error('availability boom'));
    renderWithProvider(<LicenceActivityPage />);
    expect(await screen.findByText('availability boom')).toBeInTheDocument();
  });
});

describe('LicenceActivityPage - user-detail gating', () => {
  it('shows aggregates but hides the drill-down without the ReadUsers role', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: false }));
    renderWithProvider(<LicenceActivityPage />);

    expect(await screen.findByText('Licence assignments')).toBeInTheDocument();
    // Aggregate workload distributions are visible to everyone (renders after default licence select).
    expect(await screen.findByText('Workload activity')).toBeInTheDocument();
    // Non-Latin (Greek) demographic values render without corruption (in the filters and the breakdown).
    expect(screen.getAllByText(/Μηχανικοί/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Ελλάδα/).length).toBeGreaterThan(0);
    // But not the per-user drill-down.
    expect(screen.queryByText('User drill-down')).not.toBeInTheDocument();
    expect(screen.getByText(/aggregate view/i)).toBeInTheDocument();
    expect(mockUsers).not.toHaveBeenCalled();
  });

  it('loads the drill-down for the default licence with the role', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: true }));
    renderWithProvider(<LicenceActivityPage />);

    expect(await screen.findByText('User drill-down')).toBeInTheDocument();
    expect((await screen.findAllByText('ada@contoso.com')).length).toBeGreaterThan(0);
    expect(mockUsers).toHaveBeenCalled();
  });
});

describe('LicenceActivityPage - scale (50 SKUs)', () => {
  it('holds all SKUs but issues exactly one users request, for the default (biggest) licence', async () => {
    // The skewed distribution the load fixture uses: a few tenant-wide SKUs, many tiny ones.
    const sizes = [1, 5, 25, 50, 100, 500, 5000, 15000, 60000];
    const licences = Array.from({ length: 50 }, (_, i) => ({
      licenceTypeId: i + 1,
      name: `SKU ${i + 1}`,
      skuId: `PART${i + 1}`,
      assignedUsers: sizes[i % sizes.length] + i, // varied, with a unique maximum
      workloads: WORKLOADS.map((w) => dist(w.key)),
    }));
    const biggest = [...licences].sort((a, b) => b.assignedUsers - a.assignedUsers)[0];

    mockAvailability.mockResolvedValue(availability({ canViewUsers: true }));
    mockOverview.mockResolvedValue(overview({ licences }));
    renderWithProvider(<LicenceActivityPage />);

    // The drill-down loads for exactly ONE licence (the biggest), not one request per SKU. The
    // longer timeout absorbs the heavier 50-SKU aggregate render under single-worker jsdom.
    await screen.findAllByText('ada@contoso.com', undefined, { timeout: 8000 });
    await act(async () => {});
    expect(mockUsers).toHaveBeenCalledTimes(1);
    expect(mockUsers.mock.calls[0][0].licenceTypeId).toBe(biggest.licenceTypeId);
  });
});

describe('LicenceActivityPage - export', () => {
  it('exports the aggregate snapshot (no usersId) for an aggregate-only viewer', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: false }));
    renderWithProvider(<LicenceActivityPage />);

    const exportBtn = await screen.findByRole('button', { name: /Export to Excel/i });
    await waitFor(() => expect(exportBtn).toBeEnabled());
    fireEvent.click(exportBtn);
    await waitFor(() => expect(mockDownload).toHaveBeenCalledWith({ overviewId: 'ov1', usersId: undefined }));
  });

  it('includes the current users snapshot once the drill-down has loaded', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: true }));
    renderWithProvider(<LicenceActivityPage />);

    await screen.findAllByText('ada@contoso.com'); // drill-down loaded -> usersId captured
    await act(async () => {}); // flush the onUsersSnapshot effect + page state update

    fireEvent.click(screen.getByRole('button', { name: /Export to Excel/i }));
    await waitFor(() => expect(mockDownload).toHaveBeenCalledWith({ overviewId: 'ov1', usersId: 'us1' }));
  });

  it('shows a refresh prompt when the snapshot has expired', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: false }));
    mockDownload.mockRejectedValue(
      new LicenceActivityApiError('expired', 410, 'This snapshot has expired or was refreshed.'),
    );
    renderWithProvider(<LicenceActivityPage />);

    const exportBtn = await screen.findByRole('button', { name: /Export to Excel/i });
    await waitFor(() => expect(exportBtn).toBeEnabled());
    fireEvent.click(exportBtn);

    expect(await screen.findByText(/expired/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeInTheDocument();
  });

  it('disables export while the overview is still loading', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: false }));
    mockOverview.mockReturnValue(new Promise(() => {})); // never resolves
    renderWithProvider(<LicenceActivityPage />);

    const exportBtn = await screen.findByRole('button', { name: /Export to Excel/i });
    expect(exportBtn).toBeDisabled();
  });
});

describe('LicenceActivityPage - export refresh after expiry', () => {
  it('re-mints the users snapshot on Refresh (same cached overviewId) without silently re-exporting', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: true }));
    // The overview stays cached: it returns the SAME snapshotId on reload, which is exactly the case
    // that made the old Refresh a no-op (the snapshot-id-changed effect never fired).
    mockOverview.mockResolvedValue(overview());
    // Each users fetch mints a fresh snapshot id, so a genuine re-mint is observable.
    const usersSnaps: string[] = [];
    mockUsers.mockImplementation(async () => {
      const id = `us${usersSnaps.length + 1}`;
      usersSnaps.push(id);
      return { ...usersResponse(), snapshotId: id };
    });
    // The first export fails as expired (users snapshots expire before the overview); later ones pass.
    mockDownload
      .mockRejectedValueOnce(
        new LicenceActivityApiError('expired', 410, 'This snapshot has expired or was refreshed.'),
      )
      .mockResolvedValue(undefined);

    renderWithProvider(<LicenceActivityPage />);
    await screen.findAllByText('ada@contoso.com');
    await act(async () => {}); // capture usersId = us1

    // Export -> 410 -> a Refresh prompt appears.
    fireEvent.click(screen.getByRole('button', { name: /Export to Excel/i }));
    expect(await screen.findByText(/expired/i)).toBeInTheDocument();

    const usersBefore = mockUsers.mock.calls.length;
    // The drill-down keeps its own "Refresh users" control (accessible name differs), so this exact
    // match resolves the export bar's Refresh unambiguously.
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    // A fresh users request is issued (re-mint) and the error clears...
    await waitFor(() => expect(mockUsers.mock.calls.length).toBeGreaterThan(usersBefore));
    await waitFor(() => expect(screen.queryByText(/expired/i)).not.toBeInTheDocument());
    await act(async () => {});
    // ...but Refresh does NOT silently re-export (still just the one failed attempt).
    expect(mockDownload).toHaveBeenCalledTimes(1);

    // A subsequent explicit export uses the same cached overviewId and the NEW users snapshot.
    fireEvent.click(screen.getByRole('button', { name: /Export to Excel/i }));
    await waitFor(() => {
      const last = mockDownload.mock.calls[mockDownload.mock.calls.length - 1][0];
      expect(last.overviewId).toBe('ov1');
      expect(last.usersId).toBe(usersSnaps[usersSnaps.length - 1]);
      expect(last.usersId).not.toBe('us1');
    });
    // Scope preserved across the refresh: still the same licence/overview.
    const lastUsers = mockUsers.mock.calls[mockUsers.mock.calls.length - 1][0];
    expect(lastUsers).toMatchObject({ overviewId: 'ov1', licenceTypeId: 10 });
  });
});

describe('LicenceActivityPage - browse page survives an expiry refresh', () => {
  const usersParams = () => mockUsers.mock.calls[mockUsers.mock.calls.length - 1][0];

  it('keeps the admin on page 2 (and workload/sort/search) when Refresh re-mints the overview under a NEW id', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: true }));

    // The overview re-mints on reload: ov1 first, ov2 after Refresh. A brand-new id per reload is
    // exactly the case that used to reset the browse page (the drill-down treated the id as scope).
    let ovCount = 0;
    mockOverview.mockImplementation(async () => {
      ovCount += 1;
      return overview({ snapshotId: ovCount === 1 ? 'ov1' : 'ov2' });
    });

    // A multi-page list whose snapshot id is bound to the overview id + page, so a re-mint is visible.
    mockUsers.mockImplementation(async (params) => ({
      ...usersResponse(),
      overviewId: params.overviewId,
      snapshotId: `us-${params.overviewId}-p${params.page}`,
      totalUsers: 120,
      query: {
        ...echo,
        licenceTypeId: params.licenceTypeId,
        workload: params.workload,
        search: params.search ?? '',
        sort: params.sort,
        direction: params.direction,
        top: params.top,
        page: params.page,
        pageSize: params.pageSize,
      },
    }));

    // The first export fails as expired (users snapshots expire before the overview); later ones pass.
    mockDownload
      .mockRejectedValueOnce(
        new LicenceActivityApiError('expired', 410, 'This snapshot has expired or was refreshed.'),
      )
      .mockResolvedValue(undefined);

    renderWithProvider(<LicenceActivityPage />);
    await screen.findAllByText('ada@contoso.com');
    await act(async () => {});

    // Move off the defaults: a non-default workload and sort and search, then browse to page 2.
    fireEvent.change(screen.getByLabelText('Workload'), { target: { value: 'outlook' } });
    await waitFor(() => expect(usersParams()).toMatchObject({ workload: 'outlook', page: 1 }));
    fireEvent.change(screen.getByLabelText('Sort users'), { target: { value: 'upn:asc' } });
    await waitFor(() => expect(usersParams()).toMatchObject({ sort: 'upn', direction: 'asc' }));
    fireEvent.change(screen.getByLabelText('Search users'), { target: { value: 'ada' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => expect(usersParams()).toMatchObject({ search: 'ada', page: 1 }));
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(usersParams()).toMatchObject({ overviewId: 'ov1', page: 2 }));

    // Export -> 410 -> a Refresh prompt appears.
    fireEvent.click(screen.getByRole('button', { name: /Export to Excel/i }));
    expect(await screen.findByText(/expired/i)).toBeInTheDocument();

    const usersBefore = mockUsers.mock.calls.length;
    // Exact 'Refresh' resolves the export bar's button (the drill-down's is 'Refresh users').
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    // The overview re-mints to ov2 and the list is re-fetched - but the admin is STILL on page 2 with
    // the same workload/sort/search. (With the bug the fresh id reset this to page 1.)
    await waitFor(() => expect(usersParams().overviewId).toBe('ov2'));
    expect(mockUsers.mock.calls.length).toBeGreaterThan(usersBefore);
    expect(usersParams()).toMatchObject({
      overviewId: 'ov2',
      page: 2,
      workload: 'outlook',
      sort: 'upn',
      direction: 'asc',
      search: 'ada',
    });

    await waitFor(() => expect(screen.queryByText(/expired/i)).not.toBeInTheDocument());
    await act(async () => {});
    // Refresh alone must not silently re-export.
    expect(mockDownload).toHaveBeenCalledTimes(1);

    // A subsequent explicit export references the fresh overview id AND the fresh users snapshot.
    fireEvent.click(screen.getByRole('button', { name: /Export to Excel/i }));
    await waitFor(() => {
      const last = mockDownload.mock.calls[mockDownload.mock.calls.length - 1][0];
      expect(last.overviewId).toBe('ov2');
      expect(last.usersId).toBe('us-ov2-p2');
    });
  });
});

describe('LicenceActivityPage - a real scope change resets the browse page', () => {
  const usersParams = () => mockUsers.mock.calls[mockUsers.mock.calls.length - 1][0];

  it('returns to page 1 and drops the stale export id when the demographic filter changes', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: true }));
    // A distinct overview id per demographic scope; department 1 stays in the catalogue so it remains
    // selectable after the scoped reply.
    mockOverview.mockImplementation(async (q) => overview({ snapshotId: `ov-dept-${q.departmentId ?? 'all'}` }));
    mockUsers.mockImplementation(async (params) => ({
      ...usersResponse(),
      overviewId: params.overviewId,
      snapshotId: `us-${params.overviewId}-p${params.page}`,
      totalUsers: 120,
      query: { ...echo, page: params.page },
    }));

    renderWithProvider(<LicenceActivityPage />);
    await screen.findAllByText('ada@contoso.com');
    await act(async () => {});

    // Browse to page 2 of the current (all-departments) scope.
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => expect(usersParams()).toMatchObject({ overviewId: 'ov-dept-all', page: 2 }));

    // Change the department filter: a genuine scope change, NOT a re-mint of the same query.
    fireEvent.change(screen.getByLabelText('Filter by department'), { target: { value: '1' } });
    await waitFor(() => expect(mockOverview.mock.calls.some((c) => c[0].departmentId === 1)).toBe(true));

    // The re-scoped list is fetched fresh at PAGE 1 against the NEW overview - never the old page/id.
    await waitFor(() => expect(usersParams()).toMatchObject({ overviewId: 'ov-dept-1', page: 1 }));

    // Let the fresh page-1 list settle (its snapshot id is captured for the export).
    await screen.findAllByText('ada@contoso.com');
    await act(async () => {});

    // And an export now references the new scope's ids, never the pre-change ones.
    fireEvent.click(screen.getByRole('button', { name: /Export to Excel/i }));
    await waitFor(() => {
      const last = mockDownload.mock.calls[mockDownload.mock.calls.length - 1][0];
      expect(last.overviewId).toBe('ov-dept-1');
      expect(last.usersId).toBe('us-ov-dept-1-p1');
    });
  });
});

describe('LicenceActivityPage - demographic filter options', () => {
  it('keeps every department selectable after a scoped reply, so you can switch straight from one to another', async () => {
    mockAvailability.mockResolvedValue(availability({ canViewUsers: false }));
    const sales = { id: 1, name: 'Sales', assignedUsers: 60, workloads: [] };
    const support = { id: 2, name: 'Support', assignedUsers: 40, workloads: [] };
    // The backend only returns demographic groups WITHIN the current scope: filtering by a department
    // returns just that one. The UI must not let that collapse the selectable catalogue.
    mockOverview.mockImplementation(async (q) => {
      const departments =
        q.departmentId == null ? [sales, support] : [sales, support].filter((d) => d.id === q.departmentId);
      return overview({ departments });
    });

    renderWithProvider(<LicenceActivityPage />);
    await screen.findByText('Licence assignments');

    const deptSelect = await screen.findByLabelText('Filter by department');
    expect(screen.getByRole('option', { name: 'Sales' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Support' })).toBeInTheDocument();

    // Select Sales -> overview refetches scoped to department 1 (its reply contains only Sales)...
    fireEvent.change(deptSelect, { target: { value: '1' } });
    await waitFor(() => expect(mockOverview.mock.calls.some((c) => c[0].departmentId === 1)).toBe(true));

    // ...yet Support is STILL an option, so we can switch straight to it without going via "All".
    await waitFor(() => expect(screen.getByRole('option', { name: 'Support' })).toBeInTheDocument());
    fireEvent.change(deptSelect, { target: { value: '2' } });
    await waitFor(() => expect(mockOverview.mock.calls.some((c) => c[0].departmentId === 2)).toBe(true));
  });
});
