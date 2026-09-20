import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, waitFor, fireEvent, within } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import CopilotAdoptionPage from './CopilotAdoptionPage';
import {
  fetchAdoptionAvailability,
  fetchAdoptionFilters,
  fetchAdoptionSql,
  fetchAdoptionSummary,
  fetchLicensedUsers,
  createInterventionFromAction,
} from '../api/copilotAdoptionApi';
import { AdoptionBand, CopilotResourceTypeKind, type CopilotAdoptionOptions, type CopilotAdoptionSummary } from '../types/copilotAdoption';

vi.mock('../api/copilotAdoptionApi', async (importOriginal) => ({
  ...await importOriginal<typeof import('../api/copilotAdoptionApi')>(),
  fetchAdoptionAvailability: vi.fn(),
  fetchAdoptionFilters: vi.fn(),
  fetchAdoptionSql: vi.fn(),
  fetchAdoptionSummary: vi.fn(),
  fetchLicensedUsers: vi.fn(),
  createInterventionFromAction: vi.fn(),
}));

const options: CopilotAdoptionOptions = {
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
  habitModerateMinDays: 4,
  habitFrequentMinDays: 8,
  habitDailyMinDays: 16,
  agentReviewInactiveDays: 30,
  agentRetireInactiveDays: 60,
  agentNewDays: 14,
  reclaimGraceDays: 30,
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
  coworkCollaborationTarget: 100,
  coworkMeetingTarget: 4,
  coworkEmailTarget: 80,
  coworkDocumentTarget: 20,
  coworkLoadMinScore: 60,
  coworkFluencyMinScore: 50,
  coworkRegularMinActiveDays: 4,
  coworkAgentFamiliarityUplift: 10,
  coworkMinutesSavedPerMeeting: 10,
  coworkMinutesSavedPerMailThread: 5,
  coworkMinutesSavedPerDocument: 8,
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

function summary(overrides: Partial<CopilotAdoptionSummary> = {}): CopilotAdoptionSummary {
  return {
    generatedUtc: '2026-01-01T00:00:00Z',
    windowDays: 28,
    fromUtc: '2025-12-05T00:00:00Z',
    toUtc: '2026-01-01T00:00:00Z',
    dataSources: {
      auditAvailable: true,
      copilotUsageReportAvailable: true,
      m365UsageReportsAvailable: true,
      userMetadataAvailable: true,
      copilotUsageReportDate: '2026-01-01T00:00:00Z',
      copilotUsageReportPeriodDays: 28,
      m365UsageReportDate: '2026-01-01T00:00:00Z',
      copilotUsageReportObfuscated: false,
    },
    seatLicenceTypes: [{ id: 1, name: 'Microsoft 365 Copilot', skuPartNumber: 'M365_COPILOT', assignedUsers: 120, isCopilotSeat: true }],
    licensedUsers: 120,
    scoredUsers: 120,
    activeUsers: 72,
    neverUsedUsers: 30,
    dormantUsers: 18,
    adoptionRatePct: 60,
    habitualUsers: 36,
    habitRatePct: 30,
    reclaimableSeats: 24,
    disabledLicensedUsers: 8,
    reclaimCertainSeats: 8,
    reclaimProbableSeats: 16,
    reclaimReviewSeats: 18,
    reclaimExcludedUsers: 6,
    expiredReclaimExclusions: 0,
    tooNewToJudgeUsers: 4,
    reclaimCaveat: null,
    reclaimSeatsHeldBackForWindowMismatch: 0,
    reclaimSeatsHeldBackForReview: 24,
    reclaimSeatsFromActiveBands: 0,
    usageReportSourcedUsers: 0,
    usageReportSourcedUserPct: 0,
    usageReportWindowMismatch: false,
    averageAdoptionScore: 42,
    medianAdoptionScore: 38,
    totalInteractions: 2400,
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
    coworkCreditPosition: { available: false, snapshotUtc: null, entitled: null, consumed: null, available_credits: null, payAsYouGoConsumed: null, status: null, perUserCreditsAvailable: false },
    coworkValueEstimate: { isModelled: false, cohortUsers: 0, addressableMeetings: 0, addressableMailThreads: 0, addressableDocuments: 0, hoursPerMonthLow: 0, hoursPerMonthHigh: 0, assumptions: [] },
    unlicensedActiveUsers: 14,
    recommendedForLicence: 9,
    funnel: [
      { label: 'Licensed', value: 120 },
      { label: 'Ever used Copilot', value: 90 },
      { label: 'Active this period', value: 72 },
      { label: 'Habitual users', value: 36 },
      { label: 'Champions', value: 12 },
    ],
    bandBreakdown: [
      { label: 'Never used', value: 30 },
      { label: 'Dormant', value: 18 },
      { label: 'Trialling', value: 20 },
      { label: 'Developing', value: 16 },
      { label: 'Established', value: 24 },
      { label: 'Champion', value: 12 },
    ],
    habitBuckets: [{ label: 'Frequent', rangeLabel: '8-15 days', users: 20, sharePct: 27.8 }],
    intensityByDepartment: [{ segment: 'Sales', licensedUsers: 50, activeUsers: 20, activeDaysPerUser: 8, actionsPerActiveDay: 3, activeUserAverageScore: 45 }],
    actionPlan: [
      { code: 'coach', label: 'Coach', description: 'Build a first repeatable habit.', users: 40, sharePct: 33.3 },
      { code: 'sustain', label: 'Sustain', description: 'Keep the habit going.', users: 20, sharePct: 16.7 },
    ],
    adoptionByDepartment: [
      { segment: 'Finance', licensedUsers: 30, activeUsers: 10, habitualUsers: 3, neverUsedUsers: 15, adoptionRatePct: 33.3, averageAdoptionScore: 22 },
      { segment: 'Sales', licensedUsers: 50, activeUsers: 40, habitualUsers: 25, neverUsedUsers: 5, adoptionRatePct: 80, averageAdoptionScore: 62 },
    ],
    habitByDepartment: [
      { segment: 'Support', licensedUsers: 25, activeUsers: 25, habitualUsers: 0, neverUsedUsers: 0, adoptionRatePct: 100, averageAdoptionScore: 30 },
      { segment: 'Finance', licensedUsers: 30, activeUsers: 10, habitualUsers: 3, neverUsedUsers: 15, adoptionRatePct: 33.3, averageAdoptionScore: 22 },
    ],
    adoptionByCountry: [{ segment: 'United Kingdom', licensedUsers: 120, activeUsers: 72, habitualUsers: 36, neverUsedUsers: 30, adoptionRatePct: 60, averageAdoptionScore: 42 }],
    emailDomains: [
      { segment: 'fabrikam.com', licensedUsers: 40, activeUsers: 4, habitualUsers: 1, neverUsedUsers: 30, adoptionRatePct: 10, averageAdoptionScore: 9, reclaimableSeats: 28, interactionsPerLicensedUser: 1.1, unlicensedActiveUsers: 12, recommendedForLicence: 7, coworkPrimeCandidates: 0, external: false },
      { segment: 'contoso.com', licensedUsers: 80, activeUsers: 68, habitualUsers: 35, neverUsedUsers: 0, adoptionRatePct: 85, averageAdoptionScore: 58, reclaimableSeats: 4, interactionsPerLicensedUser: 22.5, unlicensedActiveUsers: 2, recommendedForLicence: 2, coworkPrimeCandidates: 0, external: false },
    ],
    scopedEmailDomain: null,
    unscopedSections: [],
    usageByApp: [{ label: 'Teams', value: 1200 }, { label: 'Word', value: 400 }],
    opportunityByDepartment: [{ label: 'Finance', value: 6 }, { label: 'Sales', value: 3 }],
    weeklyTrend: [{ name: 'Active users', points: [{ weekStart: '2026-01-05T00:00:00Z', value: 72 }] }],
    weeklyVolumeTrend: [{ name: 'Licensed', points: [{ weekStart: '2026-01-05T00:00:00Z', value: 2000 }] }, { name: 'Unlicensed', points: [{ weekStart: '2026-01-05T00:00:00Z', value: 400 }] }],
    scoreProfiles: [{ label: 'Average active user', users: 72, frequencyScore: 50, depthScore: 40, breadthScore: 30 }],
    concentration: [{ label: 'Top 10%', users: 7, interactions: 1200, sharePct: 50, interactionsPerUser: 171 }],
    combinedByDepartment: [{ segment: 'Finance', licensedUsers: 30, licensedActiveUsers: 10, interactionsPerLicensedUser: 4, licensedAgentUserPct: 10, unlicensedActiveUsers: 6, interactionsPerUnlicensedUser: 12, unlicensedAgentUserPct: 20 }],
    topResourceTypes: [{ label: 'docx', value: 80, kind: CopilotResourceTypeKind.TenantContent }],
    agents: { historyDays: 120, activeAgents: 0, knownAgents: 0, customAgents: 0, agentUsers: 0, licensedAgentUsers: 0, agentInteractions: 0, interactionsPerAgentUser: 0, mostPopularAgent: null, mostVersatileAgent: null, healthBreakdown: [], usageByDepartment: [], usageByAgent: [], agents: [] },
    unlicensed: { activeUsers: 14, interactions: 400, interactionsPerUserPerMonth: 28.6, agentUsers: 0, habitBuckets: [], usageByApp: [], usageByDepartment: [], truncated: false },
    options,
    warnings: [],
    figuresIncomplete: false,
    incompleteReasons: [],
    ...overrides,
  };
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchAdoptionAvailability).mockResolvedValue({
    available: true,
    copilotAuditImportEnabled: true,
    copilotUsageReportImportEnabled: true,
    userMetadataImportEnabled: true,
    m365UsageReportImportEnabled: true,
    messages: [],
  });
  vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary());
  vi.mocked(fetchAdoptionFilters).mockResolvedValue({
    emailDomains: ['contoso.com', 'fabrikam.com'],
    departments: ['Finance', 'Sales'],
    countries: ['United Kingdom'],
    bands: [{ value: AdoptionBand.Developing, name: 'Developing' }],
  });
  vi.mocked(fetchAdoptionSql).mockResolvedValue({
    licensedUsers: 'select * from LicensedUsers',
    licenceOpportunities: 'select * from Opportunities',
    usageByApp: 'select * from UsageByApp',
    resourceTypes: 'select * from ResourceTypes',
    weeklyTrend: 'select * from WeeklyTrend',
  });
  vi.mocked(fetchLicensedUsers).mockResolvedValue({ total: 0, skip: 0, take: 50, rows: [], warnings: [] });
});

