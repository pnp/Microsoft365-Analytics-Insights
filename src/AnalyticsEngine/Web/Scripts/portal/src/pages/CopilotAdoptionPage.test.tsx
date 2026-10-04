import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, waitFor, fireEvent, within } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import CopilotAdoptionPage, { lastCalendarMonthRange, lastCalendarQuarterRange } from './CopilotAdoptionPage';
import {
  fetchAdoptionAvailability,
  fetchAdoptionFilters,
  fetchAdoptionSql,
  fetchAdoptionSummary,
  fetchLicensedUsers,
} from '../api/copilotAdoptionApi';
import { AdoptionBand, CopilotResourceTypeKind, type CopilotAdoptionOptions, type CopilotAdoptionSummary } from '../types/copilotAdoption';
import type { UserFilterClause, UserFilterEcho } from '../types/userFilter';
import { TIME_SAVED_STORAGE_KEY, resetTimeSavedStore } from '../components/copilotAdoption/coworkTimeSaved';
import { TIME_SAVED_COHORT_STORAGE_KEY, resetTimeSavedCohortStore } from '../components/copilotAdoption/timeSavedCohort';
import { loadCatalog } from '../i18n';

vi.mock('../api/copilotAdoptionApi', async (importOriginal) => ({
  ...await importOriginal<typeof import('../api/copilotAdoptionApi')>(),
  fetchAdoptionAvailability: vi.fn(),
  fetchAdoptionFilters: vi.fn(),
  fetchAdoptionSql: vi.fn(),
  fetchAdoptionSummary: vi.fn(),
  fetchLicensedUsers: vi.fn(),
}));

vi.mock('../api/userFilterApi', () => ({
  fetchUserFilterDimensions: vi.fn(async () => ({
    people: 200,
    loadedUtc: '2026-01-05T00:00:00Z',
    dimensions: [
      { key: 'department', kind: 'entra', name: null, orgTypeId: null, distinctValues: 3, peopleWithValue: 180, supportsTextMatch: true, fixedValues: false },
      { key: 'emailDomain', kind: 'entra', name: null, orgTypeId: null, distinctValues: 2, peopleWithValue: 200, supportsTextMatch: true, fixedValues: false },
      { key: 'org:4', kind: 'custom', name: 'Cost centre', orgTypeId: 4, distinctValues: 12, peopleWithValue: 150, supportsTextMatch: true, fixedValues: false },
    ],
  })),
  fetchUserFilterValues: vi.fn(async () => ({ dimension: 'department', values: [], totalMatching: 0, truncated: false, peopleWithoutValue: 0 })),
}));

/** The filter the server says it applied, as it echoes it on a narrowed summary. */
function echo(clauses: UserFilterClause[], matchedPeople = 40, dimensionNames: Record<string, string> = {}): UserFilterEcho {
  return { clauses, matchedPeople, directoryPeople: 200, unknownDimensions: [], dimensionNames };
}

const FABRIKAM_ONLY: UserFilterClause = {
  join: 'and',
  dimension: 'emailDomain',
  operator: 'is',
  values: ['fabrikam.com'],
  includeNotSet: false,
};

/** Narrows the report to one domain the way a reader does: from the domain table's own button. */
async function filterToDomain(domain: string) {
  const card = (await screen.findByText('Adoption by email domain')).closest('div')?.parentElement
    ?.parentElement as HTMLElement;
  const row = within(card).getByText(domain).closest('tr') as HTMLElement;
  fireEvent.click(within(row).getByRole('button', { name: 'Filter' }));
}

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
  activationWindowDays: 30,
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
  copilotMinutesSavedPerMeeting: 10,
  copilotMinutesSavedPerMailThread: 5,
  copilotMinutesSavedPerDocument: 8,
  coworkEstimateLowerBoundRatio: 0.5,
  copilotSeatOutlookMinutesPerAction: 6,
  copilotSeatOfficeMinutesPerAction: 6,
  copilotSeatMeetingMinutesPerAction: 30,
  copilotSeatUncreditedMinutesPerAction: 0,
  coworkOrganiseMeetingsShare: 0.25,
  coworkOrganiseMeetingsMinutes: 6,
  coworkPrepareMeetingsShare: 0.1,
  coworkPrepareMeetingsMinutes: 6,
  coworkSendEmailShare: 0.05,
  coworkSendEmailMinutes: 6,
  coworkPostInTeamsShare: 0.01,
  coworkPostInTeamsMinutes: 6,
  coworkCreateDocumentsShare: 0.02,
  coworkCreateDocumentsMinutes: 6,
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

