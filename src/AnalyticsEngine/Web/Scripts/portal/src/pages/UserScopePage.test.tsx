import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import UserScopePage from './UserScopePage';
import type { UserScopePurgeJob, UserScopeStatus } from '../types/userScope';

const mockFetchStatus = vi.fn();
const mockRefreshStatus = vi.fn();
const mockStartPurge = vi.fn();
const mockFetchPurge = vi.fn();
const mockCancelPurge = vi.fn();

vi.mock('../api/userScopeApi', () => ({
  fetchUserScope: (...args: unknown[]) => mockFetchStatus(...args),
  refreshUserScope: (...args: unknown[]) => mockRefreshStatus(...args),
  startUserScopePurge: (...args: unknown[]) => mockStartPurge(...args),
  fetchUserScopePurge: (...args: unknown[]) => mockFetchPurge(...args),
  cancelUserScopePurge: (...args: unknown[]) => mockCancelPurge(...args),
}));

const runningJob = (over: Partial<UserScopePurgeJob> = {}): UserScopePurgeJob => ({
  id: 42,
  state: 'running',
  phase: 'usageReports',
  stepIndex: 8,
  stepCount: 14,
  candidateCount: 120,
  usersDeleted: 50,
  usersSkipped: 0,
  rowsAffected: [{ table: 'audit_events', rows: 1234 }],
  cancelRequested: false,
  requestedBy: 'admin@contoso.com',
  createdUtc: '2026-09-01T09:00:00Z',
  startedUtc: '2026-09-01T09:01:00Z',
  updatedUtc: '2026-09-01T09:03:00Z',
  completedUtc: null,
  errorCode: null,
  ...over,
});

const status = (over: Partial<UserScopeStatus> = {}): UserScopeStatus => ({
  filtered: true,
  filterPatterns: ['Finance*', 'Athens'],
  resolution: {
    status: 'resolved',
    failureKind: null,
    httpStatus: null,
    resolvedUtc: '2026-09-01T08:00:00Z',
    memberCount: 75,
    matchedNoGroup: false,
    groups: [
      {
        id: '00000000-0000-0000-0000-000000000000',
        displayName: 'Finance Team',
        userMemberCount: 50,
        matchedPatterns: ['Finance*'],
      },
      {
        id: '11111111-1111-1111-1111-111111111111',
        displayName: 'Ομάδα Πωλήσεων Αθήνα',
        userMemberCount: 25,
        matchedPatterns: ['Athens'],
      },
    ],
    unmatchedPatterns: [],
  },
  database: { totalUsers: 100, inScopeUsers: 75, outOfScopeUsers: 25 },
  purgeUnavailableReason: null,
  latestJob: null,
  purgeStateDurable: false,
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  vi.useRealTimers();
  mockFetchStatus.mockResolvedValue(status());
  mockRefreshStatus.mockResolvedValue(status());
  mockStartPurge.mockResolvedValue(runningJob());
  mockFetchPurge.mockResolvedValue(runningJob());
  mockCancelPurge.mockResolvedValue(runningJob({ cancelRequested: true }));
});

afterEach(() => {
  vi.useRealTimers();
});

