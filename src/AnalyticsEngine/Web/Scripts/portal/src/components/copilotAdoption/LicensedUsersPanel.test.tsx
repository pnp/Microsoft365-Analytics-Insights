import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, screen, waitFor } from '@testing-library/react';
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

  it('keeps both source figures on hover only for rows covered by both sources', async () => {
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

    // The reconciliation figures are carried on hover rather than printed inline: rendered in full
    // they wrapped to five lines and set the height of every row in the table. They must still be
    // reachable, and still be per-row - that is what this asserts.
    const markers = screen.getAllByText('both sources');
    expect(markers).toHaveLength(2);

    const titles = markers.map((m) => m.getAttribute('title') ?? '');
    expect(titles.some((t) =>
      t.includes('Audit D28: 12 interactions, 4 days.')
      && t.includes('Microsoft report D28')
      && t.includes('18 prompts, 5 days.'))).toBe(true);
    expect(titles.some((t) =>
      t.includes('Audit D28: 0 interactions, 0 days.')
      && t.includes('Microsoft report D28')
      && t.includes('18 prompts, 5 days.'))).toBe(true);

    expect(screen.getAllByText('Audit log')).toHaveLength(2);
    expect(screen.getByText('Microsoft usage report')).toBeInTheDocument();

    // The audit-only row has nothing to reconcile, so it must not claim it has.
    expect(screen.queryByText(/Audit D28: 12 interactions, 4 days\./)).toBeNull();
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

  it('refuses, rather than printing one page, when the list is longer than can be printed', async () => {
    serve(PRINT_ROW_LIMIT + 10);
    renderPanel();
    await waitFor(() => expect(listed()).toBe(50));
    vi.spyOn(window, 'print').mockImplementation(() => {});

    await expect(requestPrint()).resolves.toMatchObject({ kind: 'tooManyRows', rows: PRINT_ROW_LIMIT + 10 });
    expect(window.print).not.toHaveBeenCalled();
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
      'Filters: Band: Dormant \u00b7 Action: All recommended actions \u00b7 Reclaim tier: All reclaim tiers'
        + ' \u00b7 Department: All departments',
    );
  });
});