async function renderPage() {
  renderWithProvider(<CopilotAdoptionPage />);
  await screen.findByRole('tab', { name: 'Executive view', selected: true });
}

describe('CopilotAdoptionPage view split', () => {
  it('opens on an executive view with the three acts and no SQL popovers', async () => {
    await renderPage();

    expect((await screen.findAllByText('Where we stand'))[0]).toBeVisible();
    expect(screen.getByText('Where it is working and failing')).toBeVisible();
    expect(screen.getByText('What we are doing about it')).toBeVisible();
    expect(screen.getByText('Department league table')).toBeVisible();
    expect(screen.getByText('Finance')).toBeVisible();
    expect(screen.getByText('Support')).toBeVisible();
    expect(screen.queryByText('The shape of adoption')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'SQL' })).not.toBeInTheDocument();
  });

  it('keeps every executive headline available in the Analyst view and puts SQL there', async () => {
    await renderPage();
    await screen.findAllByText('Where we stand');

    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));

    expect(await screen.findByRole('tab', { name: 'Analyst view', selected: true })).toBeVisible();
    expect(screen.getByText('Copilot licences')).toBeVisible();
    expect(screen.getAllByText('Adoption rate')[0]).toBeVisible();
    expect(screen.getAllByText('Habitual users')[0]).toBeVisible();
    expect(screen.getByText('Reclaimable licences')).toBeVisible();
    expect(screen.getByText('Recommended for a licence')).toBeVisible();
    expect(screen.getByText('The shape of adoption')).toBeVisible();
    expect(screen.getByText('Usage frequency and intensity')).toBeVisible();
    expect(screen.getAllByRole('button', { name: 'SQL' }).length).toBeGreaterThanOrEqual(5);
  });

  it('drills the executive enablement plan through using the action code counted by the aggregate', async () => {
    await renderPage();
    await screen.findAllByText('Where we stand');

    fireEvent.click(screen.getByRole('button', { name: /Coach.*Show these people/ }));

    expect(await screen.findByRole('tab', { name: 'Licensed users', selected: true })).toBeVisible();
    await waitFor(() => expect(fetchLicensedUsers).toHaveBeenCalled());
    const filters = vi.mocked(fetchLicensedUsers).mock.calls.at(-1)?.[1];
    expect(filters?.actions).toEqual(['coach']);
  });

  it('uses the designed executive empty state instead of an empty grid of diagnostics', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      licensedUsers: 0,
      scoredUsers: 0,
      activeUsers: 0,
      adoptionRatePct: 0,
      habitualUsers: 0,
      habitRatePct: 0,
      reclaimableSeats: 0,
      recommendedForLicence: 0,
      actionPlan: [],
      adoptionByDepartment: [],
      habitByDepartment: [],
      funnel: [],
      bandBreakdown: [],
    }));

    await renderPage();

    expect(await screen.findByText('No Copilot licences found')).toBeVisible();
    expect(screen.getByText(/Licence data has not been imported yet/)).toBeVisible();
    expect(screen.queryByRole('button', { name: 'SQL' })).not.toBeInTheDocument();
  });
});


