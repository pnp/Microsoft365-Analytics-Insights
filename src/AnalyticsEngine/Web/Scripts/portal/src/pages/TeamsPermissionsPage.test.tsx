import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import type { Team } from '@microsoft/microsoft-graph-types';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog, translateStatic } from '../i18n';
import TeamsPermissionsPage from './TeamsPermissionsPage';
import { fetchGraphToken } from '../auth/siteToken';
import { fetchMsGraph, GraphRequestError, GRAPH_ENDPOINTS } from '../auth/graph';

vi.mock('../auth/siteToken', () => ({ fetchGraphToken: vi.fn() }));
vi.mock('../auth/graph', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../auth/graph')>()),
  fetchMsGraph: vi.fn(),
}));

// The list reads and writes each Team's authorisation through its own API. This suite is about how
// the page gets - or can't get - a Graph token, so the list is reduced to the names it was given.
vi.mock('../components/teams/TeamList', () => ({
  default: ({ teamsList }: { teamsList: Team[] }) => (
    <ul>
      {teamsList.map((team) => (
        <li key={team.id}>{team.displayName}</li>
      ))}
    </ul>
  ),
}));

const ROUTE = '/admin/teams-permissions';
const CONNECT = 'Connect to Microsoft Teams';
const NOT_CONNECTED = "Microsoft Teams isn't connected";

/** What the page leaves in the address bar. */
function SearchProbe() {
  return <output data-testid="search">{useLocation().search}</output>;
}

function renderAt(entry: string, language: 'en' | 'es' = 'en') {
  return renderWithProvider(
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route
          path={ROUTE}
          element={
            <>
              <TeamsPermissionsPage />
              <SearchProbe />
            </>
          }
        />
      </Routes>
    </MemoryRouter>,
    { language },
  );
}

beforeEach(() => {
  vi.mocked(fetchGraphToken).mockReset();
  vi.mocked(fetchMsGraph).mockReset();
});

/**
 * Issue #670: signing in no longer captures a Graph token, because doing so needed the delegated Teams
 * permissions and locked every user out of the portal when a tenant hadn't granted them. So "no token"
 * is now the normal state of this page until an admin connects, not a fault.
 */
describe('Teams permissions page: connecting to Microsoft Teams', () => {
  it('offers to connect when the site has no Graph token, rather than reporting an error', async () => {
    vi.mocked(fetchGraphToken).mockResolvedValue(null);

    renderAt(ROUTE);

    const connect = await screen.findByRole('link', { name: CONNECT });
    expect(connect).toHaveAttribute('href', '/Account/ConnectTeams');
    expect(screen.getByText(/It doesn't ask for these when you sign in/)).toBeVisible();
    expect(screen.getByText("Your Teams will be listed here once you've connected to Microsoft Teams.")).toBeVisible();
    expect(screen.queryByText(NOT_CONNECTED)).not.toBeInTheDocument();
    expect(fetchMsGraph).not.toHaveBeenCalled();
  });

  it('lists the admin\'s Teams, with no connect prompt, once there is a token', async () => {
    vi.mocked(fetchGraphToken).mockResolvedValue({ accessToken: 'synthetic-token' });
    vi.mocked(fetchMsGraph).mockImplementation(async (url: string) =>
      url === GRAPH_ENDPOINTS.ME
        ? { displayName: 'Ada Contoso' }
        : { value: [{ id: '00000000-0000-0000-0000-000000000001', displayName: 'Contoso Sales' }] },
    );

    renderAt(ROUTE);

    expect(await screen.findByText('Contoso Sales')).toBeVisible();
    expect(screen.getByText('Your Teams - Ada Contoso')).toBeVisible();
    expect(screen.queryByRole('link', { name: CONNECT })).not.toBeInTheDocument();
  });

  it('reports a failed Graph call, rather than saying the admin is in no Teams', async () => {
    vi.mocked(fetchGraphToken).mockResolvedValue({ accessToken: 'synthetic-token' });
    vi.mocked(fetchMsGraph).mockRejectedValue(new GraphRequestError(403));

    renderAt(ROUTE);

    expect(await screen.findByText('Unable to fetch joined teams.')).toBeVisible();
  });

  it('explains a missing admin consent after a failed connection, with Entra ID\'s error code', async () => {
    vi.mocked(fetchGraphToken).mockResolvedValue(null);

    renderAt(`${ROUTE}?teamsConnect=consent_required&teamsConnectError=AADSTS65001`);

    expect(await screen.findByText(NOT_CONNECTED)).toBeVisible();
    expect(screen.getByText(/hasn't granted the delegated Teams permissions this page needs/)).toBeVisible();
    expect(screen.getByText('Microsoft Entra ID error code: AADSTS65001')).toBeVisible();
    // Still offered, for once an administrator has granted consent.
    expect(await screen.findByRole('link', { name: CONNECT })).toBeVisible();
  });

  it('says a declined request was declined, not that consent is missing', async () => {
    vi.mocked(fetchGraphToken).mockResolvedValue(null);

    renderAt(`${ROUTE}?teamsConnect=access_denied&teamsConnectError=AADSTS65004`);

    expect(await screen.findByText(/was cancelled or declined, so nothing was connected/)).toBeVisible();
    expect(screen.queryByText(/hasn't granted the delegated Teams permissions/)).not.toBeInTheDocument();
  });

  it('shows the outcome once, then takes it out of the address bar', async () => {
    vi.mocked(fetchGraphToken).mockResolvedValue(null);

    renderAt(`${ROUTE}?teamsConnect=consent_required&teamsConnectError=AADSTS65001`);

    expect(await screen.findByText(NOT_CONNECTED)).toBeVisible();
    await waitFor(() => expect(screen.getByTestId('search')).toHaveTextContent(/^$/));
    // Removing it from the URL must not remove it from the page the admin is reading.
    expect(screen.getByText(NOT_CONNECTED)).toBeVisible();
  });

  it('treats an outcome it does not know as a failure, and never shows a code that is not a plain identifier', async () => {
    vi.mocked(fetchGraphToken).mockResolvedValue(null);

    renderAt(`${ROUTE}?teamsConnect=constructor&teamsConnectError=${encodeURIComponent('<img src=x>')}`);

    expect(await screen.findByText(/couldn't get the Teams permissions for your account/)).toBeVisible();
    expect(screen.queryByText(/error code/)).not.toBeInTheDocument();
  });

  it('explains the connection in Spanish', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1]) => translateStatic('es', key);
    vi.mocked(fetchGraphToken).mockResolvedValue(null);

    renderAt(`${ROUTE}?teamsConnect=consent_required&teamsConnectError=AADSTS65001`, 'es');

    expect(await screen.findByRole('link', { name: es('admin.teamsPermissions.connect.button') })).toBeVisible();
    expect(screen.getByText(es('admin.teamsPermissions.connect.outcomeTitle'))).toBeVisible();
    expect(screen.getByText(es('admin.teamsPermissions.noTokenMessage'))).toBeVisible();
    expect(screen.getByText('Código de error de Microsoft Entra ID: AADSTS65001')).toBeVisible();
    expect(document.body.textContent).not.toContain(CONNECT);
  });
});
