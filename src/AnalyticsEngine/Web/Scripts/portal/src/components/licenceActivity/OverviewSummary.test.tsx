import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import OverviewSummary from './OverviewSummary';
import type { LicenceActivitySku } from '../../types/licenceActivity';

const E5: LicenceActivitySku = {
  licenceTypeId: 10,
  name: 'E5',
  skuId: 'ENTERPRISEPREMIUM',
  assignedUsers: 1234,
  workloads: [],
};

describe('OverviewSummary', () => {
  it('shows the headline figures the report already computes', () => {
    renderWithProvider(<OverviewSummary distinctAssignedUsers={9876} licenceCount={4} selectedLicence={E5} />);

    expect(screen.getByText('People with a licence')).toBeInTheDocument();
    expect(screen.getByText('9,876')).toBeInTheDocument();
    expect(screen.getByText('Licence types')).toBeInTheDocument();
    expect(screen.getByText('4')).toBeInTheDocument();

    // The selected licence and its own assigned count.
    expect(screen.getByText('E5')).toBeInTheDocument();
    expect(screen.getByText(/1,234 people hold it/)).toBeInTheDocument();
  });

  it('prompts to choose a licence when none is selected', () => {
    renderWithProvider(<OverviewSummary distinctAssignedUsers={10} licenceCount={1} selectedLicence={null} />);
    expect(screen.getByText('None chosen')).toBeInTheDocument();
    expect(screen.getByText(/Choose a licence below/)).toBeInTheDocument();
  });

  it('renders a non-Latin (Greek) licence name without corruption', () => {
    renderWithProvider(
      <OverviewSummary
        distinctAssignedUsers={5}
        licenceCount={1}
        selectedLicence={{ licenceTypeId: 30, name: 'Άδεια Καλημέρα', skuId: 'GREEK', assignedUsers: 5, workloads: [] }}
      />,
    );
    expect(screen.getByText('Άδεια Καλημέρα')).toBeInTheDocument();
  });
});
