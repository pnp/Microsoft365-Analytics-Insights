import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import LicensedUsersPanel from './LicensedUsersPanel';
import { AdoptionBand } from '../../types/copilotAdoption';
import type { CopilotAdoptionOptions, LicensedUserAdoptionRow, LicensedUserPage } from '../../types/copilotAdoption';
import { fetchLicensedUsers } from '../../api/copilotAdoptionApi';
import { PRINT_ROW_LIMIT, requestPrint, resetPrintPreparation } from '../shared/printPreparation';

vi.mock('../../api/copilotAdoptionApi', () => ({
  fetchLicensedUsers: vi.fn(),
  licensedUsersExportUrl: vi.fn(() => '#export'),
}));

const OPTIONS: CopilotAdoptionOptions = {
  windowDays: 28,
  historyDays: 365,
  workingDaysPerWeek: 5,
  frequencyTargetRatio: 0.6,
  depthTargetInteractionsPerActiveDay: 5,
  depthMinActiveDays: 3,
  breadthTargetApps: 3,
  frequencyWeight: 50,
  depthWeight: 30,
  breadthWeight: 20,
  championScore: 75,
  establishedScore: 50,
  developingScore: 25,
  habitBucketNormalisationDays: 28,
  habitModerateMinDays: 6,
  habitFrequentMinDays: 11,
  habitDailyMinDays: 20,
  agentReviewInactiveDays: 30,
  agentRetireInactiveDays: 90,
  agentNewDays: 14,
  reclaimGraceDays: 30,
  agentMinUsers: 3,
  agentHistoryDays: 120,
  opportunityUnlicensedCopilotWeight: 35,
  opportunityCollaborationWeight: 25,
  opportunityEmailWeight: 20,
  opportunityDocumentWeight: 20,
  opportunityCopilotTarget: 20,
  opportunityCopilotTargetBasisDays: 28,
  opportunityCollaborationTarget: 60,
  opportunityEmailTarget: 80,
  opportunityDocumentTarget: 40,
  opportunityRecommendScore: 50,
  opportunityProvenDemandMinActiveDays: 3,
  coworkCollaborationWeight: 20,
  coworkMeetingWeight: 30,
  coworkEmailWeight: 25,
  coworkDocumentWeight: 25,
  coworkCollaborationTarget: 40,
  coworkMeetingTarget: 6,
  coworkEmailTarget: 40,
  coworkDocumentTarget: 20,
  coworkLoadMinScore: 50,
  coworkFluencyMinScore: 50,
  coworkRegularMinActiveDays: 3,
  coworkAgentFamiliarityUplift: 10,
  copilotMinutesSavedPerMeeting: 10,
  copilotMinutesSavedPerMailThread: 3,
  copilotMinutesSavedPerDocument: 2,
  coworkEstimateLowerBoundRatio: 0.5,
  usageReportLagDays: 3,
  topSegments: 10,
  minSeatsPerSegment: 5,
  maxLicensedUsersScored: 50000,
  maxOpportunityCandidates: 50000,
  maxAgents: 1000,
  maxUnlicensedUsersScored: 50000,
  maxCoworkUsersScored: 50000,
};

function row(over: Partial<LicensedUserAdoptionRow>): LicensedUserAdoptionRow {
  return {
    userId: 1,
    userPrincipalName: 'user@contoso.com',
    mail: 'user@contoso.com',
    department: 'Finance',
    jobTitle: 'Analyst',
    country: null,
    officeLocation: null,
    companyName: null,
    manager: null,
    accountEnabled: true,
    accountCreatedUtc: '2026-01-01T00:00:00Z',
    tenureStartUtc: '2026-01-01T00:00:00Z',
    tenureBasis: 'accountAge',
    daysSinceTenureStart: 120,
    tooNewToJudge: false,
    reclaimEligibility: '',
    reclaimEligibilityReason: '',
    reclaimExclusionReason: null,
    reclaimExclusionNote: null,
    reclaimExcludedBy: null,
    reclaimExcludedUtc: null,
    reclaimExclusionReviewAfterUtc: null,
    reclaimExclusionExpired: false,
    seatLicences: 'Microsoft 365 Copilot',
    interactions: 12,
    activeDays: 4,
    auditInteractions: 12,
    auditActiveDays: 4,
    auditAppsUsed: 2,
    sourceComparisonAvailable: false,
    expectedActiveDays: 12,
    appsUsed: 2,
    agentsUsed: 0,
    coworkInteractions: 0,
    usedCowork: false,
    firstInteractionUtc: '2026-08-01T00:00:00Z',
    lastInteractionUtc: '2026-08-20T00:00:00Z',
    daysSinceLastUse: 3,
    reportPrompts: null,
    reportActiveDays: null,
    reportLastActivityUtc: null,
    adoptionScore: 42,
    frequencyScore: 33,
    depthScore: 80,
    breadthScore: 67,
    band: AdoptionBand.Developing,
    bandName: 'Developing',
    signalSource: 'audit',
    recommendedAction: 'Build a first habit.',
    recommendedActionCode: 'coach',
    recommendedActionLabel: 'Build a first habit',
    ...over,
  };
}