describe('UserScopePage', () => {
  it('explains the unfiltered state and does not offer a usable purge', async () => {
    mockFetchStatus.mockResolvedValue(
      status({
        filtered: false,
        filterPatterns: [],
        resolution: {
          status: 'unfiltered',
          failureKind: null,
          httpStatus: null,
          resolvedUtc: null,
          memberCount: 0,
          matchedNoGroup: false,
          groups: [],
          unmatchedPatterns: [],
        },
        database: null,
        purgeUnavailableReason: 'notFiltered',
      }),
    );

    renderWithProvider(<UserScopePage />);

    expect(await screen.findByText(/No UserGroupsFilter is set/)).toBeInTheDocument();
    expect(screen.getByText('No filter is configured, so everyone is in scope and there is nothing to purge.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Purge 0 people...' })).toBeDisabled();
  });

  it('renders resolved groups, counts and configured patterns as data', async () => {
    renderWithProvider(<UserScopePage />);

    expect(await screen.findByText('Finance Team')).toBeInTheDocument();
    expect(screen.getByText('Ομάδα Πωλήσεων Αθήνα')).toBeInTheDocument();
    expect(screen.getByText('00000000-0000-0000-0000-000000000000')).toBeInTheDocument();
    expect(screen.getAllByText('75').length).toBeGreaterThan(0);
    expect(screen.getAllByText('25').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Finance*').length).toBeGreaterThan(0);
  });

  it('explains a Graph 403 as a missing Group.Read.All permission', async () => {
    mockFetchStatus.mockResolvedValue(
      status({
        resolution: {
          status: 'unavailable',
          failureKind: 'directoryRead',
          httpStatus: 403,
          resolvedUtc: null,
          memberCount: 0,
          matchedNoGroup: false,
          groups: [],
          unmatchedPatterns: [],
        },
        database: null,
        purgeUnavailableReason: 'scopeUnavailable',
      }),
    );

    renderWithProvider(<UserScopePage />);

    expect(await screen.findByText(/missing Group\.Read\.All/)).toBeInTheDocument();
    expect(screen.getByText(/Imports carry on with the last good group list/)).toBeInTheDocument();
  });

  it('disables the purge button when the server says the purge is unavailable', async () => {
    mockFetchStatus.mockResolvedValue(status({ purgeUnavailableReason: 'nothingToPurge' }));

    renderWithProvider(<UserScopePage />);

    expect(await screen.findByText('There are no database users outside the scope.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Purge 25 people...' })).toBeDisabled();
  });

  it('still shows the scope, and says why purging waits, when Azure Table storage is out of reach', async () => {
    mockFetchStatus.mockResolvedValue(status({ purgeUnavailableReason: 'storageUnavailable' }));

    renderWithProvider(<UserScopePage />);

    expect(await screen.findByText(/Purging is unavailable because Azure Table storage/)).toBeInTheDocument();
    expect(screen.getByText('Ομάδα Πωλήσεων Αθήνα')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Purge 25 people...' })).toBeDisabled();
  });

  it('requires acknowledgement before starting a purge, then shows progress', async () => {
    renderWithProvider(<UserScopePage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Purge 25 people...' }));
    const title = await screen.findByText('Purge people outside the scope?');
    const dialog = title.closest('[role="dialog"]') ?? document.body;
    const start = within(dialog as HTMLElement).getByRole('button', { name: 'Start purge' });
    expect(start).toBeDisabled();

    fireEvent.click(within(dialog as HTMLElement).getByRole('checkbox'));
    expect(start).toBeEnabled();
    fireEvent.click(start);

    await waitFor(() => expect(mockStartPurge).toHaveBeenCalledTimes(1));
    expect(await screen.findByText('The purge is running.')).toBeInTheDocument();
    expect(screen.getByText('Usage reports: 8 of 14 steps complete')).toBeInTheDocument();
  });

  it('polls a running purge until completion and reloads the full status', async () => {
    vi.useFakeTimers();
    mockFetchStatus
      .mockResolvedValueOnce(status({ latestJob: runningJob(), purgeUnavailableReason: 'jobActive' }))
      .mockResolvedValueOnce(status({ latestJob: runningJob({ state: 'completed', phase: 'done', stepIndex: 14, completedUtc: '2026-09-01T09:10:00Z' }) }));
    mockFetchPurge.mockResolvedValue(
      runningJob({ state: 'completed', phase: 'done', stepIndex: 14, completedUtc: '2026-09-01T09:10:00Z' }),
    );

    renderWithProvider(<UserScopePage />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(screen.getByText('The purge is running.')).toBeInTheDocument();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3000);
    });

    expect(mockFetchPurge).toHaveBeenCalledWith(42);
    expect(mockFetchStatus).toHaveBeenCalledTimes(2);
    expect(screen.getByText('The purge completed.')).toBeInTheDocument();
  });

  it('stops a running purge without confirmation', async () => {
    mockFetchStatus.mockResolvedValue(status({ latestJob: runningJob(), purgeUnavailableReason: 'jobActive' }));

    renderWithProvider(<UserScopePage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Stop purge' }));

    await waitFor(() => expect(mockCancelPurge).toHaveBeenCalledWith(42));
    expect(await screen.findByText('The purge is stopping after the current batch.')).toBeInTheDocument();
  });

  it('warns when users were skipped because new data arrived', async () => {
    mockFetchStatus.mockResolvedValue(status({ latestJob: runningJob({ usersSkipped: 3 }), purgeUnavailableReason: 'jobActive' }));

    renderWithProvider(<UserScopePage />);

    expect(await screen.findByText(/new data about them arrived during the purge/)).toBeInTheDocument();
  });

  it('lists deleted tables and anonymised columns as they are, and says which is which', async () => {
    mockFetchStatus.mockResolvedValue(
      status({
        latestJob: runningJob({
          state: 'completed',
          phase: 'done',
          stepIndex: 14,
          completedUtc: '2026-09-01T09:05:00Z',
          rowsAffected: [
            { table: 'audit_events', rows: 1234 },
            { table: 'users.manager_id', rows: 2 },
          ],
        }),
        purgeUnavailableReason: null,
      }),
    );

    renderWithProvider(<UserScopePage />);

    const rows = await screen.findByRole('table', { name: 'Rows affected by table or column' });
    expect(within(rows).getByText('audit_events')).toBeInTheDocument();
    expect(within(rows).getByText('1,234')).toBeInTheDocument();
    expect(within(rows).getByText('users.manager_id')).toBeInTheDocument();
    expect(screen.getByText(/A table\.column name counts rows kept but changed in place/)).toBeInTheDocument();
  });

  it('shows a job error code sentence', async () => {
    mockFetchStatus.mockResolvedValue(
      status({
        latestJob: runningJob({ state: 'failed', errorCode: 'databaseError', completedUtc: '2026-09-01T09:05:00Z' }),
        purgeUnavailableReason: null,
      }),
    );

    renderWithProvider(<UserScopePage />);

    expect(await screen.findByText('The purge stopped because the analytics database returned an error.')).toBeInTheDocument();
  });

  it('says a purge that could not save its progress can be started again', async () => {
    mockFetchStatus.mockResolvedValue(
      status({
        latestJob: runningJob({ state: 'failed', errorCode: 'stateUnavailable', completedUtc: '2026-09-01T09:05:00Z' }),
        purgeUnavailableReason: null,
      }),
    );

    renderWithProvider(<UserScopePage />);

    expect(await screen.findByText(/couldn't save its progress to Azure Table storage/)).toBeInTheDocument();
  });

  it('explains when the purge removed nobody because the filter changed', async () => {
    mockFetchStatus.mockResolvedValue(
      status({
        latestJob: runningJob({ state: 'failed', errorCode: 'filterChanged', completedUtc: '2026-09-01T09:05:00Z' }),
        purgeUnavailableReason: null,
      }),
    );

    renderWithProvider(<UserScopePage />);

    expect(
      await screen.findByText(
        'The purge removed nobody because UserGroupsFilter changed after the purge was confirmed. Check the filter and start the purge again.',
      ),
    ).toBeInTheDocument();
  });

  it('explains that a purge stopped part-way because the filter changed while it ran', async () => {
    mockFetchStatus.mockResolvedValue(
      status({
        latestJob: runningJob({
          state: 'failed',
          errorCode: 'filterChangedWhileRunning',
          completedUtc: '2026-09-01T09:05:00Z',
        }),
        purgeUnavailableReason: null,
      }),
    );

    renderWithProvider(<UserScopePage />);

    expect(
      await screen.findByText(/The purge stopped because UserGroupsFilter changed while it was running\. What it had already removed stays removed/),
    ).toBeInTheDocument();
  });

  it('says a restart stops a purge when its progress is kept in memory, and that it starts again when it is not', async () => {
    const { unmount } = renderWithProvider(<UserScopePage />);

    expect(await screen.findByText(/keeps the purge's progress in its own memory/)).toBeInTheDocument();
    expect(screen.queryByText(/starts again by itself/)).not.toBeInTheDocument();
    unmount();

    mockFetchStatus.mockResolvedValue(status({ purgeStateDurable: true }));
    renderWithProvider(<UserScopePage />);

    expect(await screen.findByText(/the purge starts again by itself and finishes/)).toBeInTheDocument();
    expect(screen.queryByText(/keeps the purge's progress in its own memory/)).not.toBeInTheDocument();
  });

  it('keeps polling after a non-blocking poll error', async () => {
    vi.useFakeTimers();
    mockFetchStatus.mockResolvedValue(status({ latestJob: runningJob(), purgeUnavailableReason: 'jobActive' }));
    mockFetchPurge.mockRejectedValueOnce(new Error("Couldn't load the purge progress (500).")).mockResolvedValueOnce(runningJob());

    renderWithProvider(<UserScopePage />);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(screen.getByText('The purge is running.')).toBeInTheDocument();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3000);
    });
    expect(screen.getByText("Couldn't load the purge progress (500).")).toBeInTheDocument();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(3000);
    });
    expect(mockFetchPurge).toHaveBeenCalledTimes(2);
  });
});
