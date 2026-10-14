import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, within, fireEvent } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import type {
  CopilotAdoptionAvailability,
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  ManagerModellingFigures,
} from '../types/copilotAdoption';

/**
 * Do people managers use Copilot themselves (#641)? Where the figures land on the page: one line on the
 * executive view, and the same figures - with the split by seat - above the analyst view's department
 * table, whose rows gain the per-department columns. Synthetic figures only.
 */

const fetchAdoptionAvailability = vi.fn();
const fetchAdoptionSummary = vi.fn();
const fetchAdoptionFilters = vi.fn();
const fetchAdoptionSql = vi.fn();

vi.mock('../api/copilotAdoptionApi', () => ({
  fetchAdoptionAvailability: (...args: unknown[]) => fetchAdoptionAvailability(...args),
  fetchAdoptionSummary: (...args: unknown[]) => fetchAdoptionSummary(...args),
  fetchAdoptionFilters: (...args: unknown[]) => fetchAdoptionFilters(...args),
  fetchAdoptionSql: (...args: unknown[]) => fetchAdoptionSql(...args),
  workbookExportUrl: () => '/api/CopilotAdoption/export?windowDays=28',
}));

const { default: CopilotAdoptionPage } = await import('./CopilotAdoptionPage');

const GREEK_DEPARTMENT = 'Καλημέρα κόσμε';

const OPTIONS = {
  windowDays: 28,
  activationWindowDays: 28,
  historyDays: 365,
  workingDaysPerWeek: 5,
  frequencyTargetRatio: 0.6,
  depthTargetInteractionsPerActiveDay: 5,
  depthMinActiveDays: 2,
  breadthTargetApps: 3,
  frequencyWeight: 40,
  depthWeight: 35,
  breadthWeight: 25,
  championScore: 75,
  establishedScore: 50,
  developingScore: 25,
  habitBucketNormalisationDays: 28,
  habitModerateMinDays: 4,
  habitFrequentMinDays: 8,
  habitDailyMinDays: 16,
  agentReviewInactiveDays: 30,
  agentRetireInactiveDays: 60,
  agentNewDays: 14,
  reclaimGraceDays: 14,
  agentMinUsers: 3,
  agentHistoryDays: 120,
  opportunityUnlicensedCopilotWeight: 40,
  opportunityCollaborationWeight: 25,
  opportunityEmailWeight: 20,
  opportunityDocumentWeight: 15,
  opportunityCopilotTarget: 20,
  opportunityCopilotTargetBasisDays: 28,
  opportunityCollaborationTarget: 30,
  opportunityEmailTarget: 40,
  opportunityDocumentTarget: 20,
  opportunityRecommendScore: 60,
  opportunityProvenDemandMinActiveDays: 3,
  usageReportLagDays: 3,
  topSegments: 10,
  minSeatsPerSegment: 5,
  accountabilityDimension: 'directManager',
  maxLicensedUsersScored: 50000,
  maxOpportunityCandidates: 50000,
  maxAgents: 1000,
  maxUnlicensedUsersScored: 50000,
  maxCoworkUsersScored: 50000,
} as unknown as CopilotAdoptionOptions;

const FIGURES: ManagerModellingFigures = {
  reportsWithManager: 110,
  managersStatusKnown: 24,
  managersStatusUnknown: 0,
  managersActive: 15,
  managersActivePct: 62.5,
  reportsManagerActive: 70,
  reportsActiveRatePctManagerActive: 71,
  reportsHabitRatePctManagerActive: 34,
  reportsManagerInactive: 40,
  reportsActiveRatePctManagerInactive: 48,
  reportsHabitRatePctManagerInactive: 19,
  reportsManagerUnknown: 0,
  reportsManagerActiveLicensed: 50,
  reportsActiveRatePctManagerActiveLicensed: 76,
  reportsHabitRatePctManagerActiveLicensed: 40,
  reportsManagerActiveUnlicensed: 20,
  reportsActiveRatePctManagerActiveUnlicensed: 58.5,
  reportsHabitRatePctManagerActiveUnlicensed: 20,
  reportsManagerInactiveLicensed: 30,
  reportsActiveRatePctManagerInactiveLicensed: 52,
  reportsHabitRatePctManagerInactiveLicensed: 21,
  reportsManagerInactiveUnlicensed: 10,
  reportsActiveRatePctManagerInactiveUnlicensed: 40,
  reportsHabitRatePctManagerInactiveUnlicensed: 14,
};

