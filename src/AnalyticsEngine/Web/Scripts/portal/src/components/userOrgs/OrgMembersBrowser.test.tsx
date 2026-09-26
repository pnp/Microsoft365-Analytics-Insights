import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import OrgMembersBrowser from './OrgMembersBrowser';
import type { UserOrgMemberPage, UserOrgType, UserOrgValuePage } from '../../types/userOrgs';

const fetchOrgValues = vi.fn();
const fetchOrgMembers = vi.fn();

vi.mock('../../api/userOrgsApi', () => ({
  fetchOrgValues: (...args: unknown[]) => fetchOrgValues(...args),
  fetchOrgMembers: (...args: unknown[]) => fetchOrgMembers(...args),
}));

const GREEK = 'Καλημέρα κόσμε';

function orgType(over: Partial<UserOrgType>): UserOrgType {
  return {
    id: 1,
    name: 'Cost Centre',
    source: 'csv',
    entraAttributeName: null,
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

const TYPES = [orgType({ id: 1, name: 'Cost Centre' }), orgType({ id: 2, name: 'Business Unit' })];

function values(over: Partial<UserOrgValuePage> = {}): UserOrgValuePage {
  return {
    orgTypeId: 1,
    page: 1,
    pageSize: 25,
    total: 3,
    items: [
      { id: 11, name: 'Retail', memberCount: 1234 },
      { id: 12, name: GREEK, memberCount: 2 },
      { id: 13, name: 'Ops', memberCount: 0 },
    ],
    ...over,
  };
}

function members(over: Partial<UserOrgMemberPage> = {}): UserOrgMemberPage {
  return {
    orgTypeId: 1,
    valueId: 11,
    valueName: 'Retail',
    page: 1,
    pageSize: 50,
    total: 2,
    items: [
      {
        userId: 5,
        userPrincipalName: 'amy@contoso.com',
        department: GREEK,
        jobTitle: 'Account Manager',
        accountEnabled: false,
      },
      { userId: 6, userPrincipalName: 'bob@contoso.com', department: null, jobTitle: null, accountEnabled: true },
    ],
    ...over,
  };
}

function lastCall(mock: ReturnType<typeof vi.fn>): unknown[] {
  return mock.mock.calls[mock.mock.calls.length - 1];
}

describe('OrgMembersBrowser', () => {
  beforeEach(() => {
    fetchOrgValues.mockReset();
    fetchOrgMembers.mockReset();
  });

  it('lists the chosen type\u2019s organisations with how many users are in each', async () => {
    fetchOrgValues.mockResolvedValue(values());

    renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />);

    const table = await screen.findByRole('table', { name: 'Organisations' });
    expect(within(table).getByRole('button', { name: 'Retail' })).toBeInTheDocument();
    expect(within(table).getByRole('button', { name: GREEK })).toBeInTheDocument();
    // Sizes go through the reader's number format, not a bare toString().
    expect(within(table).getByText('1,234')).toBeInTheDocument();
    expect(fetchOrgValues).toHaveBeenCalledWith(1, { search: '', page: 1, pageSize: 25 }, expect.any(AbortSignal));
    expect(screen.getByText('Choose an organisation to see who is in it.')).toBeInTheDocument();
  });

  it('shows who is in an organisation once it is picked', async () => {
    fetchOrgValues.mockResolvedValue(values());
    fetchOrgMembers.mockResolvedValue(members());

    renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Retail' }));

    const table = await screen.findByRole('table', { name: 'Users in Retail' });
    expect(fetchOrgMembers).toHaveBeenCalledWith(1, 11, { search: '', page: 1, pageSize: 50 }, expect.any(AbortSignal));
    const amy = within(table).getByText('amy@contoso.com').closest('tr') as HTMLElement;
    expect(within(amy).getByText(GREEK)).toBeInTheDocument();
    expect(within(amy).getByText('Account Manager')).toBeInTheDocument();
    expect(within(amy).getByText('Account disabled')).toBeInTheDocument();

    // Missing directory details read as missing rather than as blank cells, and an enabled account
    // carries no badge.
    const bob = within(table).getByText('bob@contoso.com').closest('tr') as HTMLElement;
    expect(within(bob).getAllByText('\u2014')).toHaveLength(2);
    expect(within(bob).queryByText('Account disabled')).not.toBeInTheDocument();
  });

  it('searches organisations from the first page', async () => {
    fetchOrgValues.mockResolvedValue(values({ total: 60 }));

    renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Next' }));
    await waitFor(() => expect(lastCall(fetchOrgValues)[1]).toEqual({ search: '', page: 2, pageSize: 25 }));

    await userEvent.type(screen.getByRole('textbox', { name: 'Search organisations' }), '  retail {Enter}');

    await waitFor(() => expect(lastCall(fetchOrgValues)[1]).toEqual({ search: 'retail', page: 1, pageSize: 25 }));
  });

  it('pages through a large organisation and says where it is', async () => {
    fetchOrgValues.mockResolvedValue(values());
    fetchOrgMembers.mockResolvedValue(members({ total: 1234 }));

    renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Retail' }));
    expect(await screen.findByText('Showing 1\u201350 of 1,234')).toBeInTheDocument();

    fetchOrgMembers.mockResolvedValue(members({ total: 1234, page: 2 }));
    const memberTable = screen.getByRole('table', { name: 'Users in Retail' });
    const pager = memberTable.nextElementSibling as HTMLElement;
    await userEvent.click(within(pager).getByRole('button', { name: 'Next' }));

    await waitFor(() => expect(lastCall(fetchOrgMembers)[2]).toEqual({ search: '', page: 2, pageSize: 50 }));
    expect(await screen.findByText('Showing 51\u2013100 of 1,234')).toBeInTheDocument();
  });

  it('starts again when another type is chosen', async () => {
    fetchOrgValues.mockResolvedValue(values());
    fetchOrgMembers.mockResolvedValue(members());
    const onSelectType = vi.fn();

    const { rerender } = renderWithProvider(
      <OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={onSelectType} />,
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Retail' }));
    await screen.findByRole('table', { name: 'Users in Retail' });

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Organisation type' }), '2');
    expect(onSelectType).toHaveBeenCalledWith(2);

    fetchOrgValues.mockResolvedValue(values({ orgTypeId: 2, items: [{ id: 21, name: 'EMEA', memberCount: 4 }], total: 1 }));
    rerender(<OrgMembersBrowser types={TYPES} selectedTypeId={2} onSelectType={onSelectType} />);

    expect(await screen.findByRole('button', { name: 'EMEA' })).toBeInTheDocument();
    expect(lastCall(fetchOrgValues)[0]).toBe(2);
    expect(screen.queryByRole('table', { name: 'Users in Retail' })).not.toBeInTheDocument();
    expect(screen.getByText('Choose an organisation to see who is in it.')).toBeInTheDocument();
  });

  it('explains an empty organisation, an empty type and a failed load', async () => {
    fetchOrgValues.mockResolvedValue(values());
    fetchOrgMembers.mockResolvedValue(members({ total: 0, items: [], valueId: 13, valueName: 'Ops' }));

    const { unmount } = renderWithProvider(
      <OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />,
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Ops' }));
    expect(await screen.findByText('Nobody is in this organisation at the moment.')).toBeInTheDocument();
    unmount();

    fetchOrgValues.mockResolvedValue(values({ total: 0, items: [] }));
    const empty = renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />);
    expect(await screen.findByText('This type has no organisations yet.')).toBeInTheDocument();
    empty.unmount();

    // The server's own message is not shown: it is English, and this page is read in Spanish too.
    fetchOrgValues.mockRejectedValue(new Error('That organisation type no longer exists.'));
    renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />);
    expect(await screen.findByText(/This list could not be loaded/)).toBeInTheDocument();
    expect(screen.queryByText('That organisation type no longer exists.')).not.toBeInTheDocument();
  });

  it('forgets the chosen organisation when the type\u2019s source changes, because its organisations are gone', async () => {
    // Changing where a type's values come from discards every organisation it had. Keeping the
    // selection would ask for members of an organisation that no longer exists.
    fetchOrgValues.mockResolvedValue(values());
    fetchOrgMembers.mockResolvedValue(members());

    const { rerender } = renderWithProvider(
      <OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} refreshToken={1} />,
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Retail' }));
    await screen.findByRole('table', { name: 'Users in Retail' });

    // Renaming or refreshing keeps the selection...
    rerender(
      <OrgMembersBrowser
        types={[orgType({ id: 1, name: 'Renamed' }), TYPES[1]]}
        selectedTypeId={1}
        onSelectType={vi.fn()}
        refreshToken={2}
      />,
    );
    expect(await screen.findByRole('table', { name: 'Users in Retail' })).toBeInTheDocument();

    // ...switching the source does not.
    fetchOrgValues.mockResolvedValue(values({ total: 0, items: [] }));
    rerender(
      <OrgMembersBrowser
        types={[orgType({ id: 1, name: 'Renamed', source: 'entra', entraAttributeName: 'extensionAttribute1' }), TYPES[1]]}
        selectedTypeId={1}
        onSelectType={vi.fn()}
        refreshToken={3}
      />,
    );
    expect(await screen.findByText('Choose an organisation to see who is in it.')).toBeInTheDocument();
    expect(screen.queryByRole('table', { name: 'Users in Retail' })).not.toBeInTheDocument();
  });

  it('steps back to the last page when the list shrinks under it', async () => {
    // An import can remove people between one page and the next. An empty page 3 of a list that now
    // has one page must not show an empty table and "Showing 101\u201350 of 30".
    fetchOrgValues.mockResolvedValue(values());
    fetchOrgMembers
      .mockResolvedValueOnce(members({ total: 120 }))
      .mockResolvedValueOnce(members({ total: 30, page: 2, items: [] }))
      .mockResolvedValue(members({ total: 30 }));

    renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />);
    await userEvent.click(await screen.findByRole('button', { name: 'Retail' }));
    const table = await screen.findByRole('table', { name: 'Users in Retail' });
    await userEvent.click(within(table.nextElementSibling as HTMLElement).getByRole('button', { name: 'Next' }));

    expect(await screen.findByText('Showing 1\u201330 of 30')).toBeInTheDocument();
    expect(lastCall(fetchOrgMembers)[2]).toEqual({ search: '', page: 1, pageSize: 50 });
  });

  it('lets a failed load be tried again', async () => {
    fetchOrgValues.mockRejectedValueOnce(new Error('boom')).mockResolvedValue(values());

    renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Try again' }));

    expect(await screen.findByRole('button', { name: 'Retail' })).toBeInTheDocument();
    expect(screen.queryByText(/This list could not be loaded/)).not.toBeInTheDocument();
  });

  it('asks again when Search is pressed with nothing changed', async () => {
    // Otherwise pressing Search after a failure, with the same term, would silently do nothing.
    fetchOrgValues.mockResolvedValue(values());

    renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />);
    await screen.findByRole('button', { name: 'Retail' });
    const callsBefore = fetchOrgValues.mock.calls.length;

    await userEvent.click(screen.getAllByRole('button', { name: 'Search' })[0]);

    await waitFor(() => expect(fetchOrgValues.mock.calls.length).toBe(callsBefore + 1));
  });

  it('reads in Spanish, with the tenant\u2019s own names left as they are', async () => {
    // Five digits: Spanish does not group a four-digit number, so 1234 would prove nothing.
    fetchOrgValues.mockResolvedValue(values({ items: [{ id: 11, name: 'Retail', memberCount: 12345 }], total: 1 }));
    fetchOrgMembers.mockResolvedValue(members());

    renderWithProvider(<OrgMembersBrowser types={TYPES} selectedTypeId={1} onSelectType={vi.fn()} />, {
      language: 'es',
    });

    expect(await screen.findByText('12.345')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Retail' }));
    const table = await screen.findByRole('table', { name: 'Usuarios de Retail' });
    expect(within(table).getByText('Cuenta deshabilitada')).toBeInTheDocument();
    expect(within(table).getByText(GREEK)).toBeInTheDocument();
  });
});
