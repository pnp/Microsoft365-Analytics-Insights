import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import CopilotAuditBackfillPage from './CopilotAuditBackfillPage';
import type { CopilotAuditBackfillJob, CopilotAuditBackfillStatus } from '../types/copilotAuditBackfill';
import { PortalPermissionError, SessionExpiredError } from '../api/http';

const mockFetch = vi.fn();
const mockStart = vi.fn();
const mockCancel = vi.fn();

vi.mock('../api/copilotAuditBackfillApi', () => ({
  fetchCopilotAuditBackfill: (...args: unknown[]) => mockFetch(...args),
  startCopilotAuditBackfill: (...args: unknown[]) => mockStart(...args),
  cancelCopilotAuditBackfill: (...args: unknown[]) => mockCancel(...args),
}));

const job = (over: Partial<CopilotAuditBackfillJob> = {}): CopilotAuditBackfillJob => ({
  id: 42,
  state: 'running',
  requestedBy: 'admin@contoso.com',
  createdUtc: '2026-10-01T00:00:00Z',
  updatedUtc: '2026-10-01T00:30:00Z',
  startedUtc: '2026-10-01T00:05:00Z',
  completedUtc: null,
  startUtc: '2026-04-05T00:00:00Z',
  endUtc: '2026-10-02T00:00:00Z',
  pendingSlices: 12,
  inFlightSlices: 4,
  slicesSubmitted: 8,
  slicesCompleted: 3,
  slicesSplit: 1,
  completedDays: ['2026-10-01'],
  failedDays: [],
  incompleteDays: [],
  recordsSeen: 1200,
  recordsImported: 1190,
  recordsAlreadyPresent: 10,
  permissionStatus: 'granted',
  copilotImportEnabled: true,
  lastErrorCode: null,
  cancelRequested: false,
  currentSliceStartUtc: '2026-10-01T00:00:00Z',
  currentSliceEndUtc: '2026-10-02T00:00:00Z',
  ...over,
});

const status = (over: Partial<CopilotAuditBackfillStatus> = {}): CopilotAuditBackfillStatus => ({
  stateDurable: true,
  copilotImportEnabled: true,
  latestJob: null,
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  vi.useRealTimers();
  mockFetch.mockResolvedValue(status());
  mockStart.mockResolvedValue(job());
  mockCancel.mockResolvedValue(job({ state: 'cancelled', cancelRequested: true }));
});

