import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import OverviewSummary from './OverviewSummary';
import type { LicenceActivityAllLicences, LicenceActivitySku } from '../../types/licenceActivity';

const sku = (licenceTypeId: number, assignedUsers: number): LicenceActivitySku => ({
  licenceTypeId,
  name: `Contoso licence ${licenceTypeId}`,
  skuId: null,
  assignedUsers,
  workloads: [],
});

const everyone: LicenceActivityAllLicences = { assignedUsers: 9876, adoptionScore: 61.5, workloads: [] };

describe('OverviewSummary', () => {
  it('shows the headline figures the report already computes', () => {
    renderWithProvider(
      <OverviewSummary distinctAssignedUsers={9876} licences={[sku(1, 10), sku(2, 20), sku(3, 0)]} allLicences={everyone} />,
    );

    expect(screen.getByText('People with a licence')).toBeInTheDocument();
    expect(screen.getByText('9,876')).toBeInTheDocument();

    // Licences somebody holds are counted; the ones nobody holds are only mentioned.
    expect(screen.getByText('Licences held')).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
    expect(screen.getByText('plus 1 that nobody holds')).toBeInTheDocument();

    // The adoption score everyone holding a licence is compared with.
    expect(screen.getByText('Adoption score')).toBeInTheDocument();
    expect(screen.getByText('61.5')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'Adoption score 61.5 out of 100' })).toBeInTheDocument();
  });

  it('leaves the score out when the server sent no all-licence figures', () => {
    renderWithProvider(<OverviewSummary distinctAssignedUsers={10} licences={[sku(1, 10)]} allLicences={null} />);
    expect(screen.queryByText('Adoption score')).not.toBeInTheDocument();
    expect(screen.getByText('assigned, each measured separately')).toBeInTheDocument();
  });

  it('shows an unmeasured score as a dash, never as zero', () => {
    renderWithProvider(
      <OverviewSummary
        distinctAssignedUsers={10}
        licences={[sku(1, 10)]}
        allLicences={{ assignedUsers: 10, adoptionScore: null, workloads: [] }}
      />,
    );
    expect(screen.getByTitle('Not measured')).toHaveTextContent('\u2014');
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });
});
