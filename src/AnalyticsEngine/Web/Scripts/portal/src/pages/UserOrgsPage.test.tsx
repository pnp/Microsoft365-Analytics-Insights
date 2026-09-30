import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../test/renderWithProvider';
import UserOrgsPage from './UserOrgsPage';
import type { UserOrgImportJob, UserOrgType } from '../types/userOrgs';

const fetchOrgTypes = vi.fn();
const fetchOrgValues = vi.fn();
const updateOrgTypeMock = vi.fn();
const deleteOrgTypeMock = vi.fn();
const toastError = vi.fn();

vi.mock('../api/userOrgsApi', () => ({
  fetchOrgTypes: () => fetchOrgTypes(),
  fetchOrgValues: (...args: unknown[]) => fetchOrgValues(...args),
  fetchOrgMembers: vi.fn(),
  createOrgType: vi.fn(),
  updateOrgType: (...args: unknown[]) => updateOrgTypeMock(...args),
  deleteOrgType: (...args: unknown[]) => deleteOrgTypeMock(...args),
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

vi.mock('../components/toast', () => ({
  default: Object.assign(vi.fn(), {
    success: vi.fn(),
    error: (...args: unknown[]) => toastError(...args),
  }),
}));

function orgType(over: Partial<UserOrgType>): UserOrgType {
  return {
    id: 1,
    name: 'Cost Centre',
    source: 'entra',
    entraAttributeName: 'extensionAttribute1',
    isEnabled: true,
    revision: 1,
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
    startedUtc: '2026-03-05T09:30:01.000Z',
    finishedUtc: '2026-03-05T09:31:00.000Z',
    attempts: 1,
    rowsTotal: 3,
    rowsApplied: 0,
    rowsCleared: 0,
    rowsUnknownUpn: 0,
    rowsInvalid: 0,
    errorCode: null,
    errorMessage: 'The import failed.',
    changeLog: null,
  };
}

describe('UserOrgsPage', () => {
  beforeEach(() => {
    fetchOrgTypes.mockReset();
    fetchOrgValues.mockReset();
    updateOrgTypeMock.mockReset();
    deleteOrgTypeMock.mockReset();
    toastError.mockReset();
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

  it('shows what the file should look like when a CSV type is being created', async () => {
    // "A CSV file uploaded here" is a decision about what file to go and generate, taken before the
    // upload card exists - so the example has to be in the dialog, headed with the type's own name.
    fetchOrgTypes.mockResolvedValue([]);
    const user = userEvent.setup();
    renderWithProvider(<UserOrgsPage />);

    await user.click(await screen.findByRole('button', { name: 'New organisation type' }));
    // `hidden: true` because of a jsdom artefact, not the page: with no layout, tabster finds nothing
    // focusable in the dialog, never activates its modalizer, and marks the dialog itself
    // aria-hidden. The queries below go by label and text for the same reason.
    const dialog = await screen.findByRole('dialog', { hidden: true });
    expect(within(dialog).queryByLabelText('Example file')).not.toBeInTheDocument();
    // The organisation types ARE a report filter now; the hint must not say otherwise.
    expect(within(dialog).getByText(/as a property in the Copilot Adoption report's filter/)).toBeInTheDocument();

    await user.type(within(dialog).getByLabelText(/^Name/), 'Programme');
    await user.click(within(dialog).getByLabelText('A CSV file uploaded here'));

    expect(within(dialog).getByLabelText('Example file').textContent?.split('\n')[0]).toBe('UserPrincipalName,Programme');
    expect(within(dialog).getByText(/The header row is optional/)).toBeInTheDocument();
  });

  it('saves an edit against the revision the type had when the dialog opened', async () => {
    // A colleague may save the same type while this dialog is open. Sending back the revision it was
    // opened at is what lets the server refuse this save, rather than silently undo theirs - and the
    // values it held then, the number its discard warning showed, which an import can change without
    // moving the revision.
    fetchOrgTypes.mockResolvedValue([
      orgType({ id: 4, name: 'Programme', source: 'csv', entraAttributeName: null, revision: 7, assignedUserCount: 12 }),
    ]);
    updateOrgTypeMock.mockResolvedValue(orgType({ id: 4, name: 'Programmes', source: 'csv', entraAttributeName: null, revision: 8 }));
    const user = userEvent.setup();
    renderWithProvider(<UserOrgsPage />);

    await screen.findByRole('row', { name: /Programme/ });
    await user.click(within(rowFor('Programme')).getByRole('button', { name: 'Edit' }));
    // `hidden: true` for the jsdom artefact explained in the create test above.
    const dialog = await screen.findByRole('dialog', { hidden: true });
    const name = within(dialog).getByLabelText(/^Name/);
    await user.clear(name);
    await user.type(name, 'Programmes');
    await user.click(within(dialog).getByText('Save'));

    await waitFor(() =>
      expect(updateOrgTypeMock).toHaveBeenCalledWith(
        4,
        expect.objectContaining({ name: 'Programmes', expectedRevision: 7, confirmedDiscardCount: 12 }),
      ),
    );
  });

  it('deletes against the revision the page showed, and catches up when a colleague has saved the type since', async () => {
    // The confirmation names the type and its count as this page shows them. A colleague's save since
    // makes it a different type, so the delete is refused - and the list is brought up to date, so the
    // admin's next decision is made about the type as it now is.
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    fetchOrgTypes
      .mockResolvedValueOnce([orgType({ id: 4, name: 'Programme', source: 'csv', entraAttributeName: null, revision: 7 })])
      .mockResolvedValue([orgType({ id: 4, name: 'Initiative', source: 'csv', entraAttributeName: null, revision: 8 })]);
    deleteOrgTypeMock.mockRejectedValueOnce(
      Object.assign(new Error('server fallback'), { code: 'typeChangedBeforeDelete', values: {} }),
    );
    const user = userEvent.setup();
    try {
      renderWithProvider(<UserOrgsPage />);

      await screen.findByRole('row', { name: /Programme/ });
      await user.click(within(rowFor('Programme')).getByRole('button', { name: 'Delete' }));

      await waitFor(() => expect(deleteOrgTypeMock).toHaveBeenCalledWith(4, 7));
      await waitFor(() =>
        expect(toastError).toHaveBeenCalledWith(
          expect.stringMatching(/^Someone else changed this organisation type after the page loaded, so it was not deleted\./),
        ),
      );
      expect(await screen.findByRole('row', { name: /Initiative/ })).toBeInTheDocument();
    } finally {
      confirm.mockRestore();
    }
  });

  it('catches up when a save is refused because an import has filled the type since the dialog opened', async () => {
    fetchOrgTypes
      .mockResolvedValueOnce([orgType({ id: 4, name: 'Programme', source: 'csv', entraAttributeName: null, revision: 7 })])
      .mockResolvedValue([
        orgType({ id: 4, name: 'Programme', source: 'csv', entraAttributeName: null, revision: 7, assignedUserCount: 200 }),
      ]);
    updateOrgTypeMock.mockRejectedValueOnce(
      Object.assign(new Error('server fallback'), { code: 'discardExceedsConfirmed', values: { name: 'Programme', count: 200 } }),
    );
    const user = userEvent.setup();
    renderWithProvider(<UserOrgsPage />);

    await screen.findByRole('row', { name: /Programme/ });
    await user.click(within(rowFor('Programme')).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    await user.click(within(dialog).getByText('Save'));

    expect(await within(dialog).findByText(/now holds 200 values/)).toBeInTheDocument();
    await waitFor(() => expect(fetchOrgTypes).toHaveBeenCalledTimes(2));
  });

  it('opens the type as a colleague left it once their save has refused this one, so the retry goes through', async () => {
    // Refused as typeChangedElsewhere, the message says to close the dialog and open the type again. That
    // only helps if the list behind the dialog has caught up: otherwise the reopened dialog shows the old
    // values, sends the old revision, and every retry is refused until the page is reloaded.
    fetchOrgTypes
      .mockResolvedValueOnce([orgType({ id: 4, name: 'Programme', source: 'csv', entraAttributeName: null, revision: 7 })])
      .mockResolvedValue([orgType({ id: 4, name: 'Initiative', source: 'csv', entraAttributeName: null, revision: 8 })]);
    updateOrgTypeMock
      .mockRejectedValueOnce(
        Object.assign(new Error('Someone else changed it.'), { code: 'typeChangedElsewhere', values: { name: 'Programmes' } }),
      )
      .mockResolvedValue(orgType({ id: 4, name: 'Initiative', source: 'csv', entraAttributeName: null, revision: 9 }));
    const user = userEvent.setup();
    renderWithProvider(<UserOrgsPage />);

    await screen.findByRole('row', { name: /Programme/ });
    await user.click(within(rowFor('Programme')).getByRole('button', { name: 'Edit' }));
    const dialog = await screen.findByRole('dialog', { hidden: true });
    const name = within(dialog).getByLabelText(/^Name/);
    await user.clear(name);
    await user.type(name, 'Programmes');
    await user.click(within(dialog).getByText('Save'));

    expect(await within(dialog).findByText(/Someone else changed/)).toBeInTheDocument();
    expect(within(dialog).getByLabelText(/^Name/)).toHaveValue('Programmes');
    await waitFor(() => expect(fetchOrgTypes).toHaveBeenCalledTimes(2));

    await user.click(within(dialog).getByText('Cancel'));
    await user.click(within(await screen.findByRole('row', { name: /Initiative/ })).getByRole('button', { name: 'Edit' }));
    const reopened = await screen.findByRole('dialog', { hidden: true });
    await waitFor(() => expect(within(reopened).getByLabelText(/^Name/)).toHaveValue('Initiative'));
    await user.click(within(reopened).getByText('Save'));

    await waitFor(() =>
      expect(updateOrgTypeMock).toHaveBeenLastCalledWith(4, expect.objectContaining({ name: 'Initiative', expectedRevision: 8 })),
    );
  });

  it('keeps the file format one click away on the upload card', async () => {
    fetchOrgTypes.mockResolvedValue([
      orgType({ id: 2, name: 'Business Unit', source: 'csv', entraAttributeName: null }),
    ]);
    renderWithProvider(<UserOrgsPage />);

    const summary = await screen.findByText('What the file should look like');
    expect(summary.tagName).toBe('SUMMARY');
    expect(screen.getByLabelText('Example file').textContent?.split('\n')[0]).toBe('UserPrincipalName,Business Unit');
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

  it('says a type whose attribute holds lists cannot be imported, rather than that it is waiting', async () => {
    // No import will ever refresh it until the type points at another attribute, so "waiting for the
    // next user import" would tell the admin that nothing is wrong.
    fetchOrgTypes.mockResolvedValue([
      orgType({ id: 1, name: 'Skills', entraAttributeName: 'extension_0123456789abcdef0123456789abcdef_skills', attributeHoldsLists: true }),
    ]);

    renderWithProvider(<UserOrgsPage />);

    await screen.findByRole('columnheader', { name: 'Last refreshed' });
    expect(within(rowFor('Skills')).getByText(/Can't be imported: its attribute holds a list of values/)).toBeInTheDocument();
    expect(within(rowFor('Skills')).queryByText('Waiting for the next user import')).not.toBeInTheDocument();
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
