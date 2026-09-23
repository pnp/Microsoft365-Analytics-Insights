import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, waitFor, fireEvent } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import type {
  CopilotAdoptionAvailability,
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
} from '../types/copilotAdoption';

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

const OPTIONS: CopilotAdoptionOptions = {
  windowDays: 28,
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
  coworkCollaborationWeight: 20,
  coworkMeetingWeight: 35,
  coworkEmailWeight: 25,
  coworkDocumentWeight: 20,
  coworkCollaborationTarget: 50,
  coworkMeetingTarget: 5,
  coworkEmailTarget: 80,
  coworkDocumentTarget: 30,
  coworkLoadMinScore: 50,
  coworkFluencyMinScore: 50,
  coworkRegularMinActiveDays: 3,
  coworkAgentFamiliarityUplift: 10,
  copilotMinutesSavedPerMeeting: 10,
  copilotMinutesSavedPerMailThread: 5,
  copilotMinutesSavedPerDocument: 15,
  coworkEstimateLowerBoundRatio: 0.5,
  usageReportLagDays: 3,
  topSegments: 10,
  minSeatsPerSegment: 5,
  accountabilityDimension: 'directManager',
  maxLicensedUsersScored: 50000,
  maxOpportunityCandidates: 50000,
  maxAgents: 1000,
  maxUnlicensedUsersScored: 50000,
  maxCoworkUsersScored: 50000,
};

function incompleteSummary(): CopilotAdoptionSummary {
  return {
    generatedUtc: '2026-01-01T00:00:00Z',
    windowDays: 28,
    fromUtc: '2025-12-04T00:00:00Z',
    toUtc: '2026-01-01T00:00:00Z',
    dataSources: {
      auditEvents: false,
      usageReport: false,
      activityReport: false,
      agentActivity: false,
      coworkUsageReport: false,
    },
    seatLicenceTypes: [],
    licensedUsers: 0,
    scoredUsers: 0,
    activeUsers: 0,
    neverUsedUsers: 0,
    dormantUsers: 0,
    adoptionRatePct: 0,
    habitualUsers: 0,
    habitRatePct: 0,
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
    reclaimSeatsFromActiveBands: 0,
    usageReportSourcedUsers: 0,
    usageReportSourcedUserPct: 0,
    usageReportWindowMismatch: false,
    averageAdoptionScore: 0,
    medianAdoptionScore: 0,
    totalInteractions: 0,
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
      coworkTaskUsers: 0,
      observedCoworkTasks: 0,
      projectedCoworkUsers: 0,
      coworkTasksPerPersonPerMonth: 0,
      coworkTaskRateBasis: 'assumed',
      coworkTaskRateUsers: 0,
      coworkTasks: 0,
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
    adoptionByDepartment: [],
    adoptionByCountry: [],
    accountabilityDimension: null,
    accountabilityDimensionLabel: null,
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
    options: OPTIONS,
    warnings: ['Copilot adoption figures are incomplete because licence types could not be loaded.'],
    figuresIncomplete: true,
    incompleteReasons: ['licence types'],
  };
}

describe('CopilotAdoptionPage accountability roll-up', () => {
  beforeEach(() => {
    fetchAdoptionAvailability.mockReset();
    fetchAdoptionSummary.mockReset();
    fetchAdoptionFilters.mockReset();
    fetchAdoptionSql.mockReset();

    fetchAdoptionAvailability.mockResolvedValue({
      available: true,
      copilotAuditImportEnabled: false,
      copilotUsageReportImportEnabled: false,
      userMetadataImportEnabled: false,
      m365UsageReportImportEnabled: false,
      messages: [],
    } as CopilotAdoptionAvailability);
    fetchAdoptionSummary.mockResolvedValue(incompleteSummary());
    fetchAdoptionFilters.mockResolvedValue(null);
    fetchAdoptionSql.mockResolvedValue(null);
  });

  it('renders incomplete figures without accountability metadata', async () => {
    renderWithProvider(<CopilotAdoptionPage />);

    // The roll-up is analyst detail, so it lives in the Analyst view after the executive/analyst
    // split. The regression this guards is that the page renders at all when the licence-types
    // query failed and the accountability metadata is therefore absent.
    fireEvent.click(await screen.findByRole('tab', { name: 'Analyst view' }));

    await waitFor(() => expect(screen.getByText('Accountability roll-up')).toBeInTheDocument());

    expect(screen.getAllByText(/figures are incomplete/i).length).toBeGreaterThan(0);
    expect(screen.getByText(/Aggregate-only view by direct manager/i)).toBeInTheDocument();
  });
});


