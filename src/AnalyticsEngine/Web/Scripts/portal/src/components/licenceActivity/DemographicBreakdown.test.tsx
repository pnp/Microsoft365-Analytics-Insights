import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import DemographicBreakdown from './DemographicBreakdown';
import { WORKLOADS } from '../../types/licenceActivity';
import type { LicenceActivityDemographic } from '../../types/licenceActivity';

function demo(over: Partial<LicenceActivityDemographic> = {}): LicenceActivityDemographic {
  return {
    id: Math.floor(Math.random() * 1e9),
    name: 'Engineering',
    assignedUsers: 100,
    workloads: WORKLOADS.map((w) => ({ workload: w.key, high: 5, moderate: 3, low: 2, zero: 4, unknown: 1 })),
    ...over,
  };
}

describe('DemographicBreakdown', () => {
  it('renders segment names, assigned counts and a column per workload, largest first', () => {
    renderWithProvider(
      <DemographicBreakdown
        title="By department"
        segmentLabel="Department"
        rows={[
          demo({ id: 1, name: 'Engineering', assignedUsers: 100 }),
          demo({ id: 2, name: 'Καλημέρα', assignedUsers: 50 }), // Unicode-safe segment name
        ]}
        truncated={false}
      />,
    );

    expect(screen.getByText('Engineering')).toBeInTheDocument();
    expect(screen.getByText('Καλημέρα')).toBeInTheDocument();
    expect(screen.getByText('100')).toBeInTheDocument();
    // A column header per workload (aggregate breakdown, not just a filter list).
    for (const w of WORKLOADS) {
      expect(screen.getByText(w.label)).toBeInTheDocument();
    }
    // Sorted largest-first.
    const rows = screen.getAllByRole('row');
    expect(rows[1]).toHaveTextContent('Engineering');
  });

  it('makes truncation explicit', () => {
    renderWithProvider(
      <DemographicBreakdown title="By country" segmentLabel="Country" rows={[demo()]} truncated />,
    );
    expect(screen.getByText(/truncated/i)).toBeInTheDocument();
  });

  it('caps at 50 rows and says so', () => {
    const many = Array.from({ length: 60 }, (_, i) => demo({ id: i + 1, name: `Dept ${i + 1}`, assignedUsers: 100 - i }));
    renderWithProvider(
      <DemographicBreakdown title="By department" segmentLabel="Department" rows={many} truncated={false} />,
    );
    expect(screen.getByText(/top 50/i)).toBeInTheDocument();
  });
});
