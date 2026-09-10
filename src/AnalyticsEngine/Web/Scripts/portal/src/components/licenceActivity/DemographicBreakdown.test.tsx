import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import DemographicBreakdown from './DemographicBreakdown';
import { WORKLOADS } from '../../types/licenceActivity';
import type { LicenceActivityDemographic } from '../../types/licenceActivity';
import { BAND_DESCRIPTIONS, COPILOT_COVERAGE_NOTE } from './bands';

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
    expect(screen.getByText(/not the full list/i)).toBeInTheDocument();
  });

  it.each(['Department', 'Country'])('explains Unknown beside the %s chart without changing the counts', async (segmentLabel) => {
    const user = userEvent.setup();
    renderWithProvider(
      <DemographicBreakdown
        title={`By ${segmentLabel.toLowerCase()}`}
        segmentLabel={segmentLabel}
        rows={[demo()]}
        truncated={false}
      />,
    );

    expect(screen.getByText(/People with any imported licence, not necessarily a licence for every service/)).toBeVisible();
    expect(screen.getByRole('note')).toHaveTextContent('Unknown means insufficient data, not no activity.');
    expect(screen.getByRole('note')).toHaveTextContent('No activity means complete reporting data shows no usage.');
    expect(screen.getByText(COPILOT_COVERAGE_NOTE)).not.toBeVisible();

    const summary = screen.getByText('Why is activity Unknown?');
    const details = summary.closest('details');
    expect(details).not.toHaveAttribute('open');
    await user.tab();
    expect(summary).toHaveFocus();
    await user.click(summary);
    expect(details).toHaveAttribute('open');
    expect(screen.getByText(BAND_DESCRIPTIONS.unknown)).toBeVisible();
    expect(screen.getByText(COPILOT_COVERAGE_NOTE)).toBeVisible();
    expect(screen.getByText(/finding no events does not prove that the person was inactive/)).toBeVisible();
    expect(screen.getByText(/Under Where these figures come from, select Show data sources/)).toBeVisible();
    expect(screen.getAllByRole('img')).toHaveLength(WORKLOADS.length);
    for (const bar of screen.getAllByRole('img')) {
      expect(bar).toHaveAccessibleName('High 5, Moderate 3, Low 2, No activity 4, Unknown 1');
    }
    expect(screen.getAllByTitle(`Unknown: 1. ${BAND_DESCRIPTIONS.unknown}`)).toHaveLength(WORKLOADS.length);
    expect(screen.getAllByTitle(`No activity: 4. ${BAND_DESCRIPTIONS.zero}`)).toHaveLength(WORKLOADS.length);
    await user.click(summary);
    expect(details).not.toHaveAttribute('open');
    expect(screen.getByText(COPILOT_COVERAGE_NOTE)).not.toBeVisible();
  });

  it('caps at 50 rows and says so', () => {
    const many = Array.from({ length: 60 }, (_, i) => demo({ id: i + 1, name: `Dept ${i + 1}`, assignedUsers: 100 - i }));
    renderWithProvider(
      <DemographicBreakdown title="By department" segmentLabel="Department" rows={many} truncated={false} />,
    );
    expect(screen.getByText(/Showing only the 50 largest/i)).toBeInTheDocument();
  });
});
