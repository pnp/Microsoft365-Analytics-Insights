import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderWithProvider } from '../test/renderWithProvider';
import CopilotCostConnectionPage from './CopilotCostConnectionPage';
import { changeCostConnection, costConnectionText, fetchCostCollection, fetchCostConnection, type CostCollectionStatus, type CostConnectionStatus } from '../api/copilotCostConnectionApi';

vi.mock('../api/copilotCostConnectionApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/copilotCostConnectionApi')>();
  return { ...actual, changeCostConnection: vi.fn(), fetchCostConnection: vi.fn(), fetchCostCollection: vi.fn() };
});

const connected: CostConnectionStatus = { status: 'connected', csrfToken: 'test-csrf' };

describe('CopilotCostConnectionPage', () => {
  afterEach(() => { vi.unstubAllGlobals(); });

  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(fetchCostConnection).mockResolvedValue({ status: 'disconnected', csrfToken: 'test-csrf' });
    window.location.hash = '';
  });

  it('loads status automatically without starting a data check or reading SQL history', async () => {
    renderWithProvider(<CopilotCostConnectionPage />);
    expect(await screen.findByText('No dedicated reader is connected.')).toBeVisible();
    expect(fetchCostConnection).toHaveBeenCalledTimes(1);
    expect(fetchCostCollection).not.toHaveBeenCalled();
    expect(changeCostConnection).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: 'Connect reader' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Run or resume data check' })).toBeDisabled();
    expect(screen.getByText('Copilot credit history (preview)')).toBeVisible();
    expect(screen.queryByText(/not supported for production/)).not.toBeInTheDocument();
  });

  it.each(['en', 'es'] as const)('explains portal, reader and runtime permissions in %s before connecting', async (language) => {
    renderWithProvider(<CopilotCostConnectionPage />, { language });
    fireEvent.click(await screen.findByText(language === 'es' ? 'Permisos y configuración de la conexión' : 'Connection permissions and setup'));
    expect(screen.getByText(/Portal\.Administration/)).toBeVisible();
    expect(screen.getByText(/CopilotCostManagement-UserData.Read.All/)).toHaveTextContent('CopilotCostManagement-Policy.Read.All');
    expect(screen.getByText(/CopilotCostManagement-Assignment.Read.All/)).toHaveTextContent('CopilotCostManagement.Read.All');
    expect(screen.getAllByText(/Global Reader/).length).toBeGreaterThan(0);
    expect(screen.getByText(/User.Read.All/)).toHaveTextContent('Storage Table Data Contributor');
    expect(screen.getByText(/\/copilot-cost\/callback/)).toBeVisible();
    expect(changeCostConnection).not.toHaveBeenCalled();
  });

  it('keeps missing storage unavailable instead of offering an in-memory connection', async () => {
    vi.mocked(fetchCostConnection).mockResolvedValue({ status: 'storageRequired' });
    renderWithProvider(<CopilotCostConnectionPage />, { language: 'es' });
    expect(await screen.findByText(/No existe alternativa en memoria/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Conectar lector' })).not.toBeInTheDocument();
    expect(screen.queryByText(/se ha podido leer la configuración guardada/)).not.toBeInTheDocument();
  });

  it('requires confirmation before disconnecting and preserves the history warning', async () => {
    vi.mocked(fetchCostConnection).mockResolvedValueOnce(connected).mockResolvedValue({ status: 'disconnected', csrfToken: 'test-csrf' });
    vi.mocked(changeCostConnection).mockResolvedValue({});
    renderWithProvider(<CopilotCostConnectionPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Disconnect reader' }));
    expect(changeCostConnection).not.toHaveBeenCalled();
    expect(screen.getByText(/Historical SQL data is preserved/)).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    await waitFor(() => expect(changeCostConnection).toHaveBeenCalledWith('disconnect', 'test-csrf'));
    expect(await screen.findByText('No dedicated reader is connected.')).toBeVisible();
  });

  it('collects bounded slices only after authorization and retains coverage qualifications', async () => {
    const completed: CostConnectionStatus = {
      ...connected, probe: { phase: 'observed', directoryUsers: 100, inventoryComplete: true, observed: 90,
        measured: 70, empty: 10, partial: 5, unevaluated: 10, failed: 10, denied: 10, lastOutcome: 'sliceCompleted' },
    };
    vi.mocked(fetchCostConnection).mockResolvedValueOnce(connected).mockResolvedValueOnce(connected).mockResolvedValue(completed);
    vi.mocked(changeCostConnection).mockResolvedValue({});
    renderWithProvider(<CopilotCostConnectionPage />);
    await screen.findByText('Connected — account saved');
    fireEvent.click(screen.getByRole('button', { name: 'Run or resume data check' }));
    expect(changeCostConnection).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Confirm' }));
    expect(await screen.findByText(/Finishing does not confirm that all users were covered/)).toBeVisible();
    expect(screen.getByText(/all pages were read using the application/)).toBeVisible();
    expect(screen.getByText(/Some users could not be fully checked/)).toBeVisible();
    expect(changeCostConnection).toHaveBeenCalledTimes(1);
    expect(changeCostConnection).toHaveBeenCalledWith('probe', 'test-csrf');
  });

  it('announces callback failures without displaying unknown server text', async () => {
    window.location.hash = '#/admin/copilot-cost-connection?costConnection=untrusted-payload';
    renderWithProvider(<CopilotCostConnectionPage />);
    await screen.findByText('No dedicated reader is connected.');
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
    renderWithProvider(<CopilotCostConnectionPage />);
    await screen.findByText('No dedicated reader is connected.');
    fireEvent.click(screen.getByRole('button', { name: 'Check scheduled collection' }));
    expect(await screen.findByText(/Reading the user list/)).toBeVisible();
    expect(screen.getByText(/user count is provisional/)).toBeVisible();
    expect(fetchCostConnection).toHaveBeenCalledTimes(1);
    expect(changeCostConnection).not.toHaveBeenCalled();
  });

  it('does not infer success from an unknown collector phase or display its raw text', async () => {
    vi.mocked(fetchCostCollection).mockResolvedValue({
      status: 'available', enabled: true, phase: 'untrusted-phase', inventoryComplete: true,
      directoryUsers: 100, observed: 80, pending: 15, retrying: 5, failed: 0, denied: 0,
      measured: 70, empty: 10, partial: 0, unevaluated: 0,
    });
    renderWithProvider(<CopilotCostConnectionPage />);
    await screen.findByText('No dedicated reader is connected.');
    fireEvent.click(screen.getByRole('button', { name: 'Check scheduled collection' }));
    expect(await screen.findByText(/unrecognised collector phase/)).toBeVisible();
    expect(screen.queryByText('untrusted-phase')).not.toBeInTheDocument();
  });

  it.each(['en', 'es'] as const)('refreshes saved progress after a failed data check and explains the failure in %s', async (language) => {
    const failed: CostConnectionStatus = {
      ...connected, probe: { phase: 'context', directoryUsers: 0, inventoryComplete: false,
        observed: 0, measured: 0, empty: 0, partial: 0, unevaluated: 0, failed: 0, denied: 0,
        lastOutcome: 'invalidResponse' },
    };
    vi.mocked(fetchCostConnection).mockResolvedValueOnce(connected).mockResolvedValueOnce(connected).mockResolvedValue(failed);
    renderWithProvider(<CopilotCostConnectionPage />, { language });
    await screen.findByText(language === 'es' ? 'Conectado: cuenta guardada' : 'Connected — account saved');
    vi.mocked(changeCostConnection).mockRejectedValue(new Error(costConnectionText('invalidResponse')));
    fireEvent.click(screen.getByRole('button', { name: language === 'es' ? 'Ejecutar o reanudar comprobación de datos' : 'Run or resume data check' }));
    fireEvent.click(screen.getByRole('button', { name: language === 'es' ? 'Confirmar' : 'Confirm' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(language === 'es' ? 'ni demuestra por sí solo un problema de permisos' : 'does not by itself prove a permissions problem');
    expect(await screen.findByText(language === 'es' ? /Paso actual o último paso:/ : /Current or last step:/)).toHaveTextContent(language === 'es' ? 'directivas de gasto' : 'spending policies');
    expect(screen.getByText(language === 'es' ? /Lista de usuarios: aún no se ha comprobado/ : /User list: not checked yet/)).toBeVisible();
    expect(fetchCostConnection).toHaveBeenCalledTimes(3);
    expect(changeCostConnection).toHaveBeenCalledTimes(1);
  });

  it('does not leave a green connected panel visible when refreshing status fails', async () => {
    vi.mocked(fetchCostConnection).mockResolvedValueOnce(connected).mockRejectedValue(new Error('Synthetic status failure'));
    renderWithProvider(<CopilotCostConnectionPage />);
    await screen.findByText('Connected — account saved');
    fireEvent.click(screen.getByRole('button', { name: 'Refresh status' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Synthetic status failure');
    expect(screen.queryByText('Connected — account saved')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Run or resume data check' })).toBeDisabled();
  });

  it('shows a reconnect action without a green connected panel when sign-in is required', async () => {
    vi.mocked(fetchCostConnection).mockResolvedValue({ status: 'reconnectRequired', csrfToken: 'test-csrf' });
    renderWithProvider(<CopilotCostConnectionPage />);
    expect(await screen.findByText(/Microsoft needs the connected account/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Reconnect same reader' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Run or resume data check' })).toBeDisabled();
    expect(screen.queryByText('Connected — account saved')).not.toBeInTheDocument();
  });

  it.each([
    { language: 'en', status: 'disconnected', button: 'Connect reader', opening: 'Opening sign-in...', explanation: /You will be taken to Microsoft to choose an account/ },
    { language: 'es', status: 'connected', button: 'Volver a conectar al mismo lector', opening: 'Abriendo el inicio de sesión...', explanation: /Se le redirigirá a Microsoft para elegir una cuenta/ },
  ] as const)('explains the redirect immediately and prevents repeated $language connection attempts', async ({ language, status, button, opening, explanation }) => {
    vi.mocked(fetchCostConnection).mockResolvedValue({ ...connected, status });
    let complete!: (result: { authorizationUrl: string }) => void;
    vi.mocked(changeCostConnection).mockReturnValue(new Promise(resolve => { complete = resolve; }));
    renderWithProvider(<CopilotCostConnectionPage />, { language });
    const connect = await screen.findByRole('button', { name: button });
    const assign = vi.fn();
    const location = { ...window.location, assign };
    vi.stubGlobal('window', new Proxy(window, {
      get: (target, key) => key === 'location' ? location : Reflect.get(target, key, target),
    }));

    fireEvent.click(connect);
    expect(screen.getByText(explanation)).toBeVisible();
    expect(screen.getByRole('button', { name: opening })).toBeDisabled();
    fireEvent.click(connect);
    expect(changeCostConnection).toHaveBeenCalledTimes(1);

    await act(async () => { complete({ authorizationUrl: 'https://login.microsoftonline.com/organizations/oauth2/v2.0/authorize' }); });
    expect(assign).toHaveBeenCalledOnce();
    expect(screen.getByRole('button', { name: opening })).toBeDisabled();
    expect(screen.getByText(explanation)).toBeVisible();
    fireEvent.click(connect);
    expect(changeCostConnection).toHaveBeenCalledTimes(1);
  });

  it.each(['failure', 'unsafeRedirect'] as const)('restores the connect control after $value', async (outcome) => {
    if (outcome === 'failure') vi.mocked(changeCostConnection).mockRejectedValue(new Error('Synthetic connection failure'));
    else vi.mocked(changeCostConnection).mockResolvedValue({ authorizationUrl: 'https://contoso.example/invalid-sign-in' });
    renderWithProvider(<CopilotCostConnectionPage />);
    fireEvent.click(await screen.findByRole('button', { name: 'Connect reader' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(outcome === 'failure' ? 'Synthetic connection failure' : 'callback could not be validated');
    expect(screen.getByRole('button', { name: 'Connect reader' })).toBeEnabled();
    expect(screen.queryByText(/You will be taken to Microsoft/)).not.toBeInTheDocument();
  });
});
