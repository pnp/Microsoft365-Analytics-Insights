import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import CallDeadLettersCard, { POLL_DELAYS_MS } from './CallDeadLettersCard';
import type { CallDeadLetterRequestView, CallDeadLetterStatus } from '../../types/callDeadLetters';

const mockFetch = vi.fn();
const mockRequest = vi.fn();

vi.mock('../../api/callDeadLettersApi', () => ({
  fetchCallDeadLetters: (...args: unknown[]) => mockFetch(...args),
  requestCallDeadLetterAction: (...args: unknown[]) => mockRequest(...args),
}));

const ID = '00000000000000000000000000000001';

const status = (over: Partial<CallDeadLetterStatus> = {}): CallDeadLetterStatus => ({
  available: true,
  unavailableReason: null,
  deadLetterCount: 12345,
  activeCount: 0,
  countUnavailable: false,
  latest: null,
  latestUnavailable: false,
  canRequest: true,
  pickUpWithinMinutes: 15,
  finishWithinMinutes: 120,
  runBudgetMinutes: 15,
  ...over,
});

const waiting = (over: Partial<CallDeadLetterRequestView> = {}): CallDeadLetterRequestView => ({
  id: ID,
  action: 'reprocess',
  state: 'waiting',
  requestedUtc: '2026-11-10T09:00:00Z',
  claimedUtc: null,
  finishedUtc: null,
  outcome: null,
  failureCode: null,
  result: null,
  ...over,
});

const finished = (over: Partial<CallDeadLetterRequestView> = {}): CallDeadLetterRequestView =>
  waiting({
    state: 'finished',
    claimedUtc: '2026-11-10T09:01:00Z',
    finishedUtc: '2026-11-10T09:05:00Z',
    outcome: 'succeeded',
    result: {
      passes: 13,
      scanned: 12500,
      replayed: 12345,
      replayedButNotRemoved: 0,
      deleted: 0,
      sendFailed: 0,
      deleteFailed: 0,
      skipped: { neverImportable: 12, callRecordNoLongerInGraph: 3 },
      reasons: [
        { reason: 'CallImportRetriesExhausted', replayed: 12000, deleted: 0, left: 0 },
        { reason: 'MaxDeliveryCountExceeded', replayed: 345, deleted: 0, left: 3 },
        { reason: 'InvalidCallNotification', replayed: 0, deleted: 0, left: 12 },
        { reason: 'NoUsableSessions', replayed: 0, deleted: 0, left: 0 },
        { reason: 'ContosoFutureReason', replayed: 0, deleted: 0, left: 0 },
        { reason: null, replayed: 0, deleted: 0, left: 0 },
      ],
      stopCode: null,
    },
    ...over,
  });

function setVisibility(state: 'visible' | 'hidden') {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
}

async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  mockFetch.mockResolvedValue(status());
  mockRequest.mockResolvedValue(status({ latest: waiting(), canRequest: false }));
});

afterEach(() => {
  vi.useRealTimers();
  delete (document as { visibilityState?: unknown }).visibilityState;
});