describe('CopilotAuditBackfillPage', () => {
  it('shows the not-started state', async () => {
    renderWithProvider(<CopilotAuditBackfillPage />);

    expect(await screen.findByText('Copilot audit backfill')).toBeVisible();
    expect(screen.getByText('Preview')).toBeVisible();
    expect(screen.getByText(/uses a newer Microsoft Graph API/)).toBeVisible();
    expect(screen.getByText('No Copilot audit backfill has been requested.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Start backfill' })).toBeEnabled();
  });

  it('shows running progress and lets the admin cancel', async () => {
    mockFetch.mockResolvedValue(status({ latestJob: job() }));
    renderWithProvider(<CopilotAuditBackfillPage />);

    expect(await screen.findByText('Running')).toBeVisible();
    expect(screen.getByText('3 / 19')).toBeVisible();
    expect(screen.getByText('1,190 / 1,200')).toBeVisible();
    expect(screen.getByText('10')).toBeVisible();
    expect(screen.getByText('Completed days (most recent 12)')).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(mockCancel).toHaveBeenCalledWith(42));
  });

  it('shows missing permission', async () => {
    mockFetch.mockResolvedValue(status({ latestJob: job({ state: 'failed', permissionStatus: 'missing', lastErrorCode: 'missingPermission' }) }));
    renderWithProvider(<CopilotAuditBackfillPage />);

    expect(await screen.findByText('AuditLogsQuery.Read.All missing')).toBeVisible();
    expect(screen.getByText('The runtime app is missing AuditLogsQuery.Read.All.')).toBeVisible();
  });

  it('shows Copilot toggle off', async () => {
    mockFetch.mockResolvedValue(status({ copilotImportEnabled: false }));
    renderWithProvider(<CopilotAuditBackfillPage />);

    expect(await screen.findByText('The Copilot audit import toggle is off. Turn on Copilot import before starting a backfill.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Start backfill' })).toBeDisabled();
  });

  it('shows completed with gaps and affected days', async () => {
    mockFetch.mockResolvedValue(status({ latestJob: job({ state: 'completedWithGaps', failedDays: ['2026-09-30'], incompleteDays: ['2026-09-29'], lastErrorCode: 'queryTruncated' }) }));
    renderWithProvider(<CopilotAuditBackfillPage />);

    expect(await screen.findByText('Completed with gaps')).toBeVisible();
    expect(screen.getByText('A Microsoft Graph Audit Search slice was still truncated after splitting to quarter-hours. The records it returned were imported; the rest of that window is missing.')).toBeVisible();
    expect(screen.getByText('2026-09-30')).toBeVisible();
    expect(screen.getByText('2026-09-29')).toBeVisible();
  });

  it('disables start when state is not durable', async () => {
    mockFetch.mockResolvedValue(status({ stateDurable: false }));
    renderWithProvider(<CopilotAuditBackfillPage />);

    expect(await screen.findByText('Backfill state is not durable because Azure Storage is not configured. Configure the Storage connection string before starting a backfill.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Start backfill' })).toBeDisabled();
  });

  it('translates a stateNotDurable start refusal from the API', async () => {
    mockStart.mockRejectedValue(new Error('stateNotDurable'));
    renderWithProvider(<CopilotAuditBackfillPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Start backfill' }));

    expect(await screen.findByText('Backfill state is not durable because Azure Storage is not configured. Configure the Storage connection string before starting a backfill.')).toBeVisible();
  });

  it('keeps translated session expiry errors from load', async () => {
    mockFetch.mockRejectedValue(new SessionExpiredError());
    renderWithProvider(<CopilotAuditBackfillPage />);

    expect(await screen.findByText('Your session has expired. Reload the page to sign in again.')).toBeVisible();
  });

  it('keeps translated permission errors from cancel', async () => {
    mockFetch.mockResolvedValue(status({ latestJob: job() }));
    mockCancel.mockRejectedValue(new PortalPermissionError('administration', 'Portal.Administration'));
    renderWithProvider(<CopilotAuditBackfillPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    expect(await screen.findByText('This needs the Administration permission. Ask an Entra ID administrator to assign you the Portal.Administration app role.')).toBeVisible();
  });

  it('keeps translated session expiry errors from start', async () => {
    mockStart.mockRejectedValue(new SessionExpiredError());
    renderWithProvider(<CopilotAuditBackfillPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Start backfill' }));

    expect(await screen.findByText('Your session has expired. Reload the page to sign in again.')).toBeVisible();
  });

  it('shows None, not Unknown, for empty day lists', async () => {
    mockFetch.mockResolvedValue(status({ latestJob: job({ state: 'completed', completedDays: [], failedDays: [], incompleteDays: [] }) }));
    renderWithProvider(<CopilotAuditBackfillPage />);

    expect(await screen.findByText('Completed')).toBeVisible();
    expect(screen.getAllByText('None')).toHaveLength(3);
    expect(screen.queryByText('Unknown')).not.toBeInTheDocument();
  });

  it('waits for a slow poll instead of overlapping it, and backs off after failures', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      mockFetch.mockResolvedValueOnce(status({ latestJob: job() }));
      renderWithProvider(<CopilotAuditBackfillPage />);
      expect(await screen.findByText('Running')).toBeVisible();
      expect(mockFetch).toHaveBeenCalledTimes(1);

      let answer: (value: CopilotAuditBackfillStatus) => void = () => undefined;
      mockFetch.mockImplementationOnce(() => new Promise<CopilotAuditBackfillStatus>((resolve) => { answer = resolve; }));
      await act(() => vi.advanceTimersByTimeAsync(5_000));
      expect(mockFetch).toHaveBeenCalledTimes(2);
      await act(() => vi.advanceTimersByTimeAsync(30_000));
      expect(mockFetch).toHaveBeenCalledTimes(2);

      mockFetch.mockRejectedValue(new Error('stateUnavailable'));
      await act(async () => { answer(status({ latestJob: job() })); });
      await act(() => vi.advanceTimersByTimeAsync(5_500));
      expect(mockFetch).toHaveBeenCalledTimes(3);
      expect(await screen.findByText(/Backfill state could not be read or saved/)).toBeVisible();

      await act(() => vi.advanceTimersByTimeAsync(8_500));
      expect(mockFetch).toHaveBeenCalledTimes(3);
      await act(() => vi.advanceTimersByTimeAsync(2_000));
      expect(mockFetch).toHaveBeenCalledTimes(4);
      await act(() => vi.advanceTimersByTimeAsync(18_000));
      expect(mockFetch).toHaveBeenCalledTimes(4);
      await act(() => vi.advanceTimersByTimeAsync(2_500));
      expect(mockFetch).toHaveBeenCalledTimes(5);
    } finally {
      vi.useRealTimers();
    }
  });

  it('never lets an older status replace a newer one', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      mockFetch.mockResolvedValueOnce(status({ latestJob: job() }));
      renderWithProvider(<CopilotAuditBackfillPage />);
      expect(await screen.findByText('Running')).toBeVisible();

      let answer: (value: CopilotAuditBackfillStatus) => void = () => undefined;
      mockFetch.mockImplementationOnce(() => new Promise<CopilotAuditBackfillStatus>((resolve) => { answer = resolve; }));
      await act(() => vi.advanceTimersByTimeAsync(5_000));
      expect(mockFetch).toHaveBeenCalledTimes(2);

      mockFetch.mockResolvedValueOnce(status({ latestJob: job({ state: 'completed' }) }));
      fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));
      expect(await screen.findByText('Completed')).toBeVisible();

      await act(async () => { answer(status({ latestJob: job() })); });

      expect(screen.getByText('Completed')).toBeVisible();
      expect(screen.queryByText('Running')).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});