function summary(overrides: Partial<CopilotAdoptionSummary> = {}): CopilotAdoptionSummary {
  return {
    generatedUtc: '2026-01-01T00:00:00Z',
    windowDays: 28,
    fromUtc: '2025-12-05T00:00:00Z',
    toUtc: '2026-01-01T00:00:00Z',
    dataSources: {
      auditAvailable: true,
      copilotUsageReportAvailable: true,
      coworkUsageReportAvailable: true,
      m365UsageReportsAvailable: true,
      userMetadataAvailable: true,
      copilotUsageReportDate: '2026-01-01T00:00:00Z',
      copilotUsageReportPeriodDays: 28,
      coworkUsageReportDate: '2026-01-01T00:00:00Z',
      coworkUsageReportPeriodDays: 28,
      m365UsageReportDate: '2026-01-01T00:00:00Z',
      copilotUsageReportObfuscated: false,
    },
    seatLicenceTypes: [{ id: 1, name: 'Microsoft 365 Copilot', skuPartNumber: 'M365_COPILOT', assignedUsers: 120, isCopilotSeat: true, purchasedUnits: 120, unassignedUnits: 0, assignedIdleUsers: 0, purchasedUnitsRefreshedUtc: null }],
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
    reclaimSeatsNoLongerHeld: 0,
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
    coworkValueEstimate: { isModelled: false, cohortUsers: 0, activities: [], projectedCoworkTasks: 0, hoursPerMonthLow: 0, hoursPerMonthHigh: 0, assumptions: [] },
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
  } as CopilotAdoptionSummary;
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

describe('CopilotAdoptionPage custom ranges', () => {
  it('calculates calendar presets across month lengths and January', () => {
    expect(lastCalendarMonthRange(new Date(Date.UTC(2026, 2, 15)))).toEqual({ from: '2026-02-01', to: '2026-02-28' });
    expect(lastCalendarMonthRange(new Date(Date.UTC(2026, 0, 15)))).toEqual({ from: '2025-12-01', to: '2025-12-31' });
    expect(lastCalendarQuarterRange(new Date(Date.UTC(2026, 0, 15)))).toEqual({ from: '2025-10-01', to: '2025-12-31' });
  });

  it('sends custom ranges to the API and clears them when a rolling preset is chosen', async () => {
    await renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Last calendar month' }));
    expect(screen.getByLabelText('Reporting period')).toHaveValue('custom');
    fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '28' } });
    await waitFor(() => expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
      28,
      undefined,
      expect.any(AbortSignal),
      null,
      null,
    ));

    fireEvent.click(screen.getByRole('button', { name: 'Last calendar month' }));
    await waitFor(() => expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
      28,
      undefined,
      expect.any(AbortSignal),
      null,
      null,
      { ...lastCalendarMonthRange() },
    ));

    fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '90' } });
    await waitFor(() => expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
      90,
      undefined,
      expect.any(AbortSignal),
      null,
      null,
    ));
  });

  it('hides named action-list panels for past ranges while the print header keeps the range', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      fromUtc: '2026-09-01T00:00:00Z',
      toUtc: '2026-09-30T00:00:00Z',
      options: { ...options, usesExplicitDates: true, fromUtc: '2026-09-01T00:00:00Z', toUtc: '2026-09-30T00:00:00Z', toExclusiveUtc: '2026-10-01T00:00:00Z' },
      seatHolderTimeSavedEstimate: {
        isModelled: true,
        cohortUsers: 12,
        excludedUsageReportSourcedUsers: 0,
        observedOutlookActions: 10,
        observedOfficeActions: 5,
        observedTeamsMeetingActions: 1,
        observedUncreditedActions: 0,
        credits: { outlookMinutesPerAction: 6, officeMinutesPerAction: 6, teamsMeetingMinutesPerAction: 30, uncreditedMinutesPerAction: 0, lowerBoundRatio: 0.5 },
        hoursPerMonthLow: 1,
        hoursPerMonthHigh: 2,
        assumptions: [],
        byBand: [],
        byDepartment: [],
      },
    }));
    await renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Last calendar month' }));
    fireEvent.click(screen.getByRole('tab', { name: 'Licensed users' }));
    expect(await screen.findByText("Seat holders' time saved (modelled)")).toBeVisible();
    expect(await screen.findByText(/named reclaim and recommendation lists/i)).toBeInTheDocument();
    expect(screen.getByText(/1 Sept? 2026/i)).toBeInTheDocument();
    expect(screen.getByText(/30 Sept? 2026/i)).toBeInTheDocument();
  });
});

