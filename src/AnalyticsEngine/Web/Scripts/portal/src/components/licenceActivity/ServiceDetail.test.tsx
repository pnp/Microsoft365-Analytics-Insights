import { describe, it, expect, vi } from 'vitest';
import { screen, fireEvent } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import ServiceDetail from './ServiceDetail';
import type {
  LicenceActivityDistribution,
  LicenceActivityOverview,
  WorkloadKey,
} from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';

function dist(workload: WorkloadKey, active: number, zero: number): LicenceActivityDistribution {
  return { workload, high: active, moderate: 0, low: 0, zero, unknown: 0 };
}

function overview(): LicenceActivityOverview {
  return {
    snapshotId: 'ov1',
    generatedUtc: '2026-05-20T10:00:00Z',
    expiresUtc: '2026-05-20T10:05:00Z',
    query: {
      from: '2026-04-22', to: '2026-05-19', departmentId: null, countryId: null, licenceTypeId: null,
      workload: 'teams', search: '', sort: 'upn', direction: 'asc', top: 10, page: 1, pageSize: 50,
    },
    distinctAssignedUsers: 150,
    allLicences: { assignedUsers: 150, adoptionScore: 50, workloads: WORKLOADS.map((w) => dist(w.key, 60, 40)) },
    licences: [
      { licenceTypeId: 10, name: 'Contoso E5', skuId: null, assignedUsers: 100, adoptionScore: 70, workloads: WORKLOADS.map((w) => dist(w.key, 80, 20)) },
      { licenceTypeId: 20, name: 'Contoso F3', skuId: null, assignedUsers: 50, adoptionScore: 30, workloads: WORKLOADS.map((w) => dist(w.key, 20, 30)) },
      { licenceTypeId: 30, name: 'Contoso unused', skuId: null, assignedUsers: 0, adoptionScore: null, workloads: [] },
    ],
    coverage: [],
    departments: [],
    countries: [],
    demographicsTruncated: false,
    messages: [],
  };
}

describe('ServiceDetail', () => {
  it('describes everyone holding a licence by default, with nothing to compare it with', () => {
    renderWithProvider(<ServiceDetail overview={overview()} scope="all" onScopeChange={vi.fn()} />);

    expect(screen.getByLabelText('Licence')).toHaveValue('all');
    expect(screen.queryByLabelText('Compare with')).not.toBeInTheDocument();
    expect(screen.getByText('hold at least one licence')).toBeInTheDocument();
    expect(screen.getByRole('img', { name: 'Adoption score 50 out of 100' })).toBeInTheDocument();
    expect(screen.getByText('Each service for Everyone holding a licence')).toBeInTheDocument();
    expect(screen.getAllByText('60 of 100 active (60%)')).toHaveLength(WORKLOADS.length);
    expect(screen.queryByText(/pts$/)).not.toBeInTheDocument();
  });

  it('reads a licence against everyone holding a licence, with its rank among the licences people hold', () => {
    renderWithProvider(<ServiceDetail overview={overview()} scope={10} onScopeChange={vi.fn()} />);

    expect(screen.getByLabelText('Compare with')).toHaveValue('all');
    expect(screen.getByRole('img', { name: 'Adoption score 70 out of 100; everyone holding a licence scores 50' })).toBeInTheDocument();
    expect(screen.getByText(/Everyone holding a licence: 50 · ranks 1 of 2 licences/)).toBeInTheDocument();
    // 80% active against 60%: twenty points above, top of the two licences anybody holds.
    expect(screen.getAllByText('Everyone holding a licence: 60% active')).toHaveLength(WORKLOADS.length);
    expect(screen.getAllByText('+20 pts')).toHaveLength(WORKLOADS.length);
    expect(screen.getByText('Ranks 1 of 2 licences for Teams')).toBeInTheDocument();
  });

  it('compares with another licence instead, and never offers the licence itself', () => {
    renderWithProvider(<ServiceDetail overview={overview()} scope={20} onScopeChange={vi.fn()} />);

    const compare = screen.getByLabelText('Compare with');
    expect(Array.from((compare as HTMLSelectElement).options).map((o) => o.value)).toEqual(['all', '10']);
    fireEvent.change(compare, { target: { value: '10' } });

    // F3 (40% active) against E5 (80%).
    expect(screen.getAllByText('Contoso E5: 80% active')).toHaveLength(WORKLOADS.length);
    expect(screen.getAllByText('\u221240 pts')).toHaveLength(WORKLOADS.length);
    expect(screen.getByText('Ranks 2 of 2 licences for Outlook')).toBeInTheDocument();
  });

  it('lists licences by name in the picker, leaving out the ones nobody holds', () => {
    const onScopeChange = vi.fn();
    renderWithProvider(<ServiceDetail overview={overview()} scope="all" onScopeChange={onScopeChange} />);

    const picker = screen.getByLabelText('Licence') as HTMLSelectElement;
    expect(Array.from(picker.options).map((o) => o.text)).toEqual([
      'Everyone holding a licence (150)',
      'Contoso E5 (100)',
      'Contoso F3 (50)',
    ]);
    fireEvent.change(picker, { target: { value: '20' } });
    expect(onScopeChange).toHaveBeenCalledWith(20);
  });

  it('opens the people for the same scope, only when the reader may see them', () => {
    const onShowPeople = vi.fn();
    const { unmount } = renderWithProvider(
      <ServiceDetail overview={overview()} scope={10} onScopeChange={vi.fn()} onShowPeople={onShowPeople} />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'See who holds it' }));
    expect(onShowPeople).toHaveBeenCalledTimes(1);
    unmount();

    renderWithProvider(<ServiceDetail overview={overview()} scope="all" onScopeChange={vi.fn()} />);
    expect(screen.queryByRole('button', { name: /See / })).not.toBeInTheDocument();
  });
});