describe('CallDeadLettersCard', () => {
  it('shows the dead-letter count and asks the importer to reprocess, then says it is waiting for it', async () => {
    renderWithProvider(<CallDeadLettersCard />);

    expect(await screen.findByText('12,345')).toBeInTheDocument();
    expect(screen.getByText('None yet.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Reprocess' }));

    await waitFor(() => expect(mockRequest).toHaveBeenCalledWith('reprocess'));
    expect(await screen.findByText('Waiting for the importer')).toBeInTheDocument();
    expect(screen.getByText(/^Asked for at 10 Nov 2026, 09:00 UTC\. The importer checks for new requests every minute\.$/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reprocess' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Clear...' })).toBeDisabled();
  });

  it('asks before clearing, says the calls will never be imported, and clears only when confirmed', async () => {
    mockRequest.mockResolvedValue(status({ latest: waiting({ action: 'clear' }), canRequest: false }));
    renderWithProvider(<CallDeadLettersCard />);

    fireEvent.click(await screen.findByRole('button', { name: 'Clear...' }));
    expect(await screen.findByText('Clear the dead-letter queue?')).toBeInTheDocument();
    expect(screen.getByText(/Those calls will never be imported, and this cannot be undone\./)).toBeInTheDocument();
    expect(screen.getByText('At the last count there were 12,345.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByText('Clear the dead-letter queue?')).not.toBeInTheDocument());
    expect(mockRequest).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Clear...' }));
    await act(async () => {
      fireEvent.click(await screen.findByRole('button', { name: 'Delete them permanently' }));
    });

    expect(mockRequest).toHaveBeenCalledWith('clear');
    await waitFor(() => expect(screen.queryByText('Clear the dead-letter queue?')).not.toBeInTheDocument());
    expect(screen.getByText('Clear')).toBeInTheDocument();
  });

  it('shows what a finished request did, by dead-letter reason, naming the reasons it knows and showing others as they are', async () => {
    mockFetch.mockResolvedValue(status({ deadLetterCount: 15, latest: finished() }));
    renderWithProvider(<CallDeadLettersCard />);

    expect(await screen.findByText('Finished')).toBeInTheDocument();
    expect(screen.getByText('Finished at 10 Nov 2026, 09:05 UTC.')).toBeInTheDocument();
    expect(screen.getByText('Sent back to the queue: 12,345')).toBeInTheDocument();
    expect(screen.getByText('Left in the dead-letter queue: 15')).toBeInTheDocument();
    expect(screen.getByText('Can never be imported as they stand: 12')).toBeInTheDocument();
    expect(screen.getByText('First queued over 30 days ago, so Graph no longer has them: 3')).toBeInTheDocument();
    expect(screen.getByText('Retries ran out')).toBeInTheDocument();
    expect(screen.getByText('Service Bus gave up after too many deliveries')).toBeInTheDocument();
    expect(screen.getByText('Not a Graph call notification')).toBeInTheDocument();
    expect(screen.getByText("None of the call record's sessions could be used")).toBeInTheDocument();
    expect(screen.getByText('No reason recorded')).toBeInTheDocument();
    // The codes are data: shown as Service Bus holds them, so an operator can search the importer's log for them.
    expect(screen.getByText('CallImportRetriesExhausted')).toBeInTheDocument();
    expect(screen.getByText('ContosoFutureReason')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reprocess' })).toBeEnabled();
  });

  it('says why a request did not finish cleanly, and that a stale one can be asked for again', async () => {
    mockFetch.mockResolvedValue(
      status({
        latest: finished({ outcome: 'partial', failureCode: 'timeBudget' }),
      }),
    );
    const { unmount } = renderWithProvider(<CallDeadLettersCard />);
    expect(await screen.findByText('Partly done')).toBeInTheDocument();
    expect(
      screen.getByText('It stopped after 15 minutes, the longest one request may take, with more left to do. Ask again to carry on.'),
    ).toBeInTheDocument();
    unmount();

    mockFetch.mockResolvedValue(status({ latest: finished({ action: 'clear', outcome: 'failed', failureCode: 'queueUnavailable', result: null }) }));
    const second = renderWithProvider(<CallDeadLettersCard />);
    expect(await screen.findByText('Failed')).toBeInTheDocument();
    expect(screen.getByText(/The importer couldn't open the Teams calls queue, so nothing was done\./)).toBeInTheDocument();
    second.unmount();

    mockFetch.mockResolvedValue(status({ latest: finished({ outcome: 'partial', failureCode: 'contosoFutureCode' }) }));
    const third = renderWithProvider(<CallDeadLettersCard />);
    expect(await screen.findByText("It didn't finish cleanly (contosoFutureCode). The importer's log has the detail.")).toBeInTheDocument();
    third.unmount();

    mockFetch.mockResolvedValue(status({ latest: finished({ action: 'clear', outcome: 'partial', failureCode: 'scanLimit' }) }));
    const fourth = renderWithProvider(<CallDeadLettersCard />);
    expect(
      await screen.findByText('It deleted as many dead-lettered calls as one request may, and there are more. Ask again to carry on.'),
    ).toBeInTheDocument();
    fourth.unmount();

    mockFetch.mockResolvedValue(status({ latest: waiting({ state: 'notPickedUp' }) }));
    renderWithProvider(<CallDeadLettersCard />);
    expect(await screen.findByText('Not picked up')).toBeInTheDocument();
    expect(screen.getByText(/but no importer picked it up within 15 minutes, so it won't run\./)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reprocess' })).toBeEnabled();
  });

  it('disables both actions and explains why when a request cannot reach the importer', async () => {
    mockFetch.mockResolvedValue(status({ available: false, unavailableReason: 'stateStoreInMemory', canRequest: false }));
    const { unmount } = renderWithProvider(<CallDeadLettersCard />);

    expect(await screen.findByText(/This deployment has no Azure Storage connection string/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reprocess' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Clear...' })).toBeDisabled();
    expect(screen.queryByText('Latest request')).not.toBeInTheDocument();
    unmount();

    mockFetch.mockResolvedValue(
      status({ available: false, unavailableReason: 'contosoFutureReason', canRequest: false, deadLetterCount: null }),
    );
    renderWithProvider(<CallDeadLettersCard />);
    expect(await screen.findByText('Not available on this deployment (contosoFutureReason).')).toBeInTheDocument();
    expect(screen.queryByText('Dead-lettered calls')).not.toBeInTheDocument();
  });

  it('offers nothing to do when the dead-letter queue is empty, and says so when Service Bus gives no count', async () => {
    mockFetch.mockResolvedValue(status({ deadLetterCount: 0 }));
    const { unmount } = renderWithProvider(<CallDeadLettersCard />);
    expect(await screen.findByText('The dead-letter queue is empty, so there is nothing to reprocess or clear.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reprocess' })).toBeDisabled();
    unmount();

    mockFetch.mockResolvedValue(status({ deadLetterCount: null, activeCount: null, countUnavailable: true }));
    renderWithProvider(<CallDeadLettersCard />);
    expect(await screen.findByText("Service Bus didn't give the count. The Service health page says why.")).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reprocess' })).toBeEnabled();
  });

  it('shows the refusal when another request got there first, and the request as it now stands', async () => {
    mockRequest.mockRejectedValue(new Error('Another request is already waiting or running.'));
    renderWithProvider(<CallDeadLettersCard />);

    fireEvent.click(await screen.findByRole('button', { name: 'Reprocess' }));

    expect(await screen.findByText('Another request is already waiting or running.')).toBeInTheDocument();
    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(2));
  });

  it('follows a pending request with a bounded back-off, pauses while the tab is hidden, and stops once it has finished', async () => {
    vi.useFakeTimers();
    mockFetch.mockResolvedValue(status({ latest: waiting(), canRequest: false }));
    renderWithProvider(<CallDeadLettersCard />);
    await advance(0);
    expect(mockFetch).toHaveBeenCalledTimes(1);

    // 5 s, then 10, 20, 30 and 60 s; and every 60 s after that.
    await advance(POLL_DELAYS_MS[0] - 1);
    expect(mockFetch).toHaveBeenCalledTimes(1);
    await advance(1);
    expect(mockFetch).toHaveBeenCalledTimes(2);
    await advance(10_000 + 20_000 + 30_000 + 60_000);
    expect(mockFetch).toHaveBeenCalledTimes(6);
    await advance(59_999);
    expect(mockFetch).toHaveBeenCalledTimes(6);
    await advance(1);
    expect(mockFetch).toHaveBeenCalledTimes(7);

    setVisibility('hidden');
    await advance(10 * 60_000);
    expect(mockFetch).toHaveBeenCalledTimes(7);

    setVisibility('visible');
    await advance(0);
    expect(mockFetch).toHaveBeenCalledTimes(8);

    mockFetch.mockResolvedValue(status({ latest: finished() }));
    await advance(60_000);
    expect(mockFetch).toHaveBeenCalledTimes(9);
    expect(screen.getByText('Finished')).toBeInTheDocument();
    await advance(30 * 60_000);
    expect(mockFetch).toHaveBeenCalledTimes(9);
  });

  it('is not shown to a reader without the Administration permission, and asks the server nothing', async () => {
    renderWithProvider(<CallDeadLettersCard />, { access: { administration: false, seePii: true } });

    await act(async () => {
      await Promise.resolve();
    });
    expect(screen.queryByText('Dead-lettered Teams calls')).not.toBeInTheDocument();
    expect(mockFetch).not.toHaveBeenCalled();
  });

  it('is written in Spanish for a Spanish reader, with Spanish number formats, while the codes stay as stored', async () => {
    await loadCatalog('es');
    mockFetch.mockResolvedValue(status({ latest: finished() }));
    renderWithProvider(<CallDeadLettersCard />, { language: 'es' });

    expect(await screen.findByText('Llamadas de Teams en la cola de mensajes fallidos')).toBeInTheDocument();
    expect(screen.getByText('12.345')).toBeInTheDocument();
    expect(screen.getByText('Terminada')).toBeInTheDocument();
    expect(screen.getByText(/^Terminada el 10 nov 2026, 9:05 UTC\.$|^Terminada el 10 nov 2026, 09:05 UTC\.$/)).toBeInTheDocument();
    expect(screen.getByText('Devueltas a la cola: 12.345')).toBeInTheDocument();
    expect(screen.getByText('No se podrán importar nunca tal como están: 12')).toBeInTheDocument();
    expect(screen.getByText('Se agotaron los reintentos')).toBeInTheDocument();
    expect(screen.getByText('Sin motivo registrado')).toBeInTheDocument();
    expect(screen.getByText('CallImportRetriesExhausted')).toBeInTheDocument();
    expect(screen.getByText('ContosoFutureReason')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reprocesar' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Vaciar...' })).toBeInTheDocument();
    expect(screen.queryByText('Reprocess')).not.toBeInTheDocument();
  });
});