describe('CopilotAdoptionPage email-domain filter', () => {
  it('compares the organisations sharing the tenant on the executive view', async () => {
    await renderPage();

    const card = (await screen.findByText('Adoption by email domain')).closest('div')?.parentElement
      ?.parentElement as HTMLElement;

    // Scoped to the card: the domain names also appear in the filter drop-down above it.
    expect(within(card).getByText('fabrikam.com')).toBeVisible();
    expect(within(card).getByText('contoso.com')).toBeVisible();
    expect(within(card).getByText('Licence candidates')).toBeVisible();
  });

  it('offers the domain filter only when the tenant actually has more than one domain', async () => {
    // On a single-domain tenant a drop-down with one real choice reads as a missing feature.
    vi.mocked(fetchAdoptionFilters).mockResolvedValue({
      emailDomains: ['contoso.com'],
      departments: [],
      countries: [],
      bands: [],
    });

    await renderPage();
    await screen.findAllByText('Where we stand');

    expect(screen.queryByLabelText('Email domain')).not.toBeInTheDocument();
  });

  it('re-requests every figure from the server when a domain is chosen', async () => {
    // The point of the feature: the numbers are RECOMPUTED for the domain, not merely hidden. If
    // this ever became a client-side filter the headline KPIs would keep describing the tenant.
    await renderPage();

    const picker = await screen.findByLabelText('Email domain');
    fireEvent.change(picker, { target: { value: 'fabrikam.com' } });

    await waitFor(() =>
      expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenCalledWith(
        28,
        undefined,
        expect.anything(),
        'previousPeriod',
        'fabrikam.com',
      ),
    );
  });

  it('says on screen which population every figure describes, and names what stayed tenant-wide', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({
        scopedEmailDomain: 'fabrikam.com',
        unscopedSections: ['usageByApp', 'weeklyTrend', 'agents'],
      }),
    );

    await renderPage();

    expect(await screen.findByText(/Showing fabrikam.com only/)).toBeVisible();
    expect(
      screen.getByText(
        /Copilot use by app \(licensed and unlicensed\), the weekly trend and the agent inventory stay tenant-wide/,
      ),
    ).toBeVisible();
  });

  it('lets the reader get back to the whole tenant from the banner', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({ scopedEmailDomain: 'fabrikam.com' }));

    await renderPage();

    fireEvent.click(await screen.findByText('Show all domains'));

    await waitFor(() =>
      expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
        28,
        undefined,
        expect.anything(),
        'previousPeriod',
        null,
      ),
    );
  });

  it('never shows the scope banner for a tenant-wide report', async () => {
    await renderPage();
    await screen.findAllByText('Where we stand');

    expect(screen.queryByText(/Showing .* only/)).not.toBeInTheDocument();
  });

  it('survives a server that does not send the domain fields at all', async () => {
    // During a rolling deploy the SPA can be newer than the API answering it. A missing array must
    // hide one card, not take the whole page down.
    const older = summary();
    delete (older as Partial<CopilotAdoptionSummary>).emailDomains;
    delete (older as Partial<CopilotAdoptionSummary>).unscopedSections;
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(older);

    await renderPage();

    expect((await screen.findAllByText('Where we stand'))[0]).toBeVisible();
    expect(screen.queryByText('Adoption by email domain')).not.toBeInTheDocument();
  });
});
describe('CopilotAdoptionPage domain-scope regressions', () => {
  it('does not mistake an unlicensed organisation for an unconfigured tenant', async () => {
    // A domain with unlicensed Copilot Chat users but no seats of its own is exactly what this view
    // exists to find. Telling the reader who just went looking for it that "licence data has not
    // been imported" is both wrong and the opposite of the finding.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({
        scopedEmailDomain: 'northwind.example',
        licensedUsers: 0,
        scoredUsers: 0,
        activeUsers: 0,
        unlicensedActiveUsers: 14,
        recommendedForLicence: 9,
      }),
    );

    await renderPage();

    expect(await screen.findByText(/Showing northwind.example only/)).toBeVisible();
    expect(screen.queryByText('No Copilot licences found')).not.toBeInTheDocument();
  });

  it('still shows the first-run screen for a genuinely unconfigured tenant', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({ scopedEmailDomain: null, licensedUsers: 0, scoredUsers: 0, activeUsers: 0 }),
    );

    await renderPage();

    expect(await screen.findByText('No Copilot licences found')).toBeVisible();
  });

  it('freezes an intervention cohort against the domain the count was shown for', async () => {
    // The action count clicked is the SCOPED count, so the persisted cohort has to contain exactly
    // the people that count described - not every matching user in the tenant.
    vi.mocked(createInterventionFromAction).mockResolvedValue({
      interventionId: 1,
      cohortId: 2,
      memberCount: 40,
    } as Awaited<ReturnType<typeof createInterventionFromAction>>);

    await renderPage();

    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({ scopedEmailDomain: 'fabrikam.com' }));
    fireEvent.change(await screen.findByLabelText('Email domain'), {
      target: { value: 'fabrikam.com' },
    });
    await screen.findByText(/Showing fabrikam.com only/);

    fireEvent.click((await screen.findAllByRole('button', { name: 'Start intervention' }))[0]);

    await waitFor(() => expect(vi.mocked(createInterventionFromAction)).toHaveBeenCalled());

    const call = vi.mocked(createInterventionFromAction).mock.calls[0];
    expect(call[0]).toBe(28);
    expect(call[1]).toEqual(expect.objectContaining({ actionCode: expect.any(String) }));
    expect(call[3]).toBe('fabrikam.com');
  });
});