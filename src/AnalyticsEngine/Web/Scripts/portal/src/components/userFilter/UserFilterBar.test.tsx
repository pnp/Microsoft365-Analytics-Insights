import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { renderWithProvider } from '../../test/renderWithProvider';
import type { UserFilter, UserFilterClause, UserFilterDimension } from '../../types/userFilter';
import { fetchUserFilterDimensions, fetchUserFilterValues } from '../../api/userFilterApi';
import UserFilterBar from './UserFilterBar';
import { resetUserFilterDimensionsCache } from './useUserFilterDimensions';

vi.mock('../../api/userFilterApi', () => ({
  fetchUserFilterDimensions: vi.fn(),
  fetchUserFilterValues: vi.fn(),
}));

const DIMENSIONS: UserFilterDimension[] = [
  { key: 'department', kind: 'entra', name: null, orgTypeId: null, distinctValues: 3, peopleWithValue: 90, supportsTextMatch: true, fixedValues: false },
  { key: 'country', kind: 'entra', name: null, orgTypeId: null, distinctValues: 2, peopleWithValue: 95, supportsTextMatch: true, fixedValues: false },
  { key: 'accountStatus', kind: 'entra', name: null, orgTypeId: null, distinctValues: 2, peopleWithValue: 100, supportsTextMatch: false, fixedValues: true },
  { key: 'org:4', kind: 'custom', name: 'Cost centre', orgTypeId: 4, distinctValues: 12, peopleWithValue: 80, supportsTextMatch: true, fixedValues: false },
];

function clause(dimension: string, values: string[], overrides: Partial<UserFilterClause> = {}): UserFilterClause {
  return { join: 'and', dimension, operator: 'is', values, includeNotSet: false, ...overrides };
}

/** The bar with its filter held in state, as a page holds it, reporting every change. */
function Harness({ initial, onChange }: { initial: UserFilter; onChange: (f: UserFilter) => void }) {
  const [filter, setFilter] = useState(initial);
  return (
    <UserFilterBar
      filter={filter}
      onChange={(next) => {
        setFilter(next);
        onChange(next);
      }}
    />
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  resetUserFilterDimensionsCache();
  vi.mocked(fetchUserFilterDimensions).mockResolvedValue({ people: 100, loadedUtc: '2026-09-01T00:00:00Z', dimensions: DIMENSIONS });
  vi.mocked(fetchUserFilterValues).mockImplementation(async (dimension: string) => ({
    dimension,
    values:
      dimension === 'department'
        ? [
            { value: 'Sales', people: 40 },
            { value: 'Marketing', people: 30 },
            { value: 'Καλημέρα κόσμε', people: 20 },
          ]
        : [],
    totalMatching: 3,
    truncated: false,
    peopleWithoutValue: 10,
  }));
});

