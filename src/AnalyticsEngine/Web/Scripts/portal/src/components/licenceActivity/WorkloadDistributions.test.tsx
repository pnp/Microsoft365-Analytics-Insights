import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import WorkloadDistributions from './WorkloadDistributions';
import type { LicenceActivityDistribution, WorkloadKey } from '../../types/licenceActivity';

function dist(workload: WorkloadKey, over: Partial<LicenceActivityDistribution> = {}): LicenceActivityDistribution {
  return { workload, high: 0, moderate: 0, low: 0, zero: 0, unknown: 0, ...over };
}

describe('WorkloadDistributions', () => {
  it('summarises active over the MEASURED population and shows a measured zero band', () => {
    renderWithProvider(<WorkloadDistributions workloads={[dist('teams', { high: 5, zero: 3 })]} />);
    // measured = 5 + 3 = 8, active = 5 -> 62.5%
    expect(screen.getByText(/5 of 8 active/)).toBeInTheDocument();
    expect(screen.getByText(/62\.5%/)).toBeInTheDocument();
    // "No activity" (measured zero) is a real, rendered band - not merged with Unknown.
    expect(screen.getByText('No activity 3')).toBeInTheDocument();
  });

  it('renders a fully-unmeasured workload as "Not measured", never "0 active"', () => {
    renderWithProvider(<WorkloadDistributions workloads={[dist('copilot', { unknown: 10 })]} />);
    expect(screen.getByText('Not measured')).toBeInTheDocument();
    expect(screen.getByText('Unknown 10')).toBeInTheDocument();
    expect(screen.queryByText(/of .* active/)).not.toBeInTheDocument();
  });
});
