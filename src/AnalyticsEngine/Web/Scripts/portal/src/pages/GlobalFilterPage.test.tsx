import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import {
  GlobalFilterApiError,
  fetchGlobalFilter,
  previewGlobalFilter,
  saveGlobalFilter,
} from '../api/globalFilterApi';
import { fetchUserFilterDimensions, fetchUserFilterValues } from '../api/userFilterApi';
import { resetUserFilterDimensionsCache } from '../components/userFilter/useUserFilterDimensions';
import { GlobalFilterProvider } from '../components/globalFilter/GlobalFilterProvider';
import type { GlobalFilterAdmin, GlobalFilterClauseEcho } from '../types/globalFilter';
import type { UserFilterDimension } from '../types/userFilter';
import GlobalFilterPage from './GlobalFilterPage';

vi.mock('../api/globalFilterApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/globalFilterApi')>();
  return {
    ...actual,
    fetchGlobalFilter: vi.fn(),
    saveGlobalFilter: vi.fn(),
    previewGlobalFilter: vi.fn(),
    fetchEffectiveGlobalFilter: vi.fn(),
  };
});

vi.mock('../api/userFilterApi', () => ({
  fetchUserFilterDimensions: vi.fn(),
  fetchUserFilterValues: vi.fn(),
}));

vi.mock('../components/toast', () => ({
  default: { success: vi.fn(), error: vi.fn(), info: vi.fn(), warning: vi.fn() },
  notify: vi.fn(),
  notifySuccess: vi.fn(),
  notifyError: vi.fn(),
}));

const DIMENSIONS: UserFilterDimension[] = [
  { key: 'department', kind: 'entra', name: null, orgTypeId: null, distinctValues: 3, peopleWithValue: 90, supportsTextMatch: true, fixedValues: false },
  { key: 'userType', kind: 'entra', name: null, orgTypeId: null, distinctValues: 2, peopleWithValue: 100, supportsTextMatch: false, fixedValues: true },
];

function clause(dimension: string, values: string[], overrides: Partial<GlobalFilterClauseEcho> = {}): GlobalFilterClauseEcho {
  return {
    join: 'and',
    dimension,
    operator: 'is',
    values,
    includeNotSet: false,
    viewerAttribute: null,
    viewerValue: null,
    unresolved: false,
    ...overrides,
  };
}

function admin(overrides: Partial<GlobalFilterAdmin> = {}): GlobalFilterAdmin {
  return {
    filter: '[{"d":"department","v":[],"vu":"department"}]',
    clauses: [clause('department', [], { viewerAttribute: 'department' })],
    revision: 3,
    modifiedUtc: '2026-09-30T10:00:00Z',
    modifiedBy: 'admin@contoso.com',
    storageAvailable: true,
    rolesEnforced: true,
    invalid: false,
    ...overrides,
  };
}

function renderPage(refresh = vi.fn(async () => {})) {
  renderWithProvider(
    <GlobalFilterProvider value={{ refresh }}>
      <GlobalFilterPage />
    </GlobalFilterProvider>,
  );
  return refresh;
}

beforeEach(() => {
  vi.clearAllMocks();
  resetUserFilterDimensionsCache();
  vi.mocked(fetchUserFilterDimensions).mockResolvedValue({ people: 100, loadedUtc: '2026-09-01T00:00:00Z', dimensions: DIMENSIONS });
  vi.mocked(fetchUserFilterValues).mockResolvedValue({
    dimension: 'department',
    values: [],
    totalMatching: 0,
    truncated: false,
    peopleWithoutValue: 0,
  });
  vi.mocked(previewGlobalFilter).mockResolvedValue({
    active: true,
    applied: true,
    bypassed: false,
    canBypass: true,
    revision: 0,
    invalid: false,
    filter: {
      clauses: [clause('department', [], { viewerAttribute: 'department', viewerValue: 'Sales' })],
      matchedPeople: 12,
      directoryPeople: 100,
      viewerFound: true,
      unknownDimensions: [],
      dimensionNames: {},
    },
  });
});

