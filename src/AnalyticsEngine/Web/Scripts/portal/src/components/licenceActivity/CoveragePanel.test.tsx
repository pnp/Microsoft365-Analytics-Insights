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
    measure: 'activity counted by Microsoft',
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
  it('renders each backend status with a friendly label and shows how long the figures last', () => {
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
    expect(screen.getByText('Identities could not be matched')).toBeInTheDocument();
    expect(screen.getByText('Not imported')).toBeInTheDocument();

    // Sources/coverage are explicit: services, source and how long the figures last are all shown.
    expect(screen.getByText('Teams')).toBeInTheDocument();
    expect(screen.getAllByText(/Usage reports/).length).toBeGreaterThan(0);
    expect(screen.getByText(/prepared/i)).toBeInTheDocument();
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
    expect(screen.getByText('Import switched off')).toBeInTheDocument();
  });

  it('translates every backend source and sampling identifier into plain English', () => {
    // Every `granularity` the coverage SQL can emit. A camelCase identifier reaching this panel is the
    // exact defect this report was fixed for, so the guard covers all of them, not just the M365 one.
    const granularities = [
      'weeklySupportingSnapshot',
      'weeklySampleOfRolling7DayReport',
      'eventPositiveOnly',
      'singleRollingWindow',
      'unknown',
    ];
    renderWithProvider(
      <CoveragePanel
        generatedUtc="2026-05-20T10:00:00Z"
        expiresUtc="2026-05-20T10:05:00Z"
        now={NOW}
        coverage={[
          cov({ workload: 'teams', source: 'microsoftGraphUsageReport', granularity: granularities[0] }),
          cov({ workload: 'outlook', source: 'microsoftGraphCopilotUsageReport', granularity: granularities[1] }),
          cov({ workload: 'onedrive', source: 'copilotAudit', granularity: granularities[2] }),
          cov({ workload: 'sharepoint', source: 'copilotInteractions', granularity: granularities[3] }),
          cov({ workload: 'copilot', source: 'microsoftGraphCopilotUsageReport', granularity: granularities[4] }),
        ]}
      />,
    );

    for (const id of granularities) {
      expect(screen.queryByText(new RegExp(id))).not.toBeInTheDocument();
    }
    for (const id of ['microsoftGraphUsageReport', 'microsoftGraphCopilotUsageReport', 'copilotAudit', 'copilotInteractions']) {
      expect(screen.queryByText(new RegExp(id))).not.toBeInTheDocument();
    }
    expect(screen.getByText(/Microsoft 365 usage reports/)).toBeInTheDocument();
    expect(screen.getByText(/one reading per week/)).toBeInTheDocument();
    expect(screen.getByText(/one 7-day report read per week/)).toBeInTheDocument();
    expect(screen.getByText(/Copilot audit log/)).toBeInTheDocument();
    expect(screen.getByText(/Copilot chat history/)).toBeInTheDocument();
  });
});
