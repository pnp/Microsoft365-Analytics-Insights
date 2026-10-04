import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { fetchUserFilterDimensions, fetchUserFilterValues } from '../../api/userFilterApi';
import type { UserFilterDimension } from '../../types/userFilter';
import type { GlobalFilterDefinition } from '../../types/globalFilter';
import UserFilterBar from '../userFilter/UserFilterBar';
import { resetUserFilterDimensionsCache } from '../userFilter/useUserFilterDimensions';
import GlobalFilterEditor from './GlobalFilterEditor';

vi.mock('../../api/userFilterApi', () => ({
  fetchUserFilterDimensions: vi.fn(),
  fetchUserFilterValues: vi.fn(),
}));

const DIMENSIONS: UserFilterDimension[] = [
  { key: 'department', kind: 'entra', name: null, orgTypeId: null, distinctValues: 3, peopleWithValue: 90, supportsTextMatch: true, fixedValues: false },
  { key: 'managementChain', kind: 'entra', name: null, orgTypeId: null, distinctValues: 3, peopleWithValue: 90, supportsTextMatch: false, fixedValues: false },
];

function Harness({ initial, onChange }: { initial: GlobalFilterDefinition; onChange: (f: GlobalFilterDefinition) => void }) {
  const [filter, setFilter] = useState(initial);
  return (
    <GlobalFilterEditor
      filter={filter}
      dimensions={DIMENSIONS}
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
    values: dimension === 'department' ? [{ value: 'Sales', people: 40 }] : [],
    totalMatching: 1,
    truncated: false,
    peopleWithoutValue: 0,
  }));
});

describe('GlobalFilterEditor', () => {
  it('adds a condition whose only value is the viewer’s own', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithProvider(<Harness initial={{ clauses: [] }} onChange={onChange} />);

    await user.click(screen.getByRole('button', { name: 'Add condition' }));
    await user.click(await screen.findByRole('option', { name: /Department/ }));
    await waitFor(() => expect(fetchUserFilterValues).toHaveBeenCalledWith('department', '', 200, expect.anything()));

    await user.click(screen.getByRole('combobox', { name: 'Values' }));
    await user.click(await screen.findByRole('option', { name: 'Viewer’s own value' }));
    expect(screen.getByText(/to show each reader the people who share theirs/)).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Apply' }));

    expect(onChange).toHaveBeenLastCalledWith({
      clauses: [
        { join: 'and', dimension: 'department', operator: 'is', values: [], includeNotSet: false, viewerAttribute: 'department' },
      ],
    });
    expect(screen.getByText('Readers see only people where Department is the viewer’s own value.')).toBeVisible();
  });

  it('offers the viewer and the viewer’s manager for the management chain, one at a time', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithProvider(
      <Harness
        initial={{
          clauses: [
            { join: 'and', dimension: 'managementChain', operator: 'is', values: [], includeNotSet: false, viewerAttribute: 'userName' },
          ],
        }}
        onChange={onChange}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'Management chain includes the viewer' }));
    await user.click(screen.getByRole('combobox', { name: 'Values' }));
    await user.click(await screen.findByRole('option', { name: 'The viewer’s manager' }));
    await user.click(screen.getByRole('button', { name: 'Apply' }));

    // Picking the manager replaced the viewer: a condition compares with one of the viewer's values.
    expect(onChange).toHaveBeenLastCalledWith({
      clauses: [
        { join: 'and', dimension: 'managementChain', operator: 'is', values: [], includeNotSet: false, viewerAttribute: 'manager' },
      ],
    });
  });

  it('is never offered by a reader’s own filter', async () => {
    const user = userEvent.setup();
    renderWithProvider(<UserFilterBar filter={{ clauses: [] }} onChange={() => {}} />);

    await user.click(await screen.findByRole('button', { name: 'Add filter' }));
    await user.click(await screen.findByRole('option', { name: /Department/ }));
    await waitFor(() => expect(fetchUserFilterValues).toHaveBeenCalled());
    await user.click(screen.getByRole('combobox', { name: 'Values' }));

    expect(await screen.findByRole('option', { name: /Sales/ })).toBeVisible();
    expect(screen.queryByRole('option', { name: 'Viewer’s own value' })).not.toBeInTheDocument();
  });
});
