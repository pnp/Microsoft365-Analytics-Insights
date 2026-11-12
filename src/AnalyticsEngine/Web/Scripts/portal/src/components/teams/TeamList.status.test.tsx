import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import { apiFetch } from '../../api/http';
import TeamList from './TeamList';

vi.mock('../../api/http', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../api/http')>()),
  apiFetch: vi.fn(),
}));

const mockFetch = vi.mocked(apiFetch);
const TEAMS = [{ id: '00000000-0000-0000-0000-000000000001', displayName: 'Contoso Sales' }];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  window.o365AnalyticsAuthAPI = '/api/TeamsAuthAPI';
  mockFetch.mockReset();
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('TeamList when the authorisation status cannot be read', () => {
  it('says to connect to Microsoft Teams on a 401, rather than staying busy with no message', async () => {
    mockFetch.mockResolvedValue(new Response(null, { status: 401 }));

    renderWithProvider(<TeamList teamsList={TEAMS} />);

    expect(await screen.findByText(/doesn't hold a Microsoft Teams connection for your account/)).toBeInTheDocument();
    expect(screen.getByText(/select Connect to Microsoft Teams/)).toBeInTheDocument();
    for (const checkbox of screen.getAllByRole('checkbox')) expect(checkbox).toBeDisabled();
  });

  it('says so in Spanish to a Spanish reader', async () => {
    await loadCatalog('es');
    mockFetch.mockResolvedValue(new Response(null, { status: 401 }));

    renderWithProvider(<TeamList teamsList={TEAMS} />, { language: 'es' });

    expect(await screen.findByText(/seleccione Conectar con Microsoft Teams/)).toBeInTheDocument();
  });

  it('shows any other failure, and recovers when trying again succeeds', async () => {
    mockFetch.mockResolvedValueOnce(json({ message: 'A future Teams authorisation error.' }, 500));

    renderWithProvider(<TeamList teamsList={TEAMS} />);

    expect(await screen.findByText('A future Teams authorisation error.')).toBeInTheDocument();
    for (const checkbox of screen.getAllByRole('checkbox')) expect(checkbox).toBeDisabled();

    mockFetch.mockResolvedValueOnce(json([{ teamId: TEAMS[0].id, hasAuthToken: false }]));
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    await waitFor(() => expect(screen.queryByText('A future Teams authorisation error.')).not.toBeInTheDocument());
    for (const checkbox of screen.getAllByRole('checkbox')) expect(checkbox).toBeEnabled();
  });
});
