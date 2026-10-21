import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { renderWithProvider } from '../test/renderWithProvider';
import AgentCostConnectionPage from './AgentCostConnectionPage';
import { fetchAgentCostConnection, disconnectAgentCostConnection, beginAgentCostConnection } from '../api/agentCostConnectionApi';
import { ROUTES, missingPermission } from '../navigation';
import { loadCatalog } from '../i18n';

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

beforeEach(async () => {
  vi.resetAllMocks();
  await loadCatalog('es');
  vi.mocked(fetchAgentCostConnection).mockResolvedValue({ state: 'connected' });
});

describe('billing administrator connection', () => {
  it('is only reachable in the Administration area', () => {
    const route = ROUTES.find((item) => item.path === '/admin/agent-cost-connection')!;
    expect(missingPermission(route, { administration: false, seePii: true })).toBe('administration');
    expect(missingPermission(route, { administration: true, seePii: false })).toBeNull();
  });

  it('shows the connected account and verification time in a success panel', async () => {
    vi.mocked(fetchAgentCostConnection).mockResolvedValue({
      state: 'connected',
      details: { account: 'billing-admin@contoso.example', verifiedUtc: '2026-10-01T12:30:00Z' },
    });
    renderPage();
    const panel = await screen.findByRole('status');
    expect(within(panel).getByText('Connected — no action needed')).toBeVisible();
    expect(within(panel).getByText('Administrator account: billing-admin@contoso.example')).toBeVisible();
    expect(within(panel).getByText(/Billing access verified when connected:/)).toHaveTextContent('2026');
    expect(panel.querySelector('svg')).toHaveAttribute('aria-hidden', 'true');
    expect(screen.queryByText(/Before you connect:/)).not.toBeInTheDocument();
    expect(within(panel).getByText(/does not confirm that the latest import succeeded/)).toBeVisible();
  });

  it('renders an existing connection without an invented verification date', async () => {
    vi.mocked(fetchAgentCostConnection).mockResolvedValue({
      state: 'connected', details: { account: 'billing-admin@contoso.example', verifiedUtc: null },
    });
    renderPage();
    expect(await screen.findByText('Administrator account: billing-admin@contoso.example')).toBeVisible();
    expect(screen.queryByText(/Billing access verified/)).not.toBeInTheDocument();
  });

  it('states when account details are unavailable', async () => {
    renderPage();
    expect(await screen.findByText('Administrator connected; account details are unavailable.')).toBeVisible();
  });

  it('shows reconnect-needed without a success tick or stale account', async () => {
    vi.mocked(fetchAgentCostConnection).mockResolvedValue({ state: 'reconnectNeeded' });
    renderPage();
    expect(await screen.findByText(/Reconnect needed/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Connect or reconnect an administrator' })).toBeEnabled();
    expect(screen.queryByText('Connected — no action needed')).not.toBeInTheDocument();
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

  it('removes stale success details when refreshing status fails', async () => {
    renderPage('en', '?connection=connected');
    await screen.findByText('Connected — no action needed');
    vi.mocked(fetchAgentCostConnection).mockRejectedValueOnce(new Error('Synthetic storage failure'));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(await screen.findByText('Synthetic storage failure')).toBeVisible();
    expect(screen.queryByText('Connected — no action needed')).not.toBeInTheDocument();
    expect(screen.queryByText(/Microsoft confirmed the account can read/)).not.toBeInTheDocument();
  });

  it('renders connected account details in Spanish without translating the account', async () => {
    vi.mocked(fetchAgentCostConnection).mockResolvedValue({
      state: 'connected', details: { account: 'billing-admin@contoso.example', verifiedUtc: '2026-10-01T12:30:00Z' },
    });
    renderPage('es');
    expect(await screen.findByText('Conectado — no es necesario hacer nada')).toBeVisible();
    expect(screen.getByText('Cuenta de administrador: billing-admin@contoso.example')).toBeVisible();
    expect(screen.getByText(/Acceso a la facturación verificado al conectar:/)).toBeVisible();
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
