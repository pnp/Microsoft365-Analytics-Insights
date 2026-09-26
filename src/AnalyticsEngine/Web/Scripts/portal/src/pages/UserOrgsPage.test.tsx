import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../test/renderWithProvider';
import UserOrgsPage from './UserOrgsPage';
import type { UserOrgImportJob, UserOrgType } from '../types/userOrgs';

const fetchOrgTypes = vi.fn();
const fetchOrgValues = vi.fn();

vi.mock('../api/userOrgsApi', () => ({
  fetchOrgTypes: () => fetchOrgTypes(),
  fetchOrgValues: (...args: unknown[]) => fetchOrgValues(...args),
  fetchOrgMembers: vi.fn(),
  createOrgType: vi.fn(),
  updateOrgType: vi.fn(),
  deleteOrgType: vi.fn(),
  previewCsv: vi.fn(),
  importCsv: vi.fn(),
  fetchImportJob: vi.fn(),
  testEntraAttribute: vi.fn(),
  fetchAttributeCatalogue: vi.fn(() =>
    Promise.resolve({
      extensionAttributes: [],
      builtInProperties: [],
      employeeOrgDataProperties: [],
      directoryExtensions: [],
      discoveryWarning: null,
    }),
  ),
}));

function orgType(over: Partial<UserOrgType>): UserOrgType {
  return {
    id: 1,
    name: 'Cost Centre',
    source: 'entra',
    entraAttributeName: 'extensionAttribute1',
    isEnabled: true,
    assignedUserCount: 0,
    distinctValueCount: 0,
    createdUtc: '2026-01-01T00:00:00.000Z',
    modifiedUtc: null,
    lastRefreshedUtc: null,
    lastImport: null,
    ...over,
  };
}

function rowFor(name: string): HTMLElement {
  return screen.getByRole('row', { name: new RegExp(name) });
}

function failedImport(): UserOrgImportJob {
  return {
    id: 7,
    orgTypeId: 2,
    mode: 'merge',
    status: 'failed',
    fileName: 'orgs.csv',
    startedBy: 'admin@contoso.com',
    queuedUtc: '2026-03-05T09:30:00.000Z',
    finishedUtc: '2026-03-05T09:31:00.000Z',
    rowsTotal: 3,
    rowsApplied: 0,
    rowsCleared: 0,
    rowsUnknownUpn: 0,
    rowsInvalid: 0,
    errorMessage: 'The import failed.',
  };
}

