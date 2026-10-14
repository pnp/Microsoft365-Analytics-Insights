import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { renderWithProvider } from '../test/renderWithProvider';
import AgentCostConnectionPage from './AgentCostConnectionPage';
import { fetchAgentCostConnection, disconnectAgentCostConnection, beginAgentCostConnection } from '../api/agentCostConnectionApi';
import { ROUTES, missingPermission } from '../navigation';

vi.mock('../api/agentCostConnectionApi', () => ({
  fetchAgentCostConnection: vi.fn(),
  disconnectAgentCostConnection: vi.fn(),
  beginAgentCostConnection: vi.fn(),
}));

function renderPage(language: 'en' | 'es' = 'en', outcome = '') {
  return renderWithProvider(
    <MemoryRouter initialEntries={[`/admin/agent-cost-connection${outcome}`]}>
      <AgentCostConnectionPage />
    </MemoryRouter>,
    { language },
  );
}

beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(fetchAgentCostConnection).mockResolvedValue({ state: 'connected' });
});

describe('billing administrator connection', () => {
  it('is only reachable in the Administration area', () => {
    const route = ROUTES.find((item) => item.path === '/admin/agent-cost-connection')!;
    expect(missingPermission(route, { administration: false, seePii: true })).toBe('administration');
    expect(missingPermission(route, { administration: true, seePii: false })).toBeNull();
  });

  it('shows reconnect-needed without rendering a credential or named administrator', async () => {
    vi.mocked(fetchAgentCostConnection).mockResolvedValue({ state: 'reconnectNeeded' });
    renderPage();
    expect(await screen.findByText(/Reconnect needed/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Connect or reconnect an administrator' })).toBeEnabled();
  });

  it('does not offer a non-durable connection without Storage', async () => {
    vi.mocked(fetchAgentCostConnection).mockResolvedValue({ state: 'storageNotConfigured' });
    renderPage();
    await screen.findByText(/no Azure Storage account configured/);
    expect(screen.getByRole('button', { name: 'Connect or reconnect an administrator' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Disconnect' })).toBeDisabled();
  });

  it('requires confirmation before disconnect and displays the durable result', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false).mockReturnValueOnce(true);
    vi.mocked(disconnectAgentCostConnection).mockResolvedValue({ state: 'disconnected' });
    renderPage();
    const button = await screen.findByRole('button', { name: 'Disconnect' });
    await waitFor(() => expect(button).toBeEnabled());
    fireEvent.click(button);
    expect(disconnectAgentCostConnection).not.toHaveBeenCalled();
    fireEvent.click(button);
    expect(await screen.findByText(/Not connected\./)).toBeVisible();
    expect(disconnectAgentCostConnection).toHaveBeenCalledOnce();
    confirm.mockRestore();
  });

  it('keeps a failed begin visible instead of claiming success', async () => {
    vi.mocked(beginAgentCostConnection).mockRejectedValue(new Error('Synthetic failure'));
    renderPage();
    const button = await screen.findByRole('button', { name: 'Connect or reconnect an administrator' });
    await waitFor(() => expect(button).toBeEnabled());
    fireEvent.click(button);
    expect(await screen.findByText('Synthetic failure')).toBeVisible();
  });

  it('renders the callback and disconnected state in Spanish', async () => {
    vi.mocked(fetchAgentCostConnection).mockResolvedValue({ state: 'disconnected' });
    renderPage('es', '?connection=consentOrPolicy');
    expect(await screen.findByText(/Sin conexión\./)).toBeVisible();
    expect(screen.getByText(/Microsoft Entra ID no permitió este inicio de sesión/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Conectar o volver a conectar un administrador' })).toBeEnabled();
    expect(screen.queryByText(/Microsoft could not authorize/)).not.toBeInTheDocument();
  });
});
