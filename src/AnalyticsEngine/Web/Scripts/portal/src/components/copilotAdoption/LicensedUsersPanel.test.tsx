import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import LicensedUsersPanel from './LicensedUsersPanel';
import { AdoptionBand } from '../../types/copilotAdoption';
import type { CopilotAdoptionOptions, LicensedUserAdoptionRow, LicensedUserPage } from '../../types/copilotAdoption';
import { fetchLicensedUsers } from '../../api/copilotAdoptionApi';

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
  coworkMinutesSavedPerMeeting: 10,
  coworkMinutesSavedPerMailThread: 3,
  coworkMinutesSavedPerDocument: 2,
  coworkEstimateLowerBoundRatio: 0.5,
  coworkLoadedCostPerHour: null,
  coworkCurrencyCode: null,
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

  it('shows both source figures only for rows covered by both sources', async () => {
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
          interactions: 18,
          activeDays: 5,
          auditInteractions: 0,
          auditActiveDays: 0,
          auditAppsUsed: 0,
          reportPrompts: 18,
          reportActiveDays: 5,
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
    expect(screen.getAllByText((_content, element) => {
      const text = element?.textContent ?? '';
      return text.includes('Audit D28: 12 interactions, 4 days.')
        && text.includes('Microsoft report D28')
        && text.includes('18 prompts, 5 days.');
    }).length).toBeGreaterThan(0);
    expect(screen.getAllByText('Audit log')).toHaveLength(2);
    expect(screen.getByText('Microsoft usage report')).toBeInTheDocument();
    expect(screen.queryByText(/Audit D28: 0 interactions/)).not.toBeInTheDocument();
  });
});