describe('CopilotAdoptionPage view split', () => {
  it('translates unavailable availability reasons instead of rendering server English', async () => {
    vi.mocked(fetchAdoptionAvailability).mockResolvedValue({
      available: false,
      copilotAuditImportEnabled: false,
      copilotUsageReportImportEnabled: false,
      userMetadataImportEnabled: false,
      m365UsageReportImportEnabled: true,
      messages: ['SERVER: user metadata off', 'SERVER: sources off'],
    });

    // Loaded first, as main.tsx does before the first render. Otherwise the assertion races the
    // Spanish chunk's dynamic import, which under a loaded parallel run outlasts waitFor's 1s default.
    await loadCatalog('es');
    renderWithProvider(<CopilotAdoptionPage />, { language: 'es' });

    await waitFor(() => expect(screen.getByText(/La importación de metadatos de usuario está deshabilitada/)).toBeVisible());
    expect(screen.getByText(/No está habilitada ni la importación de auditoría de Copilot ni la importación del informe de uso de Copilot/)).toBeVisible();
    expect(screen.queryByText('SERVER: user metadata off')).toBeNull();
    expect(screen.queryByText('SERVER: sources off')).toBeNull();
  });

  it('names the datasets that could not be loaded in Spanish, and exactly as the server wrote them in English', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      figuresIncomplete: true,
      incompleteReasons: ['licence types', 'Cowork readiness', 'a dataset this build does not know'],
    }));
    await loadCatalog('es');
    const spanish = renderWithProvider(<CopilotAdoptionPage />, { language: 'es' });

    await waitFor(() => expect(document.body.textContent)
      .toContain('tipos de licencia, preparación para Cowork, a dataset this build does not know. '));
    expect(document.body.textContent).not.toContain('licence types, Cowork readiness');
    spanish.unmount();

    renderWithProvider(<CopilotAdoptionPage />);
    await waitFor(() => expect(document.body.textContent)
      .toContain('licence types, Cowork readiness, a dataset this build does not know. '));
  });

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

  it('shows former seat holders as their own reclaim reconciliation term', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      reclaimSeatsHeldBackForReview: 0,
      reclaimSeatsNoLongerHeld: 2,
    }));

    await renderPage();
    const card = (await screen.findByText('Reclaimable licences')).closest('.fui-Card') as HTMLElement;

    fireEvent.click(within(card).getByRole('button', { name: /How "Reclaimable licences" is calculated/ }));

    expect(await screen.findByText(/2 no longer hold a seat/)).toBeInTheDocument();
    expect(screen.queryByText(/2 held back for review or exclusion/)).toBeNull();
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

  it('keeps readiness for Cowork on the analyst gauges when the adoption percentage is unavailable', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      // Cowork eligibility is a spending-policy scope nothing in the import can see, so the adoption
      // percentage is suppressed on most tenants. That must not take the readiness gauge with it -
      // readiness is a share of the seat holders this tool scored, and needs no such denominator.
      coworkDetected: true,
      coworkAdoptionPct: null,
      coworkReadinessAvailable: true,
      coworkScoredUsers: 80,
      coworkRecommendedForPolicy: 60,
    }));

    await renderPage();
    await screen.findAllByText('Where we stand');
    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));

    expect(await screen.findByRole('tab', { name: 'Analyst view', selected: true })).toBeVisible();
    expect(screen.getByRole('img', { name: 'Readiness for Cowork: 75%' })).toBeVisible();
    expect(
      screen.getByText('60 of 80 scored seat holders are ready to be scoped for Cowork'),
    ).toBeVisible();
    expect(screen.queryByText('Cowork adoption')).not.toBeInTheDocument();
  });

  it('shows no readiness gauge at all when Cowork readiness could not be assessed', async () => {
    await renderPage();
    await screen.findAllByText('Where we stand');
    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));

    expect(await screen.findByRole('tab', { name: 'Analyst view', selected: true })).toBeVisible();
    expect(screen.queryByText('Readiness for Cowork')).not.toBeInTheDocument();
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

  it('narrows the whole report through the page-wide filter, not a separate domain drop-down', async () => {
    // Email domain is one attribute of the single page-wide filter now, alongside department, country
    // and every custom organisation. A second, domain-only control would be two answers to "who is
    // this report about?" - and nothing stopping them disagreeing.
    await renderPage();
    await screen.findAllByText('Where we stand');

    expect(screen.queryByLabelText('Email domain')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Add filter' })).toBeVisible();
    expect(screen.getByText('Showing everyone')).toBeVisible();
  });

  it('re-requests every figure from the server when a domain is chosen', async () => {
    // The point of the feature: the numbers are RECOMPUTED for the domain, not merely hidden. If
    // this ever became a client-side filter the headline KPIs would keep describing the tenant.
    await renderPage();

    await filterToDomain('fabrikam.com');

    await waitFor(() =>
      expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenCalledWith(
        28,
        undefined,
        expect.anything(),
        null,
        '[{"d":"emailDomain","v":["fabrikam.com"]}]',
      ),
    );
    // ...and the choice shows up as a condition in the filter bar, where it can be edited or removed.
    expect(await screen.findByRole('button', { name: 'Email domain is fabrikam.com' })).toBeVisible();
  });

  it('says on screen which population every figure describes, and names what stayed tenant-wide', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({
        userFilter: echo([FABRIKAM_ONLY]),
        unscopedLicensedUsers: 400,
        unscopedSections: ['usageByApp', 'weeklyTrend', 'agents'],
      }),
    );

    await renderPage();

    const banner = (await screen.findByText('Filtered:')).parentElement as HTMLElement;
    expect(banner).toBeVisible();
    expect(banner.textContent).toContain('Email domain is fabrikam.com.');
    expect(banner.textContent).toContain('cover 120 of the tenant’s 400 Copilot licence holders');
    expect(banner.textContent).toMatch(
      /Copilot use by app \(licensed and unlicensed\), the weekly trend and the agent inventory stay tenant-wide/,
    );
  });

  it('lets the reader get back to the whole tenant from the banner', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({ userFilter: echo([FABRIKAM_ONLY]) }));

    await renderPage();
    await filterToDomain('fabrikam.com');
    await waitFor(() =>
      expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
        28,
        undefined,
        expect.anything(),
        null,
        '[{"d":"emailDomain","v":["fabrikam.com"]}]',
      ),
    );

    fireEvent.click(await screen.findByText('Clear filter'));

    await waitFor(() =>
      expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
        28,
        undefined,
        expect.anything(),
        null,
        null,
      ),
    );
  });

  it('never shows the scope banner for a tenant-wide report', async () => {
    await renderPage();
    await screen.findAllByText('Where we stand');

    expect(screen.queryByText('Filtered:')).not.toBeInTheDocument();
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
        userFilter: echo([{ ...FABRIKAM_ONLY, values: ['northwind.example'] }]),
        licensedUsers: 0,
        scoredUsers: 0,
        activeUsers: 0,
        unlicensedActiveUsers: 14,
        recommendedForLicence: 9,
      }),
    );

    await renderPage();

    const banner = (await screen.findByText('Filtered:')).parentElement as HTMLElement;
    expect(banner.textContent).toContain('Email domain is northwind.example.');
    expect(screen.queryByText('No Copilot licences found')).not.toBeInTheDocument();
  });

  it('treats a filter that matches no seat holders the same way', async () => {
    // "Department is Finance" on a tenant that never licensed Finance is a finding, not a
    // misconfiguration - the first-run screen would tell the reader the opposite.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({
        userFilter: echo([{ join: 'and', dimension: 'department', operator: 'is', values: ['Finance'], includeNotSet: false }]),
        licensedUsers: 0,
        scoredUsers: 0,
        activeUsers: 0,
      }),
    );

    await renderPage();

    const banner = (await screen.findByText('Filtered:')).parentElement as HTMLElement;
    expect(banner.textContent).toContain('Department is Finance.');
    expect(screen.queryByText('No Copilot licences found')).not.toBeInTheDocument();
  });

  it('still shows the first-run screen for a genuinely unconfigured tenant', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({ scopedEmailDomain: null, licensedUsers: 0, scoredUsers: 0, activeUsers: 0 }),
    );

    await renderPage();

    expect(await screen.findByText('No Copilot licences found')).toBeVisible();
  });

  it('offers no in-product period comparison, because comparison is done by diffing two exports', async () => {
    // Comparison was removed from the product: there is no stored history to compare against, so a
    // "Compare" control would promise something the server cannot answer. The Excel export is the
    // comparison surface.
    await renderPage();
    await screen.findByLabelText('Reporting period');

    expect(screen.queryByLabelText('Comparison period')).toBeNull();
    expect(screen.queryByText('Compare')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Start intervention' })).toBeNull();
  });
});

/**
 * Printing.
 *
 * The printed report used to be whatever the screen happened to be: a menu down the left, a brand
 * bar across the top and the report itself squeezed into the remaining third of the sheet. The fix
 * is a `data-print` contract between the components and the `@media print` block in index.css
 * (the stylesheet half is proved by printStyles.test.ts). What these tests assert is the half the
 * components own: the chrome is marked as chrome, the report is not, and nothing the printout
 * needs in order to be identifiable disappears along with the controls.
 */
describe('CopilotAdoptionPage printing', () => {
  it('prints the report when the Print button is used', async () => {
    const print = vi.spyOn(window, 'print').mockImplementation(() => {});
    await renderPage();

    fireEvent.click(screen.getByRole('button', { name: 'Print' }));

    expect(print).toHaveBeenCalledTimes(1);
  });

  it('marks the app-level chrome, the filter controls and the tab strip as print-hidden', async () => {
    await renderPage();
    // Waits for the loaded state on purpose. Without it this asserted against the loading state by
    // accident, and then read the Excel button's role from it: Fluent renders that button as
    // `<a role="button" aria-disabled>` while there is no summary to export, and as a plain
    // `<a href>` - role `link`, not `button` - once there is. It passed locally and failed in CI
    // purely on which side of that swap the machine happened to be.
    await screen.findAllByText('Where we stand');

    // The Print button itself has to go too: a printed page carrying a button to print it is the
    // giveaway that the printout is a screenshot of an app rather than a report.
    expect(screen.getByRole('button', { name: 'Print' }).closest('[data-print="hide"]')).not.toBeNull();
    expect(screen.getByLabelText('Reporting period').closest('[data-print="hide"]')).not.toBeNull();
    // By text, not by role, so this stays true either side of that swap.
    expect(screen.getByText('Excel report').closest('[data-print="hide"]')).not.toBeNull();
    expect(screen.getByRole('tab', { name: 'Executive view' }).closest('[data-print="hide"]')).not.toBeNull();
  });

  it('keeps the report itself out of the print-hidden chrome', async () => {
    // The obvious way to get this wrong is to hide a container that also holds the report. The
    // title and the figures have to survive - they are the printout.
    await renderPage();
    await screen.findAllByText('Where we stand');

    expect(screen.getByText('Copilot Adoption').closest('[data-print="hide"]')).toBeNull();
    expect(screen.getAllByText('Where we stand')[0].closest('[data-print="hide"]')).toBeNull();
  });

  it('replaces the hidden controls with a caption naming the view, period and scope', async () => {
    // With the tab strip and the filter controls gone, a printed sheet would otherwise say only
    // "Copilot Adoption" - and which of eight views, over which of four periods, for which domain
    // is not something a reader can reconstruct from the figures.
    await renderPage();
    await screen.findAllByText('Where we stand');

    const caption = document.querySelector('[data-print="only"]');
    expect(caption).not.toBeNull();
    expect(caption?.textContent).toContain('Executive view');
    expect(caption?.textContent).toContain('Last 28 days');
    expect(caption?.textContent).toContain('Everyone');
    // Dated, because a printout outlives the period it describes.
    expect(caption?.textContent).toContain('Generated');
  });

  it('says in plain words, on paper, who the report covers', async () => {
    // Printouts go to executives. With the filter bar hidden like every other control, the sheet has
    // to say who it is about in a sentence anyone can read - not in the bar's shorthand.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({
        userFilter: echo(
          [
            { join: 'and', dimension: 'department', operator: 'is', values: ['Sales', 'Marketing'], includeNotSet: false },
            { join: 'and', dimension: 'org:4', operator: 'isNot', values: ['CC-100'], includeNotSet: false },
          ],
          37,
          { 'org:4': 'Cost centre' },
        ),
      }),
    );

    await renderPage();
    await screen.findAllByText('Where we stand');

    const printed = Array.from(document.querySelectorAll('[data-print="only"]'))
      .map((e) => e.textContent ?? '')
      .join(' ');
    expect(printed).toContain('Who this report covers');
    expect(printed).toContain('Only people who match all of these:');
    expect(printed).toContain('Department is Sales or Marketing');
    expect(printed).toContain('Cost centre is not CC-100');
    expect(printed).toContain('37 of the 200 people in the directory match this filter.');
  });

  it('follows the tab and the domain the reader actually chose', async () => {
    await renderPage();

    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));
    expect(await screen.findByRole('tab', { name: 'Analyst view', selected: true })).toBeVisible();
    await filterToDomain('fabrikam.com');

    await waitFor(() => {
      const caption = document.querySelector('[data-print="only"]');
      expect(caption?.textContent).toContain('Analyst view');
      expect(caption?.textContent).toContain('Filtered');
      expect(document.body.textContent).toContain('Only people where Email domain is fabrikam.com.');
    });
  });

  it('names every tab the same way in the strip and in the caption', async () => {
    // The caption is the only thing naming the view on paper, so a tab renamed in one place but
    // not the other would silently mislabel every printout of it.
    await renderPage();

    // Fluent renders an unselected tab's label twice: a visible span, plus a second, CSS-hidden
    // one reserving the width of the bold selected state. So textContent reads "AgentsAgents",
    // while the first span holds the label - and is what getByRole's accessible-name matching
    // sees, the hidden twin being excluded from the accessible name.
    const tabLabel = (tab: HTMLElement) => (tab.querySelector('span')?.textContent ?? '').trim();

    const labels = screen.getAllByRole('tab').map(tabLabel);
    expect(labels.length).toBe(8);

    for (const label of labels) {
      fireEvent.click(screen.getByRole('tab', { name: label }));
      await waitFor(() =>
        expect(document.querySelector('[data-print="only"]')?.textContent).toContain(label),
      );
    }
  });
});

/**
 * Dismissible data warnings.
 *
 * The bar carries caveats about the data behind the report - an import that has not run, a Graph
 * permission that was never granted. They are true, they are worth saying once, and on a tenant
 * that is not going to fix them today they are also the same orange block above the report on
 * every visit, which is how readers learn to skim past everything at the top of the page.
 *
 * So the bar can be put away - but never lost, and never silently inherited by a different
 * warning. Dismissing applies to the printout too: the control would otherwise lie to someone who
 * put the warnings away and then printed.
 */
describe('CopilotAdoptionPage data warnings', () => {
  const WARNINGS = [
    'Purchased and unassigned Copilot seats are unknown because Graph subscribedSkus has not been imported.',
    'The Microsoft 365 usage reports are not available, so coordination load cannot be measured.',
  ];

  it('shows the warnings, and lets the reader put them away', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({ warnings: WARNINGS }));
    await renderPage();

    expect(await screen.findByText(WARNINGS[0])).toBeVisible();

    fireEvent.click(screen.getByRole('button', { name: 'Hide these warnings' }));

    // Gone from the screen, but the count is still on offer: a reader must never be unaware that
    // there are caveats, only free to stop looking at them.
    await waitFor(() => expect(screen.getByRole('button', { name: /Show 2 data warnings/ })).toBeVisible());
    expect(screen.queryByText(WARNINGS[0])).not.toBeInTheDocument();
  });

  it('brings them straight back', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({ warnings: WARNINGS }));
    await renderPage();
    await screen.findByText(WARNINGS[0]);

    fireEvent.click(screen.getByRole('button', { name: 'Hide these warnings' }));
    fireEvent.click(await screen.findByRole('button', { name: /Show 2 data warnings/ }));

    expect(await screen.findByText(WARNINGS[0])).toBeVisible();
    expect(screen.queryByRole('button', { name: /Show 2 data warnings/ })).not.toBeInTheDocument();
  });

  it('shows itself again when the warnings are not the ones that were dismissed', async () => {
    // The dismissal is keyed on the warnings, not on the bar. Otherwise putting away "Cowork is
    // not imported" would also swallow a brand-new problem that appears after changing the scope -
    // which is exactly the moment a new warning is most likely and most worth reading.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({ warnings: WARNINGS }));
    await renderPage();
    await screen.findByText(WARNINGS[0]);
    fireEvent.click(screen.getByRole('button', { name: 'Hide these warnings' }));
    await screen.findByRole('button', { name: /Show 2 data warnings/ });

    const different = 'Copilot audit events stop three days before the end of the period.';
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({ userFilter: echo([FABRIKAM_ONLY]), warnings: [different] }),
    );
    await filterToDomain('fabrikam.com');

    expect(await screen.findByText(different)).toBeVisible();
    expect(screen.queryByRole('button', { name: /Show 1 data warning/ })).not.toBeInTheDocument();
  });

  it('leaves them off the printout too, because the reader said to put them away', async () => {
    // This deliberately reversed: the bar used to survive on paper on the reasoning that a printed
    // report is read by people who cannot check what it left out. But that makes the control lie -
    // someone who hides the warnings and prints has said what they want on the page. Leaving the
    // bar up is how you print it.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({ warnings: WARNINGS }));
    await renderPage();
    await screen.findByText(WARNINGS[0]);

    fireEvent.click(screen.getByRole('button', { name: 'Hide these warnings' }));
    await screen.findByRole('button', { name: /Show 2 data warnings/ });

    // Nothing left anywhere in the document - not on screen, and not in a print-only copy.
    expect(document.body.textContent).not.toContain(WARNINGS[0]);
    // ...and the button that replaced them is itself chrome, so the printout carries neither.
    expect(screen.getByRole('button', { name: /Show 2 data warnings/ })).toHaveAttribute('data-print', 'hide');
  });
});

/**
 * Page breaks.
 *
 * A printed act used to be split from the thing it introduces: the heading landed at the foot of
 * one page and every card it announces started on the next, so the sheet ended with a title and
 * nothing under it. Each act now starts its own sheet.
 */
describe('CopilotAdoptionPage page breaks', () => {
  /** The numbered act headings, in document order, with the break each one carries. */
  const acts = () =>
    [...document.querySelectorAll('[data-print="page-break"], [data-print="keep-with-next"]')].map((n) => ({
      print: n.getAttribute('data-print'),
      text: (n.querySelector('span')?.textContent ?? '') + (n.textContent ?? ''),
    }));

  it('starts every act after the first on a new sheet', async () => {
    await renderPage();
    await screen.findAllByText('Where we stand');

    // The executive view is three acts. The first keeps with what follows but does not break: it
    // belongs with the KPI tiles on the report's front page, which would otherwise print alone.
    expect(acts().map((a) => a.print)).toEqual(['keep-with-next', 'page-break', 'page-break']);
    expect(acts()[0].text).toContain('Where we stand');
    expect(acts()[2].text).toContain('What we are doing about it');
  });

  it('does the same for the four acts of the analyst view', async () => {
    await renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));
    await screen.findByRole('tab', { name: 'Analyst view', selected: true });

    await waitFor(() =>
      expect(acts().map((a) => a.print)).toEqual([
        'keep-with-next',
        'page-break',
        'page-break',
        'page-break',
      ]),
    );
  });

  it('puts those headings in block flow, without which the break does nothing', async () => {
    // Fragmentation inside a flex container is unreliable across browsers, and the acts are flex
    // items of a column stack - so `break-before` on them is ignored unless the stack itself is
    // returned to block flow for printing. The two halves only work together, hence one assertion.
    await renderPage();
    await screen.findAllByText('Where we stand');

    for (const heading of document.querySelectorAll('[data-print="page-break"]')) {
      expect(heading.parentElement).toHaveAttribute('data-print', 'flow');
    }
  });
});

describe('CopilotAdoptionPage modelled time saved', () => {
  // Under this file's options, not the product defaults: 10 minutes a meeting, 5 an email, 8 a
  // document and 6 a Cowork task, with the conservative end at 50%.
  //
  // Licensing, for the 9 recommended candidates: 2,000 meetings x 10 + 20,000 emails x 5 + 3,000
  // documents x 8 = 144,000 minutes = 2,400 hours, 1,200 at the conservative end.
  const licenceEstimate = {
    isModelled: true,
    cohortUsers: 9,
    addressableMeetings: 2000,
    addressableMailThreads: 20000,
    addressableDocuments: 3000,
    hoursPerMonthLow: 1200,
    hoursPerMonthHigh: 2400,
    candidatesCapped: false,
    assumptions: [],
  };

  // Cowork, for the 20 people ready now, all modelled from their own work (#692): 6,000 emails sent x 5%
  // = 300 handed to Cowork, and nothing else. 300 x 6 minutes = 1,800 minutes = 30 hours, 15
  // conservative.
  const noWork = [
    { activity: 'organiseMeetings' as const, volumePerMonth: 0 },
    { activity: 'prepareMeetings' as const, volumePerMonth: 0 },
    { activity: 'postInTeams' as const, volumePerMonth: 0 },
    { activity: 'createDocuments' as const, volumePerMonth: 0 },
  ];
  const coworkReady = {
    isModelled: true,
    cohortUsers: 20,
    activities: [{ activity: 'sendEmail' as const, volumePerMonth: 6000 }, ...noWork],
    projectedCoworkTasks: 300,
    hoursPerMonthLow: 15,
    hoursPerMonthHigh: 30,
    assumptions: [],
  };

  // The ceiling, all 100 seat holders: 30,000 emails x 5% = 1,500 x 6 minutes = 150 hours, 75
  // conservative.
  const coworkCeiling = {
    ...coworkReady,
    cohortUsers: 100,
    activities: [{ activity: 'sendEmail' as const, volumePerMonth: 30000 }, ...noWork],
    projectedCoworkTasks: 1500,
    hoursPerMonthLow: 75,
    hoursPerMonthHigh: 150,
  };

  // A 200,000-user tenant's 5,000 candidates: 100,000 meetings x 10 + 800,000 emails x 5 + 102,500
  // documents x 8 = 5,820,000 minutes = 97,000 hours, 48,500 conservative.
  const largeTenant: Partial<CopilotAdoptionSummary> = {
    recommendedForLicence: 5000,
    licenceOpportunityEstimate: {
      ...licenceEstimate,
      cohortUsers: 5000,
      addressableMeetings: 100000,
      addressableMailThreads: 800000,
      addressableDocuments: 102500,
      hoursPerMonthLow: 48500,
      hoursPerMonthHigh: 97000,
    },
  };

  const seatHolderEstimate = {
    isModelled: true,
    cohortUsers: 24,
    excludedUsageReportSourcedUsers: 3,
    observedOutlookActions: 100,
    observedOfficeActions: 50,
    observedTeamsMeetingActions: 4,
    observedUncreditedActions: 20,
    credits: { outlookMinutesPerAction: 6, officeMinutesPerAction: 6, teamsMeetingMinutesPerAction: 30, uncreditedMinutesPerAction: 0, lowerBoundRatio: 0.5 },
    hoursPerMonthLow: 9,
    hoursPerMonthHigh: 17,
    assumptions: ['Copilot Chat has no published per-prompt credit and defaults to zero.'],
    byBand: [{ segment: 'Established', cohortUsers: 24, observedOutlookActions: 100, observedOfficeActions: 50, observedTeamsMeetingActions: 4, observedUncreditedActions: 20, hoursPerMonthLow: 9, hoursPerMonthHigh: 17 }],
    byDepartment: [{ segment: 'Finance', cohortUsers: 24, observedOutlookActions: 100, observedOfficeActions: 50, observedTeamsMeetingActions: 4, observedUncreditedActions: 20, hoursPerMonthLow: 9, hoursPerMonthHigh: 17 }],
  };

  const withEstimate = (overrides: Partial<CopilotAdoptionSummary> = {}) =>
    summary({
      coworkReadinessAvailable: true,
      coworkScoredUsers: 100,
      coworkRecommendedForPolicy: 20,
      coworkValueEstimate: coworkReady,
      coworkFullRolloutEstimate: coworkCeiling,
      licenceOpportunityEstimate: licenceEstimate,
      seatHolderTimeSavedEstimate: seatHolderEstimate,
      licenceChatUsersEstimate: {
        ...licenceEstimate,
        cohortUsers: 3,
        addressableMeetings: 600,
        addressableMailThreads: 6000,
        addressableDocuments: 900,
        hoursPerMonthLow: 360,
        hoursPerMonthHigh: 720,
      },
      ...overrides,
    });

  /** The headline tile carrying a label. */
  const tile = async (label: string, timeout?: number) =>
    (await screen.findByText(label, undefined, timeout ? { timeout } : undefined)).closest('.fui-Card') as HTMLElement;

  // Every licence candidate, recommended or not: the 9 recommended and 31 lighter users - 3,000
  // meetings x 10 + 30,000 emails x 5 + 4,500 documents x 8 = 216,000 minutes = 3,600 hours, 1,800 at
  // the conservative end.
  const licenceAllCandidates = {
    ...licenceEstimate,
    cohortUsers: 40,
    addressableMeetings: 3000,
    addressableMailThreads: 30000,
    addressableDocuments: 4500,
    hoursPerMonthLow: 1800,
    hoursPerMonthHigh: 3600,
  };

  beforeEach(() => {
    resetTimeSavedStore();
    resetTimeSavedCohortStore();
  });

  /**
   * The tabs let the reader model every licence candidate, or every Copilot seat holder, instead of the
   * people recommended. The overview must then quote the same people - or it would put one figure on
   * the tile and another on the tab it links to.
   */
  it('quotes every licence candidate on the licensing tile when the reader chose to model them all', async () => {
    sessionStorage.setItem(TIME_SAVED_COHORT_STORAGE_KEY, JSON.stringify({ licence: 'all' }));
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate({ licenceAllCandidatesEstimate: licenceAllCandidates }));

    await renderPage();

    const licence = await tile('Time back from licensing');
    expect(within(licence).getByText('1,800\u20133,600 h')).toBeVisible();
    expect(within(licence).getByText('a month if all 40 licence candidates were licensed')).toBeVisible();
    // The Cowork tile keeps its own choice: the people ready now.
    expect(within(await tile('Time back from Cowork')).getByText('15\u201330 h')).toBeVisible();
  });

  it('quotes every Copilot seat holder on the Cowork tile when the reader chose to model them all', async () => {
    sessionStorage.setItem(TIME_SAVED_COHORT_STORAGE_KEY, JSON.stringify({ cowork: 'all' }));
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate({ licenceAllCandidatesEstimate: licenceAllCandidates }));

    await renderPage();

    const cowork = await tile('Time back from Cowork');
    expect(within(cowork).getByText('75\u2013150 h')).toBeVisible();
    expect(within(cowork).getByText('a month if all 100 Copilot seat holders used Cowork')).toBeVisible();
    expect(within(await tile('Time back from licensing')).getByText('1,200\u20132,400 h')).toBeVisible();
  });

  /**
   * The customer this exists for: nobody uses Microsoft 365 heavily enough to be recommended, and the
   * licensing tile used to vanish. Every candidate stands in, and the tile says whose time it is.
   */
  it('models every licence candidate on the licensing tile when nobody is recommended', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      withEstimate({
        recommendedForLicence: 0,
        licenceOpportunityEstimate: { ...licenceEstimate, cohortUsers: 0, hoursPerMonthLow: 0, hoursPerMonthHigh: 0 },
        licenceChatUsersEstimate: { ...licenceEstimate, cohortUsers: 0, hoursPerMonthLow: 0, hoursPerMonthHigh: 0 },
        licenceAllCandidatesEstimate: licenceAllCandidates,
      }),
    );

    await renderPage();

    const licence = await tile('Time back from licensing');
    expect(within(licence).getByText('1,800\u20133,600 h')).toBeVisible();
    expect(within(licence).getByText('a month if all 40 licence candidates were licensed')).toBeVisible();
    expect(within(licence).queryByText(/recommended people/)).toBeNull();
  });

  /**
   * On a large tenant the candidate list stops at its limit, so "all N candidates" would present a floor
   * as a total - the one surface that did not say so, while the tab and the Excel report both do.
   */
  it('calls a capped candidate list a floor on the tile rather than every candidate', async () => {
    sessionStorage.setItem(TIME_SAVED_COHORT_STORAGE_KEY, JSON.stringify({ licence: 'all' }));
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      withEstimate({ licenceAllCandidatesEstimate: { ...licenceAllCandidates, candidatesCapped: true } }),
    );

    await renderPage();

    const licence = await tile('Time back from licensing');
    expect(
      within(licence).getByText('a month for the first 40 licence candidates - the list stopped at its limit, so treat this as a floor'),
    ).toBeVisible();
    expect(within(licence).queryByText(/if all 40 licence candidates/)).toBeNull();

    // The definition behind the tile says the same, rather than "every licence candidate".
    fireEvent.click(within(licence).getByRole('button', { name: /How "Time back from licensing" is calculated/ }));
    expect(
      await screen.findByText(/^The time Microsoft 365 Copilot could give back each month if the first 40 licence candidates on the list/),
    ).toBeInTheDocument();
    expect(screen.queryByText(/if every licence candidate/)).toBeNull();
  });

  it('shows realised seat-holder time on the executive tile and Licensed users section', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();

    const tileCard = await tile('Time already saved by seat holders');
    expect(within(tileCard).getByText('9\u201317 h')).toBeVisible();
    expect(within(tileCard).getByText('Modelled')).toBeVisible();

    fireEvent.click(screen.getByRole('tab', { name: 'Licensed users' }));
    expect(await screen.findByText("Seat holders' time saved (modelled)")).toBeVisible();
    expect(screen.getByText(/Copilot Chat and other non-agent surfaces default to zero minutes/)).toBeVisible();
    expect(screen.getByText(/Agent and Cowork activity is left out of this figure/)).toBeVisible();
    expect(screen.getByText(/20 other at 0 min/)).toBeVisible();
  });

  it('lets the reader edit a realised-value credit for the session', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Licensed users' }));

    const input = await screen.findByLabelText('Outlook minutes per action');
    fireEvent.change(input, { target: { value: '12' } });

    expect(await screen.findByText(/100 Outlook at 12 min/)).toBeVisible();
    expect(screen.getByText('14\u201327 h')).toBeVisible();
  });

  it('has Spanish text for the realised seat-holder estimate', async () => {
    const es = await loadCatalog('es');
    expect(es['copilotAdoption.page.seatTime.title']).toBe('Tiempo ahorrado por titulares de licencia (modelado)');
    expect(es['copilotAdoption.page.kpi.seatHolderTimeSaved.label']).toBe('Tiempo ya ahorrado por titulares de licencia');
  });

  it('puts each modelled figure on a tile of its own, marked as modelled', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();

    const licence = await tile('Time back from licensing');
    expect(within(licence).getByText('Modelled')).toBeVisible();
    expect(within(licence).getByText('1,200\u20132,400 h')).toBeVisible();
    expect(within(licence).getByText('a month if the 9 recommended people were licensed')).toBeVisible();

    const cowork = await tile('Time back from Cowork');
    expect(within(cowork).getByText('Modelled')).toBeVisible();
    expect(within(cowork).getByText('15\u201330 h')).toBeVisible();
    expect(within(cowork).getByText('a month if the 20 people ready used Cowork')).toBeVisible();
  });

  /**
   * The defect this guards. One "Potential time back" tile used to add Microsoft 365 Copilot's minutes
   * to Cowork's, for people who already hold a licence - so the figure quoted for Copilot Credits was
   * mostly the licence's, and rested on evidence that never measured Cowork. The two figures justify
   * different decisions on different evidence, and a total of them would recreate that blend.
   */
  it('never adds the two figures together, and has no figure for people already licensed', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();
    await tile('Time back from licensing');

    expect(screen.queryByText('Potential time back')).not.toBeInTheDocument();
    // 1,200 + 15 and 2,400 + 30.
    expect(document.body.textContent).not.toMatch(/1,215|2,430/);
    expect(document.body.textContent).not.toMatch(/used Copilot and Cowork fully/);
  });

  it('falls back to the ceiling on the Cowork tile, and says so, when nobody is ready yet', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      withEstimate({
        coworkRecommendedForPolicy: 0,
        coworkValueEstimate: {
          ...coworkReady,
          cohortUsers: 0,
          projectedCoworkTasks: 0,
          hoursPerMonthLow: 0,
          hoursPerMonthHigh: 0,
        },
      }),
    );

    await renderPage();

    const cowork = await tile('Time back from Cowork');
    expect(within(cowork).getByText('75\u2013150 h')).toBeVisible();
    expect(within(cowork).getByText('a month if all 100 Copilot seat holders used Cowork')).toBeVisible();
  });

  it('links each tile to the tab that explains it', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();

    fireEvent.click(within(await tile('Time back from licensing')).getByRole('button', { name: 'See licence opportunities' }));
    expect(await screen.findByRole('tab', { name: 'Licence opportunities', selected: true })).toBeVisible();

    fireEvent.click(screen.getByRole('tab', { name: 'Executive view' }));
    fireEvent.click(within(await tile('Time back from Cowork')).getByRole('button', { name: 'See the Cowork tab' }));
    expect(await screen.findByRole('tab', { name: 'Cowork', selected: true })).toBeVisible();
  });

  it('keeps the Cowork and licence-opportunity figures for a past range and hides only their named lists', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate({
      fromUtc: '2026-09-01T00:00:00Z',
      toUtc: '2026-09-30T00:00:00Z',
      options: { ...options, usesExplicitDates: true, fromUtc: '2026-09-01T00:00:00Z', toUtc: '2026-09-30T00:00:00Z', toExclusiveUtc: '2026-10-01T00:00:00Z' },
    }));

    await renderPage();
    fireEvent.click(screen.getByRole('button', { name: 'Last calendar month' }));

    fireEvent.click(screen.getByRole('tab', { name: 'Licence opportunities' }));
    expect(await screen.findByRole('tablist', { name: 'Licence opportunity sections' })).toBeVisible();
    expect(screen.getByText(/named reclaim and recommendation lists/i)).toBeInTheDocument();
    expect(screen.queryByText('Individual details are hidden')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Cowork' }));
    expect(await screen.findByRole('tablist', { name: 'Cowork sections' })).toBeVisible();
    expect(screen.getByText(/named reclaim and recommendation lists/i)).toBeInTheDocument();
    expect(screen.queryByText('Individual details are hidden')).not.toBeInTheDocument();

    const printHeader = screen.getByText((_content, element) =>
      element?.tagName === 'SPAN' && /Custom range/.test(element.textContent ?? '') && /30 Sept? 2026/.test(element.textContent ?? ''));
    expect(printHeader.textContent).not.toMatch(/Last 28 days/);
  });

  it('gives a reader without See PII the permission reason in the panels on a past range', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate({
      fromUtc: '2026-09-01T00:00:00Z',
      toUtc: '2026-09-30T00:00:00Z',
      options: { ...options, usesExplicitDates: true, fromUtc: '2026-09-01T00:00:00Z', toUtc: '2026-09-30T00:00:00Z', toExclusiveUtc: '2026-10-01T00:00:00Z' },
    }));

    renderWithProvider(<CopilotAdoptionPage />, { access: { administration: false, seePii: false } });
    await screen.findByRole('tab', { name: 'Executive view', selected: true });
    fireEvent.click(screen.getByRole('button', { name: 'Last calendar month' }));

    fireEvent.click(screen.getByRole('tab', { name: 'Licence opportunities' }));
    expect(await screen.findByRole('tablist', { name: 'Licence opportunity sections' })).toBeVisible();
    expect(screen.getByText('Individual details are hidden')).toBeInTheDocument();
  });

  it('claims nothing when there is nobody to model', async () => {
    // This file's default summary: no licence estimate from the server, and Cowork readiness not assessed.
    await renderPage();
    await screen.findAllByText('Where we stand');

    expect(screen.queryByText('Time back from licensing')).not.toBeInTheDocument();
    expect(screen.queryByText('Time back from Cowork')).not.toBeInTheDocument();
  });

  it('shows the licensing figure on its own when Cowork readiness could not be assessed', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate({ coworkReadinessAvailable: false }));

    await renderPage();

    expect(within(await tile('Time back from licensing')).getByText('1,200\u20132,400 h')).toBeVisible();
    expect(screen.queryByText('Time back from Cowork')).not.toBeInTheDocument();
  });

  it('keeps a large range short, with its unit on the same line', async () => {
    // At the 200,000-user design point the range runs to five and six digits, which wrapped the old
    // tile onto three lines with the "h" alone on the last.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate(largeTenant));

    await renderPage();

    const value = within(await tile('Time back from licensing')).getByText('48.5k\u201397k h');
    // A no-break space, so the unit can never wrap onto a line of its own.
    expect(value.textContent).toBe('48.5k\u201397k\u00a0h');
    // And its length, which the tile sizes it by so the range stays on one line however narrow.
    expect(value.style.getPropertyValue('--kpi-value-chars')).toBe('11');
  });

  it('quotes the reader\u2019s own Copilot figures on the licensing tile only, and sends them with the Excel report', async () => {
    sessionStorage.setItem(TIME_SAVED_STORAGE_KEY, JSON.stringify({ meetingMinutes: 16 }));
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();

    // 2,000 x 16 + 100,000 + 24,000 = 156,000 minutes = 2,600 hours, 1,300 conservative.
    expect(within(await tile('Time back from licensing')).getByText('1,300\u20132,600 h')).toBeVisible();
    expect(within(await tile('Time back from Cowork')).getByText('15\u201330 h')).toBeVisible();
    const url = new URL((screen.getByText('Excel report').closest('a') as HTMLAnchorElement).href);
    expect(url.searchParams.get('copilotMinutesSavedPerMeeting')).toBe('16');
    expect(url.searchParams.has('copilotMinutesSavedPerMailThread')).toBe(false);
    // The name before the split is gone: the server no longer reads it.
    expect(url.searchParams.has('coworkMinutesSavedPerMeeting')).toBe(false);
  });

  it('quotes the reader\u2019s own Cowork share on the Cowork tile only, and sends it with the Excel report', async () => {
    sessionStorage.setItem(TIME_SAVED_STORAGE_KEY, JSON.stringify({ sendEmailShare: 0.1 }));
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();

    // 6,000 emails x 10% = 600 handed over x 6 = 3,600 minutes = 60 hours, 30 conservative.
    expect(within(await tile('Time back from Cowork')).getByText('30\u201360 h')).toBeVisible();
    expect(within(await tile('Time back from licensing')).getByText('1,200\u20132,400 h')).toBeVisible();
    const url = new URL((screen.getByText('Excel report').closest('a') as HTMLAnchorElement).href);
    expect(url.searchParams.get('coworkSendEmailShare')).toBe('0.1');
    // The minutes per Cowork task is gone with the tasks it applied to (#692): the server no longer reads it.
    expect(url.searchParams.has('coworkMinutesSavedPerTask')).toBe(false);
    // The flat task rate is gone: the server no longer reads it.
    expect(url.searchParams.has('coworkTasksPerPersonPerMonth')).toBe(false);
  });

  it('explains the two estimates separately, and says they are never added', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();

    fireEvent.click(screen.getByRole('tab', { name: 'How this is calculated' }));
    fireEvent.click(await screen.findByRole('button', { name: 'How licence candidates are ranked' }));
    fireEvent.click(screen.getByRole('button', { name: 'How Cowork readiness is assessed' }));

    expect(await screen.findByText('The licensing estimate is a model, not a measurement.')).toBeVisible();
    expect(screen.getByText(/\(10 minutes per meeting, 5 per email, 8 per document by default/)).toBeVisible();
    expect(screen.getByText('The Cowork estimate is a model, not a measurement.')).toBeVisible();
    // #692: Cowork is audit-only, so nobody is modelled from task counts, and the method says so.
    expect(screen.queryByText(/minutes a task/)).toBeNull();
    expect(screen.getByText(/Everyone covered is modelled from the work they already do by hand, one kind at a time/)).toBeVisible();
    expect(screen.getByText(/the Copilot audit log counts their Cowork interactions, not the work they hand over/)).toBeVisible();
    expect(screen.getByText(/it is never added to the licensing estimate/)).toBeVisible();
  });

  it('keeps Spanish digits where compact notation would be longer, and translates both tiles', async () => {
    // Spanish writes 48,500 compactly as "48,5 mil" - longer than "48.500" - so a compact figure
    // would make the tile wider, not narrower.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate(largeTenant));
    await loadCatalog('es');

    renderWithProvider(<CopilotAdoptionPage />, { language: 'es' });

    const licence = await tile('Tiempo recuperado con licencias', 5000);
    const value = within(licence).getByText('48.500\u201397.000 h');
    expect(value.textContent).toBe('48.500\u201397.000\u00a0h');
    expect(value.style.getPropertyValue('--kpi-value-chars')).toBe('15');
    expect(within(licence).getByText('Modelado')).toBeVisible();
    expect(within(licence).getByText('al mes si las 5000 personas recomendadas tuvieran licencia')).toBeVisible();

    const cowork = await tile('Tiempo recuperado con Cowork');
    expect(within(cowork).getByText('15\u201330 h')).toBeVisible();
    expect(within(cowork).getByText('al mes si las 20 personas preparadas usaran Cowork')).toBeVisible();
  });
});