describe('LicensedUsersPanel source reconciliation', () => {
  beforeEach(() => vi.mocked(fetchLicensedUsers).mockReset());

  it('keeps the signal out of the table, and shows both source figures in the expanded row of a user covered by both', async () => {
    const page: LicensedUserPage = {
      total: 3,
      skip: 0,
      take: 50,
      warnings: [],
      rows: [
        row({
          userId: 1,
          userPrincipalName: 'both@contoso.com',
          sourceComparisonAvailable: true,
          auditInteractions: 12,
          auditActiveDays: 4,
          auditAppsUsed: 2,
          reportPrompts: 18,
          reportActiveDays: 5,
          reportLastActivityUtc: '2026-08-20T00:00:00Z',
        }),
        row({ userId: 2, userPrincipalName: 'audit-only@contoso.com' }),
        row({
          userId: 3,
          userPrincipalName: 'report-only@contoso.com',
          signalSource: 'usageReport',
          sourceComparisonAvailable: true,
          interactions: 18,
          activeDays: 5,
          auditInteractions: 0,
          auditActiveDays: 0,
          auditAppsUsed: 0,
          reportPrompts: 18,
          reportActiveDays: 5,
          reportLastActivityUtc: '2026-08-20T00:00:00Z',
        }),
      ],
    };
    vi.mocked(fetchLicensedUsers).mockResolvedValue(page);

    renderWithProvider(
      <LicensedUsersPanel
        windowDays={28}
        filterOptions={null}
        actionPlan={[]}
        options={OPTIONS}
        dataSources={{
          auditAvailable: true,
          copilotUsageReportAvailable: true,
          m365UsageReportsAvailable: false,
          userMetadataAvailable: true,
          copilotUsageReportDate: '2026-08-20T00:00:00Z',
          copilotUsageReportPeriodDays: 28,
          m365UsageReportDate: null,
          copilotUsageReportObfuscated: false,
        }}
      />,
    );

    expect(await screen.findByText('both@contoso.com')).toBeInTheDocument();

    // The table fits the page: where the signal came from is detail, not a column of its own.
    expect(screen.queryByRole('columnheader', { name: /Signal/ })).toBeNull();
    expect(screen.queryByText('Audit log')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Show the full assessment for both@contoso.com' }));
    expect(screen.getByText('Audit log')).toBeInTheDocument();
    expect(screen.getByText('both sources')).toBeInTheDocument();
    expect(screen.getByText(/Audit D28: 12 interactions, 4 days\./).textContent).toMatch(/Microsoft report D28.*18 prompts, 5 days\./);

    // Every row at once, as the printout gets it.
    fireEvent.click(screen.getByRole('button', { name: 'Expand all' }));
    expect(screen.getAllByText('Audit log')).toHaveLength(2);
    expect(screen.getByText('Microsoft usage report')).toBeInTheDocument();
    expect(screen.getAllByText('both sources')).toHaveLength(2);
    expect(screen.getByText(/Audit D28: 0 interactions, 0 days\./)).toBeInTheDocument();

    // The audit-only row has nothing to reconcile, so it must not claim it has: two comparisons for
    // three expanded rows.
    expect(screen.getAllByText(/^Audit D28: /)).toHaveLength(2);
  });

  it('keeps the reclaim tier on the row, under the action it qualifies', async () => {
    vi.mocked(fetchLicensedUsers).mockResolvedValue({
      total: 1,
      skip: 0,
      take: 50,
      warnings: [],
      rows: [row({ userPrincipalName: 'idle@contoso.com', reclaimEligibility: 'probable', reclaimExclusionExpired: false })],
    });

    renderWithProvider(<LicensedUsersPanel windowDays={28} filterOptions={null} actionPlan={[]} options={OPTIONS} />);

    expect(await screen.findByText('idle@contoso.com')).toBeInTheDocument();
    expect(within(screen.getByRole('table')).getByText('Probable reclaim')).toBeInTheDocument();
  });
});

// Rendering a hundred-odd rows through Fluent in jsdom takes seconds, and a CI runner is slower.
describe('LicensedUsersPanel printing', { timeout: 30000 }, () => {
  const upn = (id: number) => `demo.user${String(id).padStart(7, '0')}@contoso.example`;

  /** A server holding `total` seat holders, paging and clamping exactly as the API does. */
  function serve(total: number) {
    vi.mocked(fetchLicensedUsers).mockImplementation(async (_windowDays, _filters, skip, take) => {
      const count = Math.max(0, Math.min(take, 500, total - skip));
      return {
        total,
        skip,
        take,
        warnings: [],
        rows: Array.from({ length: count }, (_, i) => row({ userId: skip + i + 1, userPrincipalName: upn(skip + i + 1) })),
      };
    });
  }

  const listed = () => screen.queryAllByText(/^demo\.user\d+@contoso\.example$/).length;

  function renderPanel(initialBands?: AdoptionBand[]) {
    return renderWithProvider(
      <LicensedUsersPanel
        windowDays={28}
        filterOptions={{ bands: [{ value: AdoptionBand.Dormant, name: 'Dormant' }], departments: ['Finance'] } as never}
        actionPlan={[]}
        options={OPTIONS}
        initialBands={initialBands}
      />,
    );
  }

  beforeEach(() => {
    vi.mocked(fetchLicensedUsers).mockReset();
    resetPrintPreparation();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    resetPrintPreparation();
  });

  it('prints every seat holder rather than the page on screen, then returns to that page', async () => {
    serve(75);
    renderPanel();
    await waitFor(() => expect(listed()).toBe(50));

    let printed = -1;
    vi.spyOn(window, 'print').mockImplementation(() => {
      printed = listed();
    });
    await act(async () => {
      await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
    });

    expect(printed).toBe(75);
    expect(listed()).toBe(50);
  });

  it('prints the first rows of a list too long to print in full, and says so above it', async () => {
    serve(PRINT_ROW_LIMIT + 10);
    renderPanel();
    await waitFor(() => expect(listed()).toBe(50));

    const printed = { rows: -1, warned: false };
    vi.spyOn(window, 'print').mockImplementation(() => {
      printed.rows = listed();
      printed.warned =
        screen.queryByText(
          `Only the first ${PRINT_ROW_LIMIT.toLocaleString('en')} of ${(PRINT_ROW_LIMIT + 10).toLocaleString('en')} rows are printed`,
          { exact: false },
        ) !== null;
    });
    await act(async () => {
      await expect(requestPrint()).resolves.toEqual({
        kind: 'printed',
        truncated: { rows: PRINT_ROW_LIMIT + 10, limit: PRINT_ROW_LIMIT },
      });
    });

    expect(printed).toEqual({ rows: PRINT_ROW_LIMIT, warned: true });
    expect(listed()).toBe(50);
  });

  it('keeps the filter bar and the pager off paper, and prints what the bar was set to', async () => {
    serve(75);
    renderPanel([AdoptionBand.Dormant]);
    await waitFor(() => expect(listed()).toBe(50));

    for (const control of [
      screen.getByRole('combobox', { name: 'Filter by engagement band' }),
      screen.getByRole('checkbox', { name: 'Disabled accounts only' }),
      screen.getByRole('button', { name: 'Next' }),
    ]) {
      expect(control.closest('[data-print="hide"]')).not.toBeNull();
    }

    expect(document.querySelector('[data-print="only"]')?.textContent).toBe(
      'Filters: Band: Dormant \u00b7 Action: All recommended actions \u00b7 Reclaim tier: All reclaim tiers',
    );
  });

  it('leaves who the list is about to the page-wide filter: no department drop-down of its own', async () => {
    // A second, list-only department filter would combine with the page-wide one by AND and could
    // contradict it - "Department is Sales" above, "Finance" here, and an empty list nobody can explain.
    serve(5);
    renderPanel();
    await waitFor(() => expect(listed()).toBe(5));

    expect(screen.queryByRole('combobox', { name: /department/i })).not.toBeInTheDocument();
  });
});