describe('GlobalFilterPage', () => {
  it('shows the saved conditions as the administrator wrote them, and what they mean for them', async () => {
    vi.mocked(fetchGlobalFilter).mockResolvedValue(admin());
    renderPage();

    expect(await screen.findByText('the viewer’s own value')).toBeVisible();
    expect(screen.getByText('Readers see only people where Department is the viewer’s own value.')).toBeVisible();
    expect(await screen.findByText('You would see 12 of the 100 people in the directory.', undefined, { timeout: 3000 })).toBeVisible();
    expect(screen.getByText('For you, that means: Department is Sales (from your profile).')).toBeVisible();
    expect(previewGlobalFilter).toHaveBeenCalledWith('[{"d":"department","v":[],"vu":"department"}]', expect.anything());
    expect(screen.getByText(/by admin@contoso\.com/)).toBeVisible();

    // Nothing has changed, so there is nothing to save.
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('warns when portal roles are not enforced, since then anyone can switch the filter off', async () => {
    vi.mocked(fetchGlobalFilter).mockResolvedValue(admin({ rolesEnforced: false }));
    renderPage();

    expect(await screen.findByText(/Portal roles aren’t enforced on this deployment/)).toBeVisible();
  });

  it('cannot save until the database has been upgraded, and names the script that does it', async () => {
    vi.mocked(fetchGlobalFilter).mockResolvedValue(admin({ storageAvailable: false, clauses: [], filter: '', revision: 0 }));
    renderPage();

    expect(await screen.findByText(/202610011330001_PortalGlobalFilter\.manual\.sql/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
  });

  it('saves against the revision it opened, then refreshes the filter every Insights page shows', async () => {
    vi.mocked(fetchGlobalFilter).mockResolvedValue(admin());
    vi.mocked(saveGlobalFilter).mockResolvedValue(admin({ clauses: [], filter: '', revision: 4 }));
    const refresh = renderPage();

    await screen.findByText('the viewer’s own value');
    await userEvent.click(screen.getByRole('button', { name: 'Department is the viewer’s own value Remove' }));
    expect(screen.getByText('Unsaved changes')).toBeVisible();

    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(saveGlobalFilter).toHaveBeenCalledWith('', 3));
    await waitFor(() => expect(refresh).toHaveBeenCalled());
    expect(screen.queryByText('Unsaved changes')).not.toBeInTheDocument();
  });

  it('refuses to overwrite someone else’s change, and offers to reload it', async () => {
    vi.mocked(fetchGlobalFilter).mockResolvedValue(admin());
    vi.mocked(saveGlobalFilter).mockRejectedValue(new GlobalFilterApiError('conflict', 'revisionConflict', 409));
    renderPage();

    await screen.findByText('the viewer’s own value');
    await userEvent.click(screen.getByRole('button', { name: 'Department is the viewer’s own value Remove' }));
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText(/Someone else changed the global filter after you opened it/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();

    vi.mocked(fetchGlobalFilter).mockResolvedValue(admin({ revision: 5 }));
    await userEvent.click(screen.getByRole('button', { name: 'Reload' }));
    await waitFor(() => expect(fetchGlobalFilter).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByText(/Someone else changed/)).not.toBeInTheDocument());
  });

  it('lets an unreadable saved filter be replaced, even with no conditions', async () => {
    vi.mocked(fetchGlobalFilter).mockResolvedValue(admin({ invalid: true, clauses: [], filter: '' }));
    renderPage();

    expect(await screen.findByText(/The saved filter can’t be read by this version of the portal/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled();
  });

  it('is translated', async () => {
    vi.mocked(fetchGlobalFilter).mockResolvedValue(admin());
    await loadCatalog('es');
    renderWithProvider(
      <GlobalFilterProvider value={{}}>
        <GlobalFilterPage />
      </GlobalFilterProvider>,
      { language: 'es' },
    );

    expect(await screen.findByText('el valor propio del lector', undefined, { timeout: 5000 })).toBeVisible();
    expect(screen.getByText('Filtro global de informes')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled();
  });
});
