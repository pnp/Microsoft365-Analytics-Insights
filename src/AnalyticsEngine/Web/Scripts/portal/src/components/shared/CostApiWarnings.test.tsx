import { act, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProvider } from '../../test/renderWithProvider';
import { fetchCostApiHealth } from '../../api/costApiHealthApi';
import { loadCatalog } from '../../i18n';
import CostApiWarnings from './CostApiWarnings';

vi.mock('../../api/costApiHealthApi', () => ({ fetchCostApiHealth: vi.fn() }));

describe('Cost API connection warnings', () => {
  beforeEach(() => { vi.mocked(fetchCostApiHealth).mockReset(); });
  afterEach(() => vi.useRealTimers());

  it('explains failure and recovery limits without exposing admin links to ordinary readers', async () => {
    vi.mocked(fetchCostApiHealth).mockResolvedValue([
      { source: 'graph', status: 'reconnectRequired', lastSuccessfulRead: '2026-01-01T12:00:00Z' },
      { source: 'studio', status: 'permissionDenied' },
    ]);
    renderWithProvider(<CostApiWarnings />, { access: { administration: false, seePii: false } });
    expect(await screen.findByText('Copilot Credits collection needs attention')).toBeInTheDocument();
    expect(screen.getByText('Copilot Studio consumption needs attention')).toBeInTheDocument();
    expect(screen.getByText(/Missed daily balance snapshots cannot/)).toBeInTheDocument();
    expect(screen.getByText(/Studio re-reads the configured look-back window/)).toBeInTheDocument();
    expect(screen.getAllByText('Ask a portal administrator to check the saved billing connection.')).toHaveLength(2);
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('polls until an explicit successful read removes the warning and cancels on unmount', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.mocked(fetchCostApiHealth)
      .mockResolvedValueOnce([{ source: 'graph', status: 'reconnectRequired' }])
      .mockResolvedValue([{ source: 'graph', status: 'healthy', lastSuccessfulRead: '2026-01-01T12:00:00Z' }]);
    const view = renderWithProvider(<CostApiWarnings />);
    expect(await screen.findByRole('link', { name: 'Check billing connection' }))
      .toHaveAttribute('href', '#/admin/copilot-cost-connection');
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    await waitFor(() => expect(screen.queryByText('Copilot Credits collection needs attention')).not.toBeInTheDocument());
    const signal = vi.mocked(fetchCostApiHealth).mock.calls[0][0];
    view.unmount();
    expect(signal.aborted).toBe(true);
    await act(() => vi.advanceTimersByTimeAsync(60_000));
    expect(fetchCostApiHealth).toHaveBeenCalledTimes(2);
  });

  it('keeps disabled and intentionally disconnected sources quiet', async () => {
    vi.mocked(fetchCostApiHealth).mockResolvedValue([
      { source: 'graph', status: 'disabled' }, { source: 'studio', status: 'disconnected' },
    ]);
    renderWithProvider(<CostApiWarnings />);
    await waitFor(() => expect(fetchCostApiHealth).toHaveBeenCalledOnce());
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('does not silently treat an unavailable health check as a healthy connection', async () => {
    vi.mocked(fetchCostApiHealth).mockRejectedValue(new Error('Synthetic transport failure'));
    renderWithProvider(<CostApiWarnings />);
    expect(await screen.findByText('Cost connection health is unavailable. Do not assume the saved connections are working.')).toBeInTheDocument();
  });

  it('renders the connection failure and recovery advice in Spanish', async () => {
    await loadCatalog('es');
    vi.mocked(fetchCostApiHealth).mockResolvedValue([{ source: 'studio', status: 'failing' }]);
    renderWithProvider(<CostApiWarnings />, { language: 'es' });
    expect(await screen.findByText('El consumo de Copilot Studio necesita atención')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Revisar la conexión de facturación' })).toHaveAttribute('href', '#/admin/agent-cost-connection');
    expect(screen.queryByText('Copilot Studio consumption needs attention')).not.toBeInTheDocument();
  });
});
