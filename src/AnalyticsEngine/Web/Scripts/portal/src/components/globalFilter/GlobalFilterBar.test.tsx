import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import type { GlobalFilterClauseEcho, GlobalFilterEffective, GlobalFilterEcho } from '../../types/globalFilter';
import GlobalFilterBar from './GlobalFilterBar';
import { GlobalFilterProvider, type GlobalFilterContextValue } from './GlobalFilterProvider';

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

function echo(clauses: GlobalFilterClauseEcho[], overrides: Partial<GlobalFilterEcho> = {}): GlobalFilterEcho {
  return {
    clauses,
    matchedPeople: 1234,
    directoryPeople: 50000,
    viewerFound: true,
    unknownDimensions: [],
    dimensionNames: {},
    ...overrides,
  };
}

function effective(overrides: Partial<GlobalFilterEffective> = {}): GlobalFilterEffective {
  return {
    active: true,
    applied: true,
    bypassed: false,
    canBypass: false,
    revision: 3,
    filter: echo([
      clause('department', [], { viewerAttribute: 'department', viewerValue: 'Sales' }),
      clause('userType', ['member']),
    ]),
    invalid: false,
    ...overrides,
  };
}

function renderBar(value: Partial<GlobalFilterContextValue>, options: Parameters<typeof renderWithProvider>[1] = {}, note?: string) {
  return renderWithProvider(
    <GlobalFilterProvider value={value}>
      <GlobalFilterBar note={note} />
    </GlobalFilterProvider>,
    options,
  );
}

beforeEach(() => vi.clearAllMocks());

describe('GlobalFilterBar', () => {
  it('shows nothing without a provider, or when no filter is defined', () => {
    renderWithProvider(<GlobalFilterBar />);
    expect(screen.queryByText('Set by your administrator')).not.toBeInTheDocument();
    expect(screen.queryByText('Filter set by a portal administrator')).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();

    renderBar({ effective: effective({ active: false, applied: false, filter: null }) });
    expect(screen.queryByText('Set by your administrator')).not.toBeInTheDocument();
  });

  it('shows the conditions locked, with the reader’s own value filled in', () => {
    renderBar({ effective: effective() }, {}, 'Team-level figures cover every team.');

    expect(screen.getByText('Set by your administrator')).toBeVisible();
    expect(screen.getByText('Sales (from your profile)')).toBeVisible();
    expect(screen.getByText('Member')).toBeVisible();
    expect(screen.getByText('1,234 of 50,000 people')).toBeVisible();
    expect(screen.getAllByText('Team-level figures cover every team.').length).toBeGreaterThan(0);

    // A reader can neither edit the filter nor switch it off.
    expect(screen.queryByRole('link', { name: 'Edit' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Switch off/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Remove/ })).not.toBeInTheDocument();
  });

  it('describes the filter on paper, where the bar is hidden', () => {
    renderBar({ effective: effective() });
    expect(
      screen.getByText('Only people where Department is Sales (from your profile) and User type is Member.'),
    ).toBeInTheDocument();
  });

  it('gives an administrator a link to edit it and a switch for their own view', async () => {
    const setBypassed = vi.fn(async () => {});
    renderBar({ effective: effective({ canBypass: true }), setBypassed });

    expect(screen.getByRole('link', { name: 'Edit' })).toHaveAttribute('href', '#/admin/global-filter');
    await userEvent.click(screen.getByRole('button', { name: 'Switch off for my view' }));
    expect(setBypassed).toHaveBeenCalledWith(true);
  });

  it('does not offer the editor to an administrator without See PII, who may still switch it off', () => {
    renderBar({ effective: effective({ canBypass: true }) }, { access: { administration: true, seePii: false } });

    expect(screen.queryByRole('link', { name: 'Edit' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Switch off for my view' })).toBeVisible();
  });

  it('says plainly when an administrator has switched it off for their own view', async () => {
    const setBypassed = vi.fn(async () => {});
    renderBar({ effective: effective({ canBypass: true, bypassed: true, applied: false }), setBypassed });

    expect(screen.getByText(/switched off for your view, so these reports cover everyone/)).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Switch back on' }));
    expect(setBypassed).toHaveBeenCalledWith(false);
  });

  it('warns a reader the directory does not hold that conditions on their own details match nobody', () => {
    renderBar({
      effective: effective({
        filter: echo([clause('department', [], { viewerAttribute: 'department', unresolved: true })], {
          viewerFound: false,
          matchedPeople: 0,
        }),
      }),
    });

    expect(screen.getByText('not recorded for you')).toBeVisible();
    expect(screen.getByText(/you aren’t in the user directory the reports use/)).toBeVisible();
  });

  it('warns when the filter matches nobody for this reader', () => {
    renderBar({ effective: effective({ filter: echo([clause('department', ['Sales'])], { matchedPeople: 0 }) }) });
    expect(screen.getByText('This filter matches nobody for you, so the reports will be empty.')).toBeVisible();
  });

  it('explains that reports are refused while the stored filter cannot be read', () => {
    renderBar({ effective: effective({ invalid: true, filter: null }) });
    expect(screen.getByText(/can’t be read by this version of the portal, so reports aren’t available/)).toBeVisible();
  });

  it('says when it could not check for a filter, and offers to try again', async () => {
    const refresh = vi.fn(async () => {});
    renderBar({ status: 'error', effective: null, refresh });

    expect(screen.getByText(/Couldn’t check whether a portal administrator has set a filter/)).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }));
    expect(refresh).toHaveBeenCalled();
  });

  it('is translated', async () => {
    // Loaded up front: the Spanish catalog is its own chunk, and under a full parallel run fetching it can
    // outlast the default one-second wait.
    await loadCatalog('es');
    renderBar({ effective: effective({ canBypass: true }) }, { language: 'es' });

    expect(await screen.findByText('Establecido por su administrador', undefined, { timeout: 5000 })).toBeVisible();
    expect(screen.getByText('Sales (según su perfil)')).toBeVisible();
    expect(screen.getByText('Miembro')).toBeVisible();
    expect(screen.getByText('1234 de 50.000 personas')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Desactivar en mi vista' })).toBeVisible();
  });
});