function summaryWithManagers(): CopilotAdoptionSummary {
  return {
    generatedUtc: '2026-01-01T00:00:00Z',
    windowDays: 28,
    fromUtc: '2025-12-04T00:00:00Z',
    toUtc: '2026-01-01T00:00:00Z',
    dataSources: {
      auditAvailable: true,
      copilotUsageReportAvailable: false,
      m365UsageReportsAvailable: false,
      userMetadataAvailable: true,
      copilotUsageReportDate: null,
      copilotUsageReportPeriodDays: 0,
      m365UsageReportDate: null,
      copilotUsageReportObfuscated: false,
    },
    seatLicenceTypes: [],
    licensedUsers: 120,
    scoredUsers: 120,
    activeUsers: 70,
    neverUsedUsers: 30,
    dormantUsers: 20,
    adoptionRatePct: 58.3,
    habitualUsers: 30,
    habitRatePct: 25,
    reclaimableSeats: 0,
    disabledLicensedUsers: 0,
    reclaimCertainSeats: 0,
    reclaimProbableSeats: 0,
    reclaimReviewSeats: 0,
    reclaimExcludedUsers: 0,
    expiredReclaimExclusions: 0,
    tooNewToJudgeUsers: 0,
    reclaimCaveat: null,
    reclaimSeatsHeldBackForWindowMismatch: 0,
    reclaimSeatsHeldBackForReview: 0,
    reclaimSeatsNoLongerHeld: 0,
    reclaimSeatsFromActiveBands: 0,
    usageReportSourcedUsers: 0,
    usageReportSourcedUserPct: 0,
    usageReportWindowMismatch: false,
    averageAdoptionScore: 30,
    medianAdoptionScore: 25,
    totalInteractions: 1200,
    coworkUsers: 0,
    coworkAdoptionPct: 0,
    coworkInteractions: 0,
    coworkDetected: false,
    coworkReadinessAvailable: false,
    coworkScoredUsers: 0,
    coworkEstablishedUsers: 0,
    coworkTriallingUsers: 0,
    coworkPrimeCandidates: 0,
    coworkBuildFluencyFirst: 0,
    coworkRecommendedForPolicy: 0,
    coworkAverageCoordinationLoad: 0,
    coworkAverageFluency: 0,
    coworkTiers: [],
    coworkQuadrant: [],
    coworkByDepartment: [],
    coworkCreditPosition: {
      available: false,
      snapshotUtc: null,
      entitled: null,
      consumed: null,
      available_credits: null,
      payAsYouGoConsumed: null,
      status: null,
      perUserCreditsAvailable: false,
    },
    coworkValueEstimate: {
      isModelled: false,
      cohortUsers: 0,
      activities: [],
      projectedCoworkTasks: 0,
      hoursPerMonthLow: 0,
      hoursPerMonthHigh: 0,
      assumptions: [],
    },
    unlicensedActiveUsers: 0,
    recommendedForLicence: 0,
    funnel: [],
    bandBreakdown: [],
    habitBuckets: [],
    intensityByDepartment: [],
    actionPlan: [],
    adoptionByDepartment: [
      { segment: GREEK_DEPARTMENT, licensedUsers: 60, activeUsers: 30, habitualUsers: 12, neverUsedUsers: 20, adoptionRatePct: 50, averageAdoptionScore: 28 },
      { segment: 'Contoso Legal', licensedUsers: 8, activeUsers: 5, habitualUsers: 2, neverUsedUsers: 2, adoptionRatePct: 62.5, averageAdoptionScore: 33 },
    ],
    managerModellingByDepartment: [
      { ...FIGURES, segment: GREEK_DEPARTMENT, licensedUsers: 60, managersActivePct: 75, reportsActiveRatePctManagerActive: 66.7 },
      {
        ...FIGURES,
        segment: 'Contoso Legal',
        licensedUsers: 8,
        managersStatusKnown: 2,
        managersActive: null,
        managersActivePct: null,
        reportsActiveRatePctManagerActive: null,
        reportsActiveRatePctManagerInactive: null,
        reportsHabitRatePctManagerActive: null,
        reportsHabitRatePctManagerInactive: null,
      },
    ],
    habitByDepartment: [],
    adoptionByCountry: [],
    emailDomains: [],
    accountabilityDimension: 'department',
    accountabilityDimensionLabel: 'Department',
    accountabilityRollup: [],
    usageByApp: [],
    opportunityByDepartment: [],
    weeklyTrend: [],
    weeklyVolumeTrend: [],
    scoreProfiles: [],
    concentration: [],
    combinedByDepartment: [],
    topResourceTypes: [],
    agents: {
      historyDays: 120,
      activeAgents: 0,
      knownAgents: 0,
      customAgents: 0,
      agentUsers: 0,
      licensedAgentUsers: 0,
      agentInteractions: 0,
      interactionsPerAgentUser: 0,
      mostPopularAgent: null,
      mostVersatileAgent: null,
      healthBreakdown: [],
      usageByDepartment: [],
      usageByAgent: [],
      agents: [],
    },
    unlicensed: {
      activeUsers: 0,
      interactions: 0,
      interactionsPerUserPerMonth: 0,
      agentUsers: 0,
      habitBuckets: [],
      usageByApp: [],
      usageByDepartment: [],
      truncated: false,
    },
    scopedEmailDomain: null,
    unscopedSections: [],
    options: { ...OPTIONS, accountabilityDimension: 'department' },
    warnings: [],
    figuresIncomplete: false,
    incompleteReasons: [],
    ...FIGURES,
  } as unknown as CopilotAdoptionSummary;
}