describe('UserOrgsPage', () => {
  beforeEach(() => {
    fetchOrgTypes.mockReset();
    fetchOrgValues.mockReset();
    fetchOrgValues.mockResolvedValue({ orgTypeId: 1, page: 1, pageSize: 25, total: 0, items: [] });
  });

  it('opens the chosen type in "who is in each organisation" from its user count', async () => {
    fetchOrgTypes.mockResolvedValue([
      orgType({ id: 1, name: 'Cost Centre', assignedUserCount: 5 }),
      orgType({ id: 2, name: 'Business Unit', source: 'csv', entraAttributeName: null, assignedUserCount: 1234 }),
      orgType({ id: 3, name: 'Division', assignedUserCount: 0 }),
    ]);

    renderWithProvider(<UserOrgsPage />);

    // The first type is browsed until the admin picks another.
    expect(await screen.findByText('Who is in each organisation')).toBeInTheDocument();
    await waitFor(() => expect(fetchOrgValues).toHaveBeenCalledWith(1, expect.anything(), expect.anything()));

    // The link's name says what it does; the visible text stays the number.
    const link = within(rowFor('Business Unit')).getByRole('button', { name: 'View the 1,234 users in Business Unit' });
    expect(link).toHaveTextContent('1,234');
    await userEvent.click(link);

    await waitFor(() => expect(fetchOrgValues).toHaveBeenLastCalledWith(2, expect.anything(), expect.anything()));
    const typeSelect = screen.getByRole('combobox', { name: 'Organisation type' });
    expect(typeSelect).toHaveValue('2');
    // Focus follows the view, or a keyboard user is left on a link that has scrolled away.
    expect(typeSelect).toHaveFocus();

    // Nobody to show, so no link to show them with.
    expect(within(rowFor('Division')).queryByRole('button', { name: /View the/ })).not.toBeInTheDocument();
  });

  it('shows when each type was last refreshed', async () => {
    fetchOrgTypes.mockResolvedValue([
      orgType({ id: 1, name: 'Cost Centre', lastRefreshedUtc: '2026-03-04T05:06:00.000Z' }),
      orgType({
        id: 2,
        name: 'Business Unit',
        source: 'csv',
        entraAttributeName: null,
        lastRefreshedUtc: '2026-03-05T09:30:00.000Z',
      }),
    ]);

    renderWithProvider(<UserOrgsPage />);

    expect(await screen.findByRole('columnheader', { name: 'Last refreshed' })).toBeInTheDocument();
    expect(within(rowFor('Cost Centre')).getByText(/2026/)).toBeInTheDocument();
    expect(within(rowFor('Business Unit')).getByText(/2026/)).toBeInTheDocument();
  });

  it('says what a never-refreshed type is waiting for, by source', async () => {
    // "Never" alone would leave an admin who has just created a type wondering whether it is broken.
    // What they are waiting for differs by source - and a disabled type is not waiting for anything,
    // because it is never imported.
    fetchOrgTypes.mockResolvedValue([
      orgType({ id: 1, name: 'Cost Centre' }),
      orgType({ id: 2, name: 'Business Unit', source: 'csv', entraAttributeName: null }),
      orgType({ id: 3, name: 'Division', isEnabled: false }),
    ]);

    renderWithProvider(<UserOrgsPage />);

    await screen.findByRole('columnheader', { name: 'Last refreshed' });
    expect(within(rowFor('Cost Centre')).getByText('Waiting for the next user import')).toBeInTheDocument();
    expect(within(rowFor('Business Unit')).getByText('No successful import yet')).toBeInTheDocument();
    expect(within(rowFor('Division')).getByText('Never')).toBeInTheDocument();
  });

  it('does not contradict a failed import shown beside it', async () => {
    // The Source column already says a file was imported and failed, so "no file imported" next to it
    // would read as a contradiction. Nothing was applied, and that is what the column must say.
    fetchOrgTypes.mockResolvedValue([
      orgType({ id: 2, name: 'Business Unit', source: 'csv', entraAttributeName: null, lastImport: failedImport() }),
    ]);

    renderWithProvider(<UserOrgsPage />);

    await screen.findByRole('columnheader', { name: 'Last refreshed' });
    const row = rowFor('Business Unit');
    expect(within(row).getByText(/last imported .* \(failed\)/)).toBeInTheDocument();
    expect(within(row).getByText('No successful import yet')).toBeInTheDocument();
  });

  it('explains a successful import whose values a change of source discarded', async () => {
    // Switching a CSV type to Entra and back discards its values and clears its refresh time, but keeps
    // its import history - so "(succeeded)" can sit beside a type with nothing in it. Saying "no
    // successful import yet" there would be false, and so would "Never" for a disabled one.
    const succeeded = { ...failedImport(), status: 'succeeded' as const, errorMessage: null };
    fetchOrgTypes.mockResolvedValue([
      orgType({ id: 2, name: 'Business Unit', source: 'csv', entraAttributeName: null, lastImport: succeeded }),
      orgType({
        id: 3,
        name: 'Squad',
        source: 'csv',
        entraAttributeName: null,
        isEnabled: false,
        lastImport: { ...succeeded, id: 8, orgTypeId: 3 },
      }),
    ]);

    renderWithProvider(<UserOrgsPage />);

    await screen.findByRole('columnheader', { name: 'Last refreshed' });
    expect(within(rowFor('Business Unit')).getByText('Cleared when the source changed')).toBeInTheDocument();
    expect(within(rowFor('Squad')).getByText('Cleared when the source changed')).toBeInTheDocument();
  });

  it('tells the admin what a stalled Entra refresh time means', async () => {
    // The time going stale is the only visible sign that Graph has started rejecting an attribute:
    // the user import carries on without every organisation attribute, so all Entra types stop at once.
    fetchOrgTypes.mockResolvedValue([orgType({ lastRefreshedUtc: '2026-03-04T05:06:00.000Z' })]);

    renderWithProvider(<UserOrgsPage />);

    expect(await screen.findByText(/every Entra type stops moving at once/i)).toBeInTheDocument();
  });
});
