import { fireEvent, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProvider } from '../../test/renderWithProvider';
import CostConnectionPanel from './CostConnectionPanel';
import { changeCostConnection, fetchCostCollection, fetchCostConnection, type CostCollectionStatus, type CostConnectionStatus } from '../../api/copilotCostConnectionApi';

vi.mock('../../api/copilotCostConnectionApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/copilotCostConnectionApi')>();
  return { ...actual, changeCostConnection: vi.fn(), fetchCostConnection: vi.fn(), fetchCostCollection: vi.fn() };
});

const connected: CostConnectionStatus = { status: 'connected', csrfToken: 'test-csrf' };

describe('CostConnectionPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.location.hash = '';
  });

  it('does not contact storage or expose controls until status is requested', () => {
    renderWithProvider(<CostConnectionPanel />);
    expect(fetchCostConnection).not.toHaveBeenCalled();
    expect(fetchCostCollection).not.toHaveBeenCalled();
    expect(screen.queryByRole('button', { name: 'Connect reader' })).not.toBeInTheDocument();
    expect(screen.getByText(/Graph beta: test\/pilot use only/)).toBeVisible();
  });

  it('keeps missing storage unavailable instead of offering an in-memory connection', async () => {
    vi.mocked(fetchCostConnection).mockResolvedValue({ status: 'storageRequired' });
    renderWithProvider(<CostConnectionPanel />, { language: 'es' });
    fireEvent.click(await screen.findByRole('button', { name: 'Comprobar conexión' }));
    expect(await screen.findByText(/No existe alternativa en memoria/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Conectar lector' })).not.toBeInTheDocument();
  });

  it('requires confirmation before disconnecting and preserves the history warning', async () => {
    vi.mocked(fetchCostConnection).mockResolvedValueOnce(connected).mockResolvedValue({ status: 'disconnected', csrfToken: 'test-csrf' });
    vi.mocked(changeCostConnection).mockResolvedValue({});
    renderWithProvider(<CostConnectionPanel />);
    fireEvent.click(screen.getByRole('button', { name: 'Check connection' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Disconnect reader' }));
    expect(changeCostConnection).not.toHaveBeenCalled();
    expect(screen.getByText(/Historical SQL data is preserved/)).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    await waitFor(() => expect(changeCostConnection).toHaveBeenCalledWith('disconnect', 'test-csrf'));
    expect(await screen.findByText('No dedicated reader is connected.')).toBeVisible();
  });

  it('collects bounded slices only after authorization and never labels observations approved', async () => {
    const completed: CostConnectionStatus = {
      ...connected, probe: { phase: 'observed', directoryUsers: 100, inventoryComplete: true, observed: 90,
        measured: 70, empty: 10, partial: 5, unevaluated: 10, failed: 10, denied: 10, lastOutcome: 'sliceCompleted' },
    };
    vi.mocked(fetchCostConnection).mockResolvedValueOnce(connected).mockResolvedValueOnce(connected).mockResolvedValue(completed);
    vi.mocked(changeCostConnection).mockResolvedValue({});
    renderWithProvider(<CostConnectionPanel />);
    fireEvent.click(screen.getByRole('button', { name: 'Check connection' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Run or resume read-only probe' }));
    expect(changeCostConnection).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(await screen.findByText(/feasibility is NOT approved/)).toBeVisible();
    expect(changeCostConnection).toHaveBeenCalledTimes(1);
    expect(changeCostConnection).toHaveBeenCalledWith('probe', 'test-csrf');
  });

  it('announces callback failures without displaying unknown server text', () => {
    window.location.hash = '#/admin/configuration?costConnection=untrusted-payload';
    renderWithProvider(<CostConnectionPanel />);
    expect(screen.getByRole('alert')).toHaveTextContent('An unrecognised connection state');
    expect(screen.queryByText('untrusted-payload')).not.toBeInTheDocument();
  });

  it('loads collection status separately without probing or implying completed coverage', async () => {
    const inventory: CostCollectionStatus = {
      status: 'available', enabled: true, phase: 'inventory', directoryUsers: 100,
      inventoryComplete: false, observed: 0, pending: 100, retrying: 0, failed: 0, denied: 0,
      measured: 0, empty: 0, partial: 0, unevaluated: 0,
    };
    vi.mocked(fetchCostCollection).mockResolvedValue(inventory);
    renderWithProvider(<CostConnectionPanel />);
    fireEvent.click(screen.getByRole('button', { name: 'Check scheduled collection' }));
    expect(await screen.findByText(/Building a complete directory inventory/)).toBeVisible();
    expect(screen.getByText(/directory count is provisional/)).toBeVisible();
    expect(fetchCostConnection).not.toHaveBeenCalled();
    expect(changeCostConnection).not.toHaveBeenCalled();
  });

  it('does not infer success from an unknown collector phase or display its raw text', async () => {
    vi.mocked(fetchCostCollection).mockResolvedValue({
      status: 'available', enabled: true, phase: 'untrusted-phase', inventoryComplete: true,
      directoryUsers: 100, observed: 80, pending: 15, retrying: 5, failed: 0, denied: 0,
      measured: 70, empty: 10, partial: 0, unevaluated: 0,
    });
    renderWithProvider(<CostConnectionPanel />);
    fireEvent.click(screen.getByRole('button', { name: 'Check scheduled collection' }));
    expect(await screen.findByText(/unrecognised collector phase/)).toBeVisible();
    expect(screen.queryByText('untrusted-phase')).not.toBeInTheDocument();
  });
});
