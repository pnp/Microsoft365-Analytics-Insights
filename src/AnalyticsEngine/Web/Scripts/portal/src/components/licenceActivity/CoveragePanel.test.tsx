import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import CoveragePanel from './CoveragePanel';
import type { LicenceActivityCoverage } from '../../types/licenceActivity';

function cov(over: Partial<LicenceActivityCoverage> = {}): LicenceActivityCoverage {
  return {
    workload: 'teams',
    status: 'available',
    source: 'Usage reports',
    measure: 'reporting samples',
    granularity: 'weekly',
    message: null,
    effectiveFromUtc: '2026-04-22T00:00:00Z',
    effectiveToUtc: '2026-05-19T00:00:00Z',
    latestImportUtc: '2026-05-20T00:00:00Z',
    lagDays: 1,
    reportPeriodDays: 28,
    expectedSamples: 100,
    observedSamples: 95,
    unmatchedUsers: 2,
    snapshotDates: [],
    ...over,
  };
}

const NOW = new Date('2026-05-20T12:00:00Z');

describe('CoveragePanel', () => {
  it('renders each backend status with a friendly label and shows the snapshot lifetime', () => {
    renderWithProvider(
      <CoveragePanel
        generatedUtc="2026-05-20T10:00:00Z"
        expiresUtc="2026-05-20T10:05:00Z"
        now={NOW}
        coverage={[
          cov({ workload: 'teams', status: 'available' }),
          cov({ workload: 'outlook', status: 'partial' }),
          cov({ workload: 'onedrive', status: 'missingCoverage' }),
          cov({ workload: 'sharepoint', status: 'unmatchableIdentity' }),
          cov({ workload: 'copilot', status: 'notImported' }),
        ]}
      />,
    );

    expect(screen.getByText('Available')).toBeInTheDocument();
    expect(screen.getByText('Partial')).toBeInTheDocument();
    expect(screen.getByText('Missing coverage')).toBeInTheDocument();
    expect(screen.getByText('Unmatchable identity')).toBeInTheDocument();
    expect(screen.getByText('Not imported')).toBeInTheDocument();

    // Sources/coverage are explicit: workloads, source and snapshot lifetime are all shown.
    expect(screen.getByText('Teams')).toBeInTheDocument();
    expect(screen.getAllByText(/Usage reports/).length).toBeGreaterThan(0);
    expect(screen.getByText(/generated/i)).toBeInTheDocument();
  });

  it('labels a disabled import distinctly', () => {
    renderWithProvider(
      <CoveragePanel
        generatedUtc="2026-05-20T10:00:00Z"
        expiresUtc="2026-05-20T10:05:00Z"
        now={NOW}
        coverage={[cov({ workload: 'teams', status: 'disabled' })]}
      />,
    );
    expect(screen.getByText('Import disabled')).toBeInTheDocument();
  });
});
