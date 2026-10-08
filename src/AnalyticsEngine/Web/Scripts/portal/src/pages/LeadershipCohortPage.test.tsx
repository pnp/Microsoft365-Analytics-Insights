import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import LeadershipCohortPage from './LeadershipCohortPage';
import type { LeadershipCohortStatus } from '../types/leadershipCohort';

const mockFetch = vi.fn();
const mockSave = vi.fn();
const mockRefresh = vi.fn();

vi.mock('../api/leadershipCohortApi', () => ({
  fetchLeadershipCohort: (...args: unknown[]) => mockFetch(...args),
  saveLeadershipCohort: (...args: unknown[]) => mockSave(...args),
  refreshLeadershipCohort: (...args: unknown[]) => mockRefresh(...args),
}));

const GROUP = '00000000-0000-0000-0000-000000000654';

const status = (over: Partial<LeadershipCohortStatus> = {}): LeadershipCohortStatus => ({
  stateDurable: true,
  configured: false,
  groupId: null,
  updatedUtc: null,
  minimumCohort: 10,
  maxMembers: 10000,
  refreshAfterSuccessHours: 6,
  staleAfterHours: 72,
  refresh: null,
  ...over,
});

const ready = (over: Partial<LeadershipCohortStatus> = {}) => status({
  configured: true,
  groupId: GROUP,
  updatedUtc: '2026-10-01T08:00:00Z',
  refresh: {
    status: 'ready',
    failureKind: null,
    httpStatus: null,
    groupDisplayName: 'Contoso Leadership Team',
    attemptedUtc: '2026-10-01T08:00:05Z',
    refreshedUtc: '2026-10-01T08:00:05Z',
    directMembers: 42,
    matchedUsers: 40,
    stale: false,
  },
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  mockFetch.mockResolvedValue(status());
});

describe('LeadershipCohortPage', () => {
  it('is off by default and explains the fixed minimum', async () => {
    renderWithProvider(<LeadershipCohortPage />);
    expect(await screen.findByText(/No leadership group is configured/)).toBeInTheDocument();
    expect(screen.getByText(/fewer than 10 of its members hold a Copilot licence/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Read members now/ })).toBeDisabled();
    expect(screen.getByRole('button', { name: /Turn off/ })).toBeDisabled();
  });

  it('rejects a value that is not a group object ID before calling the server', async () => {
    renderWithProvider(<LeadershipCohortPage />);
    const input = await screen.findByRole('textbox');
    fireEvent.change(input, { target: { value: 'Leadership Team' } });
    expect(screen.getAllByText(/Enter a group object ID/).length).toBeGreaterThan(0);
    expect(screen.getByRole('button', { name: /Save and read members/ })).toBeDisabled();
    expect(mockSave).not.toHaveBeenCalled();
  });

  it('saves the group and shows the refresh outcome with the group name as stored', async () => {
    mockSave.mockResolvedValue(ready());
    renderWithProvider(<LeadershipCohortPage />);
    fireEvent.change(await screen.findByRole('textbox'), { target: { value: ` ${GROUP} ` } });
    fireEvent.click(screen.getByRole('button', { name: /Save and read members/ }));

    await waitFor(() => expect(mockSave).toHaveBeenCalledWith(GROUP));
    expect(await screen.findByText('Contoso Leadership Team')).toBeInTheDocument();
    expect(screen.getByText('Succeeded')).toBeInTheDocument();
    expect(screen.getByText('42 direct members, 40 matched to users in the database')).toBeInTheDocument();
  });

  it('turns the comparison off by saving an empty group', async () => {
    mockFetch.mockResolvedValue(ready());
    mockSave.mockResolvedValue(status());
    renderWithProvider(<LeadershipCohortPage />);
    fireEvent.click(await screen.findByRole('button', { name: /Turn off/ }));
    await waitFor(() => expect(mockSave).toHaveBeenCalledWith(''));
    expect(await screen.findByText(/No leadership group is configured/)).toBeInTheDocument();
  });

  it('names a missing Graph permission rather than reporting success', async () => {
    mockFetch.mockResolvedValue(ready({
      refresh: { ...ready().refresh!, status: 'permissionMissing', failureKind: 'graphError', httpStatus: 403, refreshedUtc: null, directMembers: 0, matchedUsers: 0 },
    }));
    renderWithProvider(<LeadershipCohortPage />);
    expect(await screen.findByText('Permission missing: grant Group.Read.All or GroupMember.Read.All')).toBeInTheDocument();
    expect(screen.getByText(/Microsoft Graph returned an error/)).toBeInTheDocument();
    expect(screen.getByText('Never')).toBeInTheDocument();
    expect(screen.queryByText(/direct members,/)).not.toBeInTheDocument();
  });

  it('translates a server error code', async () => {
    mockFetch.mockResolvedValue(ready());
    mockRefresh.mockRejectedValue(new Error('refreshInProgress'));
    renderWithProvider(<LeadershipCohortPage />);
    fireEvent.click(await screen.findByRole('button', { name: /Read members now/ }));
    expect(await screen.findByText('The members are already being read. Try again in a minute.')).toBeInTheDocument();
  });

  it('cannot save without durable state', async () => {
    mockFetch.mockResolvedValue(status({ stateDurable: false }));
    renderWithProvider(<LeadershipCohortPage />);
    expect(await screen.findByText(/Azure Storage is not configured/)).toBeInTheDocument();
    fireEvent.change(screen.getByRole('textbox'), { target: { value: GROUP } });
    expect(screen.getByRole('button', { name: /Save and read members/ })).toBeDisabled();
  });
});