describe('UserFilterBar', () => {
  it('starts as a quick, one-condition filter: no conditions, and one obvious way to add one', async () => {
    renderWithProvider(<Harness initial={{ clauses: [] }} onChange={() => {}} />);

    expect(screen.getByText('Showing everyone')).toBeVisible();
    expect(await screen.findByRole('button', { name: 'Add filter' })).toBeEnabled();
    expect(screen.queryByRole('button', { name: /How this condition combines/ })).not.toBeInTheDocument();
    expect(screen.queryByText('Clear all')).not.toBeInTheDocument();
  });

  it('shows each condition as a pill, with the operator as a symbol and the values spelled out', async () => {
    renderWithProvider(
      <Harness
        initial={{ clauses: [clause('department', ['Sales', 'Marketing', 'Finance']), clause('org:4', ['CC-100'], { operator: 'isNot' })] }}
        onChange={() => {}}
      />,
    );

    const department = await screen.findByRole('button', { name: 'Department is Sales, Marketing or Finance' });
    expect(department.textContent).toContain('Department');
    expect(department.textContent).toContain('=');
    expect(department.textContent).toContain('Sales, Marketing');
    expect(department.textContent).toContain('+1 more');

    const costCentre = screen.getByRole('button', { name: 'Cost centre is not CC-100' });
    expect(costCentre.textContent).toContain('≠');
  });

  it('marks a custom organisation apart from a standard Entra ID attribute', async () => {
    renderWithProvider(
      <Harness initial={{ clauses: [clause('department', ['Sales']), clause('org:4', ['CC-100'])] }} onChange={() => {}} />,
    );

    const department = await screen.findByRole('button', { name: 'Department is Sales' });
    const costCentre = screen.getByRole('button', { name: 'Cost centre is CC-100' });
    expect(within(department).getByRole('img', { name: 'Standard Entra ID attribute' })).toBeInTheDocument();
    expect(within(costCentre).getByRole('img', { name: 'Custom organisation defined by an administrator' })).toBeInTheDocument();
  });

  it('removes a condition from its pill', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithProvider(
      <Harness initial={{ clauses: [clause('department', ['Sales']), clause('country', ['Ireland'])] }} onChange={onChange} />,
    );

    await user.click(await screen.findByRole('button', { name: 'Department is Sales Remove' }));

    expect(onChange).toHaveBeenLastCalledWith({ clauses: [clause('country', ['Ireland'])] });
  });

  it('reads a filter of several conditions back in plain words, groups and all', async () => {
    renderWithProvider(
      <Harness
        initial={{
          clauses: [clause('department', ['Sales']), clause('country', ['Ireland']), clause('org:4', ['CC-100'], { join: 'or' })],
        }}
        onChange={() => {}}
      />,
    );

    expect(
      await screen.findByText(
        'Showing people where (Department is Sales and Country or region is Ireland) or (Cost centre is CC-100).',
      ),
    ).toBeVisible();
    expect(screen.getByRole('button', { name: /How this condition combines with the one before it: AND/ })).toBeVisible();
    expect(screen.getByRole('button', { name: /How this condition combines with the one before it: OR/ })).toBeVisible();
  });

  it('switches a connector between AND and OR', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithProvider(
      <Harness initial={{ clauses: [clause('department', ['Sales']), clause('country', ['Ireland'])] }} onChange={onChange} />,
    );

    await user.click(await screen.findByRole('button', { name: /How this condition combines with the one before it: AND/ }));
    await user.click(await screen.findByRole('menuitemradio', { name: /OR: people can match either condition/ }));

    expect(onChange).toHaveBeenLastCalledWith({
      clauses: [clause('department', ['Sales']), clause('country', ['Ireland'], { join: 'or' })],
    });
  });

  it('adds a condition from the editor: property, then values, then Apply', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithProvider(<Harness initial={{ clauses: [] }} onChange={onChange} />);

    await user.click(await screen.findByRole('button', { name: 'Add filter' }));

    // The property list opens straight away - the first condition should take two picks, not four.
    await user.click(await screen.findByRole('option', { name: /Department/ }));

    await waitFor(() => expect(fetchUserFilterValues).toHaveBeenCalledWith('department', '', 200, expect.anything()));
    await user.click(screen.getByRole('combobox', { name: 'Values' }));
    await user.click(await screen.findByRole('option', { name: /Καλημέρα κόσμε/ }));

    await user.click(screen.getByRole('button', { name: 'Apply' }));

    expect(onChange).toHaveBeenLastCalledWith({ clauses: [clause('department', ['Καλημέρα κόσμε'])] });
  });

  it('offers "(not set)" for the people with no value at all', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithProvider(<Harness initial={{ clauses: [] }} onChange={onChange} />);

    await user.click(await screen.findByRole('button', { name: 'Add filter' }));
    await user.click(await screen.findByRole('option', { name: /Department/ }));
    await user.click(screen.getByRole('combobox', { name: 'Values' }));
    await user.click(await screen.findByRole('option', { name: /\(not set\)/ }));
    await user.click(screen.getByRole('button', { name: 'Apply' }));

    expect(onChange).toHaveBeenLastCalledWith({ clauses: [clause('department', [], { includeNotSet: true })] });
  });

  it('refuses to apply a condition with nothing chosen, and says why', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithProvider(<Harness initial={{ clauses: [] }} onChange={onChange} />);

    await user.click(await screen.findByRole('button', { name: 'Add filter' }));
    await user.click(await screen.findByRole('option', { name: /Department/ }));
    await user.click(screen.getByRole('button', { name: 'Apply' }));

    expect(await screen.findByText('Choose at least one value.')).toBeVisible();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('leaves the filter untouched when an edit is cancelled', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithProvider(<Harness initial={{ clauses: [clause('department', ['Sales'])] }} onChange={onChange} />);

    await user.click(await screen.findByRole('button', { name: 'Department is Sales' }));
    await user.click(await screen.findByRole('button', { name: 'Cancel' }));

    expect(onChange).not.toHaveBeenCalled();
    expect(await screen.findByRole('button', { name: 'Department is Sales' })).toBeVisible();
  });

  it('offers "contains" only where text matching makes sense', async () => {
    const user = userEvent.setup();
    renderWithProvider(<Harness initial={{ clauses: [clause('accountStatus', ['disabled'])] }} onChange={() => {}} />);

    await user.click(await screen.findByRole('button', { name: 'Account status is Disabled' }));
    await user.click(screen.getByRole('combobox', { name: 'Operator' }));

    expect(await screen.findByRole('option', { name: 'is not (≠)' })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'contains' })).not.toBeInTheDocument();
  });

  it('keeps working, and says so, when the attribute list cannot be loaded', async () => {
    vi.mocked(fetchUserFilterDimensions).mockRejectedValue(new Error('boom'));
    renderWithProvider(<Harness initial={{ clauses: [clause('department', ['Sales'])] }} onChange={() => {}} />);

    expect(await screen.findByText(/The attributes to filter on could not be loaded/)).toBeVisible();
    // The condition already applied is still shown - and still removable.
    expect(screen.getByRole('button', { name: 'Department is Sales' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Add filter' })).toBeDisabled();
  });

  it('is chrome: the whole bar is left off paper', async () => {
    renderWithProvider(<Harness initial={{ clauses: [clause('department', ['Sales'])] }} onChange={() => {}} />);

    const pill = await screen.findByRole('button', { name: 'Department is Sales' });
    expect(pill.closest('[data-print="hide"]')).not.toBeNull();
  });
});