describe('CopilotAdoptionPage manager modelling', () => {
  beforeEach(() => {
    fetchAdoptionAvailability.mockReset();
    fetchAdoptionSummary.mockReset();
    fetchAdoptionFilters.mockReset();
    fetchAdoptionSql.mockReset();

    fetchAdoptionAvailability.mockResolvedValue({
      available: true,
      copilotAuditImportEnabled: true,
      copilotUsageReportImportEnabled: false,
      userMetadataImportEnabled: true,
      m365UsageReportImportEnabled: false,
      messages: [],
    } as CopilotAdoptionAvailability);
    fetchAdoptionSummary.mockResolvedValue(summaryWithManagers());
    fetchAdoptionFilters.mockResolvedValue(null);
    fetchAdoptionSql.mockResolvedValue(null);
  });

  it('puts one line on the executive view, with its caveat, and the department columns in the analyst view', async () => {
    renderWithProvider(<CopilotAdoptionPage />);

    const line = await screen.findByTestId('manager-modelling-line');
    expect(within(line).getByText(/^62\.5% of the 24 people managers whose own use is known used Copilot themselves/)).toBeInTheDocument();
    expect(within(line).getByText(/active at 71% \(habit 34%\), against 48% \(habit 19%\)/)).toBeInTheDocument();
    expect(within(line).getByText(/^An association, not a cause/)).toBeInTheDocument();
    expect(within(line).queryByText(/Managers with a Copilot seat/)).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));

    const analystLine = await screen.findByTestId('manager-modelling-line');
    expect(within(analystLine).getByText(/Managers with a Copilot seat: their direct reports were active at 76%/)).toBeInTheDocument();
    expect(within(analystLine).getByText(/Managers without a seat: their direct reports were active at 58\.5%/)).toBeInTheDocument();

    expect(screen.getByRole('columnheader', { name: 'Managers using Copilot' })).toBeInTheDocument();
    const greek = screen.getAllByText(GREEK_DEPARTMENT).map((cell) => cell.closest('tr')).find((row) => row && within(row).queryByText('75%')) as HTMLElement;
    expect(greek).toBeTruthy();
    expect(within(greek).getByText('66.7%')).toBeInTheDocument();
    const legal = screen.getByText('Contoso Legal').closest('tr') as HTMLElement;
    expect(within(legal).getAllByLabelText('Too few people to show without singling someone out')).toHaveLength(5);
  });

  it('shows the line and the columns in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(<CopilotAdoptionPage />, { language: 'es' });

    const line = await screen.findByTestId('manager-modelling-line');
    expect(within(line).getByText('¿Usan Copilot los propios responsables?')).toBeInTheDocument();
    expect(within(line).getByText(/^El 62,5% de los 24 responsables de equipo cuyo uso propio se conoce usó Copilot/)).toBeInTheDocument();
    expect(within(line).getByText(/^Es una asociación, no una causa/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Vista de analista' }));

    expect(await screen.findByRole('columnheader', { name: 'Responsables que usan Copilot' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Activos: el responsable no lo usa' })).toBeInTheDocument();
    expect(screen.getAllByText(GREEK_DEPARTMENT).length).toBeGreaterThan(0);
    expect(screen.getByText('66,7%')).toBeInTheDocument();
    expect(screen.queryByText('Managers using Copilot')).not.toBeInTheDocument();
  });

  it('shows the aggregate figures to a reader without See PII', async () => {
    renderWithProvider(<CopilotAdoptionPage />, { access: { administration: false, seePii: false } });

    const line = await screen.findByTestId('manager-modelling-line');
    expect(within(line).getByText(/^62\.5% of the 24 people managers/)).toBeInTheDocument();
  });

  it('shows nothing for a server that predates the figures', async () => {
    const older = summaryWithManagers() as unknown as Record<string, unknown>;
    for (const key of Object.keys(FIGURES)) delete older[key];
    delete older.managerModellingByDepartment;
    fetchAdoptionSummary.mockResolvedValue(older as unknown as CopilotAdoptionSummary);

    renderWithProvider(<CopilotAdoptionPage />);

    expect(await screen.findByRole('tab', { name: 'Executive view', selected: true })).toBeInTheDocument();
    expect(await screen.findByText('Department league table')).toBeInTheDocument();
    expect(screen.queryByTestId('manager-modelling-line')).not.toBeInTheDocument();
  });
});
