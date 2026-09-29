import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, screen, fireEvent, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import UserImportPage from './UserImportPage';
import type { UserImportCheckpointStatus } from '../types/userImportCheckpoint';

const mockFetch = vi.fn();
const mockClear = vi.fn();

vi.mock('../api/userImportCheckpointApi', () => ({
  fetchUserImportCheckpoint: (...args: unknown[]) => mockFetch(...args),
  clearUserImportCheckpoint: (...args: unknown[]) => mockClear(...args),
}));

const KEY = 'UserDeltaCode-00000000-0000-0000-0000-000000000000-v2';

const status = (over: Partial<UserImportCheckpointStatus> = {}): UserImportCheckpointStatus => ({
  redisConfigured: true,
  userImportEnabled: true,
  checkpointStored: true,
  checkpointKey: KEY,
  lastCompletedUtc: '2026-09-01T10:00:00Z',
  intervalHours: 24,
  ...over,
});

/** Opens the confirmation and confirms. The click settles inside act, because clearing resolves asynchronously. */
async function openAndConfirmClear() {
  fireEvent.click(await screen.findByRole('button', { name: 'Clear checkpoint...' }));
  const confirm = await screen.findByRole('button', { name: 'Clear checkpoint' });
  await act(async () => {
    fireEvent.click(confirm);
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  mockFetch.mockResolvedValue(status());
  mockClear.mockResolvedValue({ checkpointCleared: true, lastCompletedCleared: true });
});

describe('UserImportPage', () => {
  it('shows whether a checkpoint is stored, where it is kept and when the import last completed', async () => {
    renderWithProvider(<UserImportPage />);

    expect(await screen.findByText('Yes - the next run reads only what has changed')).toBeInTheDocument();
    expect(screen.getByText('In Azure Cache for Redis')).toBeInTheDocument();
    // The key is data, shown verbatim so it can be matched to the documentation.
    expect(screen.getByText(KEY)).toBeInTheDocument();
    expect(screen.getByText(/10:00:00 UTC/)).toBeInTheDocument();
    expect(screen.getByText('At most once every 24 h')).toBeInTheDocument();
    expect(screen.getByText('Switched on')).toBeInTheDocument();
  });

  it('asks before clearing, then clears and runs the import on the next cycle by default', async () => {
    renderWithProvider(<UserImportPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Clear checkpoint...' }));
    expect(await screen.findByText('Clear the user import checkpoint?')).toBeInTheDocument();
    expect(mockClear).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Clear checkpoint' }));

    await waitFor(() => expect(mockClear).toHaveBeenCalledWith(true));
    expect(
      await screen.findByText('Checkpoint cleared. The next user import reads every user. It runs on the next import cycle.'),
    ).toBeInTheDocument();
    // The state is reloaded, so the page shows what the server now holds rather than what it assumes.
    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(2));
  });

  it('leaves the cadence alone when the admin says so, and says when the import will run instead', async () => {
    mockClear.mockResolvedValue({ checkpointCleared: true, lastCompletedCleared: false });
    renderWithProvider(<UserImportPage />);

    fireEvent.click(await screen.findByRole('checkbox', { name: 'Run the user import on the next import cycle' }));
    fireEvent.click(screen.getByRole('button', { name: 'Clear checkpoint...' }));
    expect(await screen.findByText('It runs once its 24 h interval has passed.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Clear checkpoint' }));

    await waitFor(() => expect(mockClear).toHaveBeenCalledWith(false));
    expect(
      await screen.findByText('Checkpoint cleared. The next user import reads every user. It runs once its 24 h interval has passed.'),
    ).toBeInTheDocument();
  });

  it('does nothing when the confirmation is cancelled', async () => {
    renderWithProvider(<UserImportPage />);

    fireEvent.click(await screen.findByRole('button', { name: 'Clear checkpoint...' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(screen.queryByText('Clear the user import checkpoint?')).not.toBeInTheDocument());
    expect(mockClear).not.toHaveBeenCalled();
  });

  it('keeps the dialog open and says why when clearing fails', async () => {
    mockClear.mockRejectedValue(new Error("Couldn't reach Azure Cache for Redis, where the checkpoint is kept."));
    renderWithProvider(<UserImportPage />);

    await openAndConfirmClear();

    expect(await screen.findByText("Couldn't reach Azure Cache for Redis, where the checkpoint is kept.")).toBeInTheDocument();
    expect(screen.getByText('Clear the user import checkpoint?')).toBeInTheDocument();
    expect(screen.queryByText(/Checkpoint cleared/)).not.toBeInTheDocument();
  });

  it('says so when there was no checkpoint to clear, rather than claiming it cleared one', async () => {
    mockFetch.mockResolvedValue(status({ checkpointStored: false }));
    mockClear.mockResolvedValue({ checkpointCleared: false, lastCompletedCleared: true });
    renderWithProvider(<UserImportPage />);

    expect(await screen.findByText('None - the next run reads every user')).toBeInTheDocument();
    await openAndConfirmClear();

    expect(
      await screen.findByText(
        'There was no stored checkpoint, so the next user import reads every user anyway. It runs on the next import cycle.',
      ),
    ).toBeInTheDocument();
  });

  it('explains there is nothing to clear without Redis, and offers no button', async () => {
    mockFetch.mockResolvedValue(status({ redisConfigured: false, checkpointStored: false, lastCompletedUtc: null }));
    renderWithProvider(<UserImportPage />);

    expect(await screen.findByText(/There is nothing to clear/)).toBeInTheDocument();
    expect(screen.getByText("Nowhere - Azure Cache for Redis isn't configured, so every run reads every user")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Clear checkpoint...' })).not.toBeInTheDocument();
    expect(screen.queryByText(KEY)).not.toBeInTheDocument();
  });

  it('offers no next-cycle option when the import already runs every cycle', async () => {
    mockFetch.mockResolvedValue(status({ intervalHours: 0 }));
    mockClear.mockResolvedValue({ checkpointCleared: true, lastCompletedCleared: false });
    renderWithProvider(<UserImportPage />);

    expect(await screen.findByText('On every import cycle')).toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();

    await openAndConfirmClear();

    await waitFor(() => expect(mockClear).toHaveBeenCalledWith(false));
    expect(
      await screen.findByText('Checkpoint cleared. The next user import reads every user. It runs on the next import cycle.'),
    ).toBeInTheDocument();
  });

  it('offers no next-cycle option when no completion is recorded for the interval to wait on', async () => {
    mockFetch.mockResolvedValue(status({ lastCompletedUtc: null }));
    mockClear.mockResolvedValue({ checkpointCleared: true, lastCompletedCleared: false });
    renderWithProvider(<UserImportPage />);

    expect(await screen.findByText('Not recorded')).toBeInTheDocument();
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument();

    await openAndConfirmClear();

    await waitFor(() => expect(mockClear).toHaveBeenCalledWith(false));
    expect(
      await screen.findByText('Checkpoint cleared. The next user import reads every user. It runs on the next import cycle.'),
    ).toBeInTheDocument();
  });

  it('warns that nothing runs while the import is switched off, and promises no schedule', async () => {
    mockFetch.mockResolvedValue(status({ userImportEnabled: false }));
    renderWithProvider(<UserImportPage />);

    expect(await screen.findByText('Switched off (GraphUsersMetadata)')).toBeInTheDocument();
    expect(screen.getByText(/The user import is switched off/)).toBeInTheDocument();

    await openAndConfirmClear();

    expect(await screen.findByText('Checkpoint cleared. The next user import reads every user.')).toBeInTheDocument();
    expect(screen.queryByText(/It runs on the next import cycle/)).not.toBeInTheDocument();
  });

  it('shows a load failure instead of an empty page', async () => {
    mockFetch.mockRejectedValue(new Error("Couldn't load the user import checkpoint (500)."));
    renderWithProvider(<UserImportPage />);

    expect(await screen.findByText("Couldn't load the user import checkpoint (500).")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Clear checkpoint...' })).not.toBeInTheDocument();
  });

  it('is written in Spanish for a Spanish reader, while the key stays as stored', async () => {
    await loadCatalog('es');
    renderWithProvider(<UserImportPage />, { language: 'es' });

    expect(await screen.findByText('Sí: la próxima ejecución solo lee lo que ha cambiado')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Borrar punto de control...' })).toBeInTheDocument();
    expect(screen.getByText(KEY)).toBeInTheDocument();
    expect(screen.queryByText('Clear checkpoint...')).not.toBeInTheDocument();
  });
});
