import { describe, it, expect } from 'vitest';
import { screen, fireEvent } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import UsersTable from './UsersTable';
import { WORKLOADS } from '../../types/licenceActivity';
import type { LicenceActivityEvidence, LicenceActivityUser } from '../../types/licenceActivity';

function evidence(over: Partial<LicenceActivityEvidence> = {}): LicenceActivityEvidence {
  return {
    workload: 'teams',
    status: 'known',
    band: 'high',
    source: 'Usage reports',
    measure: 'reporting samples',
    activeSamples: 15,
    observedSamples: 20,
    expectedSamples: 20,
    averageActions: 3.5,
    lastActivityUtc: '2026-05-19T00:00:00Z',
    ...over,
  };
}

function usr(over: Partial<LicenceActivityUser> = {}): LicenceActivityUser {
  return {
    userId: Math.floor(Math.random() * 1e9),
    userPrincipalName: 'ada@contoso.com',
    department: 'Engineering',
    country: 'Greece',
    accountEnabled: true,
    workloads: [evidence()],
    ...over,
  };
}

describe('UsersTable', () => {
  it('identifies the user by UPN only (no display names exist to show or invent)', () => {
    renderWithProvider(<UsersTable rows={[usr()]} workload="teams" workloadLabel="Teams" />);
    expect(screen.getByText('ada@contoso.com')).toBeInTheDocument();
  });

  it('renders non-Latin (Greek) department text without corruption', () => {
    // Department is a Unicode-safe field (unlike the ASCII-by-policy UPN).
    renderWithProvider(
      <UsersTable rows={[usr({ department: 'Καλημέρα κόσμε' })]} workload="teams" workloadLabel="Teams" />,
    );
    expect(screen.getByText('Καλημέρα κόσμε')).toBeInTheDocument();
  });

  it('shows the active-sample frequency and a coloured band', () => {
    renderWithProvider(<UsersTable rows={[usr()]} workload="teams" workloadLabel="Teams" />);
    // 15 / 20 observed samples = 75% (the parenthesised cell value, not the band-method tooltip).
    expect(screen.getByText('(75%)')).toBeInTheDocument();
    expect(screen.getByText('High')).toBeInTheDocument();
  });

  it('renders an unknown band (incomplete coverage) as Unknown, not zero', () => {
    renderWithProvider(
      <UsersTable
        rows={[usr({ workloads: [evidence({ status: 'unknown', band: 'unknown' })] })]}
        workload="teams"
        workloadLabel="Teams"
      />,
    );
    expect(screen.getByText('Unknown')).toBeInTheDocument();
    // A user with no evidence for the workload at all is also Unknown, never a zero.
    renderWithProvider(<UsersTable rows={[usr({ workloads: [] })]} workload="copilot" workloadLabel="Copilot" />);
    expect(screen.getAllByText('Unknown').length).toBeGreaterThan(0);
  });

  it('expands a row to show all five workloads (so the same person can be inspected without re-ranking)', () => {
    const user = usr({
      workloads: WORKLOADS.map((w) =>
        evidence({
          workload: w.key,
          status: w.key === 'copilot' ? 'notImported' : 'available',
          band: w.key === 'copilot' ? 'unknown' : 'high',
        }),
      ),
    });
    renderWithProvider(<UsersTable rows={[user]} workload="teams" workloadLabel="Teams" />);

    // Collapsed: only the selected workload is summarised, so the not-imported one isn't shown yet.
    expect(screen.queryByText('Not imported')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: /Show all workloads/ }));

    // Expanded: every workload is listed with its coverage status.
    for (const w of WORKLOADS) {
      expect(screen.getAllByText(w.label).length).toBeGreaterThan(0);
    }
    expect(screen.getByText('Not imported')).toBeInTheDocument();
  });
});
