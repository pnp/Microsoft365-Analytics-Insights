import { describe, it, expect, vi } from 'vitest';
import { screen, fireEvent, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import LicenceComparisonTable, { DEFAULT_LICENCE_SORT, sortLicences } from './LicenceComparisonTable';
import type {
  LicenceActivityAllLicences,
  LicenceActivityDistribution,
  LicenceActivitySku,
  WorkloadKey,
} from '../../types/licenceActivity';

function dist(workload: WorkloadKey, active: number, zero: number, unknown = 0): LicenceActivityDistribution {
  return { workload, high: active, moderate: 0, low: 0, zero, unknown };
}

const licences: LicenceActivitySku[] = [
  {
    licenceTypeId: 10, name: 'E5', skuId: 'ENTERPRISEPREMIUM', assignedUsers: 100, adoptionScore: 40,
    workloads: [dist('teams', 50, 50), dist('outlook', 90, 10)],
  },
  {
    licenceTypeId: 20, name: 'F3', skuId: 'DESKLESSPACK', assignedUsers: 50, adoptionScore: 75.5,
    workloads: [dist('teams', 45, 5), dist('outlook', 10, 40)],
  },
];

const everyone: LicenceActivityAllLicences = {
  assignedUsers: 120,
  adoptionScore: 52,
  workloads: [dist('teams', 80, 40), dist('outlook', 90, 30)],
};

/** The licence names in the order the table shows them (each row's selection button, minus the baseline). */
function shownOrder(): string[] {
  return screen
    .getAllByRole('row')
    .slice(1)
    .map((row) => within(row).getAllByRole('button')[0].textContent ?? '')
    .filter((text) => !text.startsWith('Everyone holding a licence'));
}

describe('LicenceComparisonTable', () => {
  it('leads with everyone holding a licence, then each licence with its people, score and service shares', () => {
    renderWithProvider(<LicenceComparisonTable licences={licences} allLicences={everyone} selected="all" onSelect={vi.fn()} />);

    const rows = screen.getAllByRole('row');
    expect(rows).toHaveLength(4); // header + baseline + two licences
    const baseline = rows[1];
    expect(within(baseline).getByRole('button', { name: /Everyone holding a licence/ })).toHaveAttribute('aria-pressed', 'true');
    expect(within(baseline).getByText('120')).toBeInTheDocument();
    expect(within(baseline).getByText('52')).toBeInTheDocument();
    // 80 of 120 measured holders active in Teams.
    expect(within(baseline).getByText('66.7%')).toBeInTheDocument();

    const f3 = rows.find((r) => within(r).queryByText('F3'))!;
    expect(within(f3).getByText('75.5')).toBeInTheDocument();
    expect(within(f3).getByText('90%')).toBeInTheDocument(); // 45 of 50 in Teams
  });

  it('sorts by any column: name A-Z first, every figure highest first, and a second click reverses it', () => {
    renderWithProvider(<LicenceComparisonTable licences={licences} allLicences={everyone} selected="all" onSelect={vi.fn()} />);

    // Default: most people first.
    expect(shownOrder()).toEqual(['E5ENTERPRISEPREMIUM', 'F3DESKLESSPACK']);

    fireEvent.click(screen.getByRole('button', { name: /^Adoption score/ }));
    expect(shownOrder()).toEqual(['F3DESKLESSPACK', 'E5ENTERPRISEPREMIUM']);
    expect(screen.getByRole('columnheader', { name: /Adoption score/ })).toHaveAttribute('aria-sort', 'descending');

    // Outlook: E5 90% active, F3 20%.
    fireEvent.click(screen.getByRole('button', { name: /^Outlook/ }));
    expect(shownOrder()).toEqual(['E5ENTERPRISEPREMIUM', 'F3DESKLESSPACK']);
    fireEvent.click(screen.getByRole('button', { name: /^Outlook/ }));
    expect(shownOrder()).toEqual(['F3DESKLESSPACK', 'E5ENTERPRISEPREMIUM']);
    expect(screen.getByRole('columnheader', { name: /Outlook/ })).toHaveAttribute('aria-sort', 'ascending');

    fireEvent.click(screen.getByRole('button', { name: /^Licence/ }));
    expect(shownOrder()).toEqual(['E5ENTERPRISEPREMIUM', 'F3DESKLESSPACK']);
  });

  it('always sorts licences that could not be measured last, whichever the direction', () => {
    const withUnknown: LicenceActivitySku[] = [
      ...licences,
      { licenceTypeId: 30, name: 'Kiosk', skuId: null, assignedUsers: 70, adoptionScore: null, workloads: [dist('teams', 0, 0, 70)] },
    ];
    const names = (key: 'score' | 'teams', direction: 'asc' | 'desc') =>
      sortLicences(withUnknown, { key, direction }).map((l) => l.name);
    expect(names('score', 'desc')).toEqual(['F3', 'E5', 'Kiosk']);
    expect(names('score', 'asc')).toEqual(['E5', 'F3', 'Kiosk']);
    expect(names('teams', 'desc')).toEqual(['F3', 'E5', 'Kiosk']);
    expect(names('teams', 'asc')).toEqual(['E5', 'F3', 'Kiosk']);
    expect(sortLicences(withUnknown, DEFAULT_LICENCE_SORT).map((l) => l.name)).toEqual(['E5', 'Kiosk', 'F3']);
  });

  it('hides licences nobody holds by default, with a switch to show them', () => {
    const withEmpty = [...licences, { licenceTypeId: 40, name: 'VISIO ONLINE PLAN 2', skuId: 'VISIOCLIENT', assignedUsers: 0, workloads: [] }];
    renderWithProvider(<LicenceComparisonTable licences={withEmpty} allLicences={everyone} selected="all" onSelect={vi.fn()} />);

    expect(screen.queryByText('VISIO ONLINE PLAN 2')).not.toBeInTheDocument();
    const toggle = screen.getByRole('switch', { name: 'Hide licences nobody holds (1)' });
    expect(toggle).toBeChecked();
    fireEvent.click(toggle);
    expect(screen.getByText('VISIO ONLINE PLAN 2')).toBeInTheDocument();
  });

  it('keeps a selected licence on screen even when it would be hidden', () => {
    const withEmpty = [...licences, { licenceTypeId: 40, name: 'VISIO ONLINE PLAN 2', skuId: null, assignedUsers: 0, workloads: [] }];
    renderWithProvider(<LicenceComparisonTable licences={withEmpty} allLicences={everyone} selected={40} onSelect={vi.fn()} />);
    expect(screen.getByRole('button', { name: /VISIO ONLINE PLAN 2/ })).toHaveAttribute('aria-pressed', 'true');
  });

  it('selects from anywhere in the row, and the button still fires exactly once', () => {
    const onSelect = vi.fn();
    renderWithProvider(<LicenceComparisonTable licences={licences} allLicences={everyone} selected="all" onSelect={onSelect} />);

    fireEvent.click(screen.getByText('50'));
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(20);

    onSelect.mockClear();
    fireEvent.click(screen.getByRole('button', { name: /E5/ }));
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(10);

    onSelect.mockClear();
    fireEvent.click(screen.getByRole('button', { name: /Everyone holding a licence/ }));
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith('all');
  });

  it('keeps native table-row semantics, with a concise, pressed selection button', () => {
    renderWithProvider(<LicenceComparisonTable licences={licences} allLicences={everyone} selected={10} onSelect={vi.fn()} />);

    screen.getAllByRole('row').forEach((r) => expect(r).not.toHaveAttribute('role'));
    const e5 = screen.getByRole('button', { name: /E5/ });
    expect(e5).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: /F3/ })).toHaveAttribute('aria-pressed', 'false');
    expect(e5).not.toHaveTextContent('High');
  });

  it('selects with the keyboard via the real button', async () => {
    const user = userEvent.setup();
    const onSelect = vi.fn();
    renderWithProvider(<LicenceComparisonTable licences={licences} allLicences={everyone} selected="all" onSelect={onSelect} />);
    screen.getByRole('button', { name: /F3/ }).focus();
    await user.keyboard('{Enter}');
    expect(onSelect).toHaveBeenCalledWith(20);
  });

  it('renders a non-Latin (Greek) licence name without corruption', () => {
    renderWithProvider(
      <LicenceComparisonTable
        licences={[{ licenceTypeId: 30, name: 'Άδεια Καλημέρα', skuId: 'GREEK_SKU', assignedUsers: 5, workloads: [] }]}
        allLicences={null}
        selected="all"
        onSelect={vi.fn()}
      />,
    );
    expect(screen.getByText('Άδεια Καλημέρα')).toBeInTheDocument();
  });

  it('scales to many licences: filters by name or code, and stays selectable', () => {
    const many: LicenceActivitySku[] = Array.from({ length: 12 }, (_, i) => ({
      licenceTypeId: i + 1,
      name: `SKU ${i + 1}`,
      skuId: `PART${i + 1}`,
      assignedUsers: (i + 1) * 10,
      workloads: [],
    }));
    many.push({ licenceTypeId: 99, name: 'Άδεια σπάνια', skuId: 'RARE', assignedUsers: 1, workloads: [] });

    const onSelect = vi.fn();
    renderWithProvider(<LicenceComparisonTable licences={many} allLicences={null} selected="all" onSelect={onSelect} />);

    expect(shownOrder()[0]).toContain('SKU 12');
    fireEvent.change(screen.getByLabelText('Filter licences'), { target: { value: 'σπάνια' } });
    expect(screen.getByText('Άδεια σπάνια')).toBeInTheDocument();
    expect(screen.queryByText('SKU 12')).not.toBeInTheDocument();
    fireEvent.click(screen.getByText('Άδεια σπάνια'));
    expect(onSelect).toHaveBeenCalledWith(99);

    fireEvent.change(screen.getByLabelText('Filter licences'), { target: { value: 'nothing like this' } });
    expect(screen.getByText('No licences match \u201cnothing like this\u201d.')).toBeInTheDocument();
  });
});
