import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, waitFor, fireEvent, within, act } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import CopilotAdoptionPage, { USER_FILTER_URL_PARAM } from './CopilotAdoptionPage';
import { MemoryRouter } from 'react-router-dom';
import { serializeUserFilter } from '../components/userFilter/userFilterModel';
import { lastCalendarMonthRange, lastCalendarQuarterRange } from '../components/copilotAdoption/AdoptionPeriodControl';
import {
  fetchAdoptionAvailability,
  fetchAdoptionFilters,
  fetchAdoptionSql,
  fetchAdoptionSummary,
  fetchLicensedUsers,
  rebuildAdoption,
  AdoptionRequestError,
  AdoptionGenerationChangedError,
} from '../api/copilotAdoptionApi';
import { AdoptionBand, CopilotResourceTypeKind, type AdoptionProgress, type CopilotAdoptionAvailability, type CopilotAdoptionOptions, type CopilotAdoptionSummary, type LicensedUserAdoptionRow } from '../types/copilotAdoption';
import type { UserFilterClause, UserFilterEcho } from '../types/userFilter';
import { TIME_SAVED_STORAGE_KEY, resetTimeSavedStore } from '../components/copilotAdoption/coworkTimeSaved';
import { TIME_SAVED_COHORT_STORAGE_KEY, resetTimeSavedCohortStore } from '../components/copilotAdoption/timeSavedCohort';
import { LanguageSwitcher, loadCatalog } from '../i18n';
import userEvent from '@testing-library/user-event';

vi.mock('../api/copilotAdoptionApi', async (importOriginal) => ({
  ...await importOriginal<typeof import('../api/copilotAdoptionApi')>(),
  fetchAdoptionAvailability: vi.fn(),
  fetchAdoptionFilters: vi.fn(),
  fetchAdoptionSql: vi.fn(),
  fetchAdoptionSummary: vi.fn(),
  fetchLicensedUsers: vi.fn(),
  rebuildAdoption: vi.fn(),
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
      m365UsageReportsAvailable: true,
      userMetadataAvailable: true,
      copilotUsageReportDate: '2026-01-01T00:00:00Z',
      copilotUsageReportPeriodDays: 28,
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
      { segment: 'Support', licensedUsers: 25, activeUsers: 25, habitualUsers: 0, neverUsedUsers: 0, adoptionRatePct: 100, averageAdoptionScore: 30, recommendedCandidates: null },
      { segment: 'Finance', licensedUsers: 30, activeUsers: 10, habitualUsers: 3, neverUsedUsers: 15, adoptionRatePct: 33.3, averageAdoptionScore: 22, recommendedCandidates: 6 },
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

async function renderPage(options?: Parameters<typeof renderWithProvider>[1]) {
  renderWithProvider(<CopilotAdoptionPage />, options);
  await screen.findByRole('tab', { name: options?.language === 'es' ? 'Vista ejecutiva' : 'Executive view', selected: true });
}

describe('CopilotAdoptionPage loading and recovery', () => {
  const generationId = '00000000000000000000000000000001';
  const replacementId = '00000000000000000000000000000002';
  const errorReference = '00000000-0000-0000-0000-000000000003';

  function resultProgress(failed = false): AdoptionProgress {
    return {
      generationId, phase: 'completed', elapsedMs: 90_000, percentComplete: 100,
      steps: [
        { step: 'LicenceTypes', state: 'loaded', durationMs: 10 },
        { step: 'ResourceTypes', state: failed ? 'failed' : 'loaded', durationMs: 90_000,
          failure: failed ? 'Timeout' : null, reference: failed ? errorReference : null },
        { step: 'Publishing', state: 'loaded', durationMs: 0 },
      ],
    };
  }

  function partialReport(): CopilotAdoptionSummary {
    return summary({
      topResourceTypes: [],
      diagnostics: {
        generationId, totalMs: 90_000,
        steps: [{ step: 'ResourceTypes', durationMs: 90_000, failed: true }],
        progress: resultProgress(true),
      },
    });
  }

  it.each([
    { language: 'en' as const, percent: '25% of stages processed', progressLabel: 'Analysis stages processed' },
    { language: 'es' as const, percent: '25% de etapas procesadas', progressLabel: 'Etapas del análisis procesadas' },
  ])('shows live stage progress in $language without pretending the report is ready', async ({ language, percent, progressLabel }) => {
    vi.mocked(fetchAdoptionSummary).mockImplementationOnce((_window, _ids, _signal, _domain, _filter, _range, options) => {
      options?.onProgress?.({
        generationId, phase: 'analysing', elapsedMs: 5000, percentComplete: 25,
        steps: [
          { step: 'LicenceTypes', state: 'loaded', durationMs: 10 },
          { step: 'ResourceTypes', state: 'running', durationMs: 4000 },
          { step: 'Scoring', state: 'pending', durationMs: 0 },
          { step: 'Publishing', state: 'pending', durationMs: 0 },
        ],
      });
      return new Promise(() => {});
    });
    await renderPage({ language });
    expect(await screen.findByText(percent, { exact: false })).toBeVisible();
    const bar = screen.getByRole('progressbar', { name: progressLabel });
    expect(bar.closest('[aria-busy]')).toHaveAttribute('aria-busy', 'true');
    expect(screen.queryByText('Where we stand')).not.toBeInTheDocument();
    expect(fetchAdoptionFilters).not.toHaveBeenCalled();
    expect(rebuildAdoption).not.toHaveBeenCalled();
  });

  it('keeps a partial report visible during a rebuild and reconciles supporting data after publication', async () => {
    let resolve!: (report: CopilotAdoptionSummary) => void;
    const replacement = new Promise<CopilotAdoptionSummary>(done => { resolve = done; });
    vi.mocked(fetchAdoptionSummary).mockResolvedValueOnce(partialReport()).mockImplementationOnce(() => replacement);
    vi.mocked(rebuildAdoption).mockResolvedValueOnce({
      generationId: replacementId,
      progress: { ...resultProgress(), generationId: replacementId, phase: 'analysing', percentComplete: 50 },
    });
    await renderPage();
    await screen.findAllByText('Where we stand');
    fireEvent.click(screen.getByRole('button', { name: 'Retry failed analysis' }));
    expect(await screen.findByText(/The previous report remains visible/)).toBeVisible();
    expect(screen.getAllByText('Where we stand')[0]).toBeVisible();
    expect(screen.getByRole('button', { name: 'Print' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Retry failed analysis' })).toBeDisabled();
    await waitFor(() => expect(rebuildAdoption).toHaveBeenCalledTimes(1));

    const recovered = summary({
      diagnostics: {
        generationId: replacementId, totalMs: 100,
        steps: [{ step: 'ResourceTypes', durationMs: 100, failed: false }],
        progress: { ...resultProgress(), generationId: replacementId },
      },
    });
    await act(async () => { resolve(recovered); await replacement; });
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Retry failed analysis' })).not.toBeInTheDocument());
    expect(screen.queryByText(errorReference, { exact: false })).not.toBeInTheDocument();
    await waitFor(() => expect(vi.mocked(fetchAdoptionFilters).mock.lastCall?.[4]).toEqual({ generationId: replacementId }));
    expect(vi.mocked(fetchAdoptionSql).mock.lastCall?.[4]).toEqual({ generationId: replacementId });
    expect(vi.mocked(fetchAdoptionSummary).mock.lastCall?.[6]?.after).toBe(generationId);
  });

  it('renders a failed resource-types card instead of silently hiding an empty result', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValueOnce(partialReport());
    await renderPage();
    await screen.findAllByText('Where we stand');
    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));
    expect(await screen.findByText(/This is a failed section, not evidence/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Retry failed analysis' })).toBeEnabled();
  });

  it('retries a failed filter request without rebuilding SQL or retrying successful requests', async () => {
    vi.mocked(fetchAdoptionFilters).mockRejectedValueOnce(new Error('Synthetic filter request failure'));
    await renderPage();
    expect(await screen.findByText('Synthetic filter request failure')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Retry request' }));
    await waitFor(() => expect(fetchAdoptionFilters).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByText('Synthetic filter request failure')).not.toBeInTheDocument());
    expect(fetchAdoptionSql).toHaveBeenCalledTimes(1);
    expect(rebuildAdoption).not.toHaveBeenCalled();
  });

  it('checks a failed summary request again without issuing a rebuild', async () => {
    vi.mocked(fetchAdoptionSummary).mockRejectedValueOnce(new AdoptionRequestError('Synthetic polling ceiling'));
    await renderPage();
    fireEvent.click(await screen.findByRole('button', { name: 'Check again' }));
    await screen.findAllByText('Where we stand');
    expect(fetchAdoptionSummary).toHaveBeenCalledTimes(2);
    expect(rebuildAdoption).not.toHaveBeenCalled();
  });

  it('checks again after a failed retry by reading the kept report, not by asking for the failed replacement again', async () => {
    const notCompleted = 'The replacement analysis did not complete safely. The previous report has been kept.';
    vi.mocked(fetchAdoptionSummary)
      .mockResolvedValueOnce(partialReport())
      .mockRejectedValueOnce(new AdoptionRequestError({ key: 'copilotAdoption.error.rebuildNotCompleted' }, 503))
      .mockResolvedValueOnce(partialReport());
    vi.mocked(rebuildAdoption).mockResolvedValueOnce({
      generationId: replacementId,
      progress: { ...resultProgress(), generationId: replacementId, phase: 'analysing', percentComplete: 50 },
    });
    await renderPage();
    await screen.findAllByText('Where we stand');
    fireEvent.click(screen.getByRole('button', { name: 'Retry failed analysis' }));

    expect(await screen.findByText(notCompleted, undefined, { timeout: 5000 })).toBeVisible();
    expect(vi.mocked(fetchAdoptionSummary).mock.calls[1]?.[6]?.after).toBe(generationId);
    const checkAgain = await screen.findByRole('button', { name: 'Check again' });
    await waitFor(() => expect(checkAgain).toBeEnabled());
    fireEvent.click(checkAgain);

    await waitFor(() => expect(fetchAdoptionSummary).toHaveBeenCalledTimes(3));
    expect(vi.mocked(fetchAdoptionSummary).mock.calls[2]?.[6]?.after).toBeUndefined();
    await waitFor(() => expect(screen.queryByText(notCompleted)).not.toBeInTheDocument());
    expect(screen.getAllByText('Where we stand')[0]).toBeVisible();
    expect(rebuildAdoption).toHaveBeenCalledTimes(1);
  });

  it('bounds automatic generation reconciliation instead of looping between different server instances', async () => {
    vi.mocked(fetchAdoptionFilters).mockRejectedValue(new AdoptionGenerationChangedError());
    await renderPage();
    // Two summary reads, two filter reads and their renders: on a loaded CI runner that can take longer than the
    // default 1-second wait, as for the other heavy assertions in this file.
    expect(await screen.findByRole('button', { name: 'Check again' }, { timeout: 5000 })).toBeVisible();
    expect(fetchAdoptionSummary).toHaveBeenCalledTimes(2);
    expect(fetchAdoptionFilters).toHaveBeenCalledTimes(2);
    expect(rebuildAdoption).not.toHaveBeenCalled();
  });

  it('does not offer an analysis retry for genuine empty data or ordinary caveats', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValueOnce(summary({ topResourceTypes: [], warnings: ['Synthetic explanatory caveat'] }));
    await renderPage();
    await screen.findAllByText('Where we stand');
    expect(screen.queryByRole('button', { name: 'Retry failed analysis' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));
    expect(screen.queryByText('What Copilot referenced')).not.toBeInTheDocument();
  });
});

/** Picks a reporting period from the page's one period drop-down: a rolling window's days, `lastMonth`, `lastQuarter` or `custom`. */
function choosePeriod(value: string) {
  fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value } });
}

/** The printed caption naming the view and the period, found by the period's name and a date in it. */
function printCaption(periodName: RegExp, date: RegExp) {
  return screen.getByText((_content, element) =>
    element?.tagName === 'SPAN' && periodName.test(element.textContent ?? '') && date.test(element.textContent ?? ''));
}

describe('CopilotAdoptionPage custom ranges', () => {
  it('calculates calendar presets across month lengths and January', () => {
    expect(lastCalendarMonthRange(new Date(Date.UTC(2026, 2, 15)))).toEqual({ from: '2026-02-01', to: '2026-02-28' });
    expect(lastCalendarMonthRange(new Date(Date.UTC(2026, 0, 15)))).toEqual({ from: '2025-12-01', to: '2025-12-31' });
    expect(lastCalendarQuarterRange(new Date(Date.UTC(2026, 0, 15)))).toEqual({ from: '2025-10-01', to: '2025-12-31' });
  });

  it('sends custom ranges to the API and clears them when a rolling preset is chosen', async () => {
    await renderPage();

    choosePeriod('lastMonth');
    expect(screen.getByLabelText('Reporting period')).toHaveValue('lastMonth');
    choosePeriod('28');
    await waitFor(() => expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
      28,
      undefined,
      expect.any(AbortSignal),
      null,
      null,
      null,
      expect.objectContaining({ onProgress: expect.any(Function) }),
    ));

    choosePeriod('lastMonth');
    await waitFor(() => expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
      28,
      undefined,
      expect.any(AbortSignal),
      null,
      null,
      { ...lastCalendarMonthRange() },
      expect.objectContaining({ onProgress: expect.any(Function) }),
    ));

    choosePeriod('lastQuarter');
    await waitFor(() => expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
      28,
      undefined,
      expect.any(AbortSignal),
      null,
      null,
      { ...lastCalendarQuarterRange() },
      expect.objectContaining({ onProgress: expect.any(Function) }),
    ));

    choosePeriod('90');
    await waitFor(() => expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
      90,
      undefined,
      expect.any(AbortSignal),
      null,
      null,
      null,
      expect.objectContaining({ onProgress: expect.any(Function) }),
    ));
  });

  it('offers one period control, with no second set of presets beside it', async () => {
    await renderPage();

    const period = screen.getByLabelText('Reporting period');
    const choices = within(period).getAllByRole('option').map((o) => o.textContent);
    expect(choices).toEqual([
      'Last 7 days',
      'Last 28 days',
      'Last 90 days',
      'Last 180 days',
      'Last calendar month',
      'Last calendar quarter',
      'Custom range',
    ]);
    // The licence-activity presets and their date fields used to sit beside the drop-down as well.
    expect(screen.queryByRole('button', { name: 'Last calendar month' })).toBeNull();
    expect(screen.queryByRole('button', { name: /settled/ })).toBeNull();
    expect(screen.queryByLabelText('From')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Apply' })).toBeNull();
  });

  it('asks for dates only for a custom range, and changes the report only on Apply', async () => {
    await renderPage();
    await waitFor(() => expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenCalled());
    const callsBefore = vi.mocked(fetchAdoptionSummary).mock.calls.length;

    choosePeriod('custom');
    expect(screen.getByLabelText('Reporting period')).toHaveValue('custom');
    expect(vi.mocked(fetchAdoptionSummary).mock.calls.length).toBe(callsBefore);

    fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-08-03' } });
    fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-08-30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));

    await waitFor(() => expect(vi.mocked(fetchAdoptionSummary)).toHaveBeenLastCalledWith(
      28,
      undefined,
      expect.any(AbortSignal),
      null,
      null,
      { from: '2026-08-03', to: '2026-08-30' },
      expect.objectContaining({ onProgress: expect.any(Function) }),
    ));
    // Applied, so there is nothing left to apply until a date changes again.
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
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

    choosePeriod('lastMonth');
    fireEvent.click(screen.getByRole('tab', { name: 'Licensed users' }));
    expect(await screen.findByText(/named reclaim and recommendation lists/i)).toBeVisible();
    fireEvent.click(screen.getByRole('tab', { name: 'Time saved' }));
    expect(await screen.findByText('Time already saved by seat holders')).toBeVisible();
    expect(printCaption(/Last calendar month/, /1 Sept? 2026/i).textContent).toMatch(/30 Sept? 2026/i);
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

    // One sentence with the list inside it, joined as each language joins a list.
    await waitFor(() => expect(document.body.textContent)
      .toContain('sin ellos: tipos de licencia, preparación para Cowork y a dataset this build does not know. Esto suele'));
    expect(document.body.textContent).not.toContain('licence types, Cowork readiness');
    spanish.unmount();

    renderWithProvider(<CopilotAdoptionPage />);
    await waitFor(() => expect(document.body.textContent)
      .toContain('without them: licence types, Cowork readiness and a dataset this build does not know. This is usually'));
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

  it('shows a dash, not zero candidates, for a league-table department whose count is withheld', async () => {
    await renderPage();

    const table = await screen.findByRole('table', { name: 'Department league table' });
    // Support has no published candidate count - too few candidates to show, or past the candidate list's cap.
    const support = within(within(table).getByText('Support').closest('tr') as HTMLElement);
    expect(support.getAllByText('\u2014')).toHaveLength(2);
    const finance = within(within(table).getByText('Finance').closest('tr') as HTMLElement);
    expect(finance.getByText('6')).toBeVisible();
    expect(finance.queryByText('\u2014')).toBeNull();
  });

  it('shows each league-table department its own candidate count, including a true zero, whatever the candidate chart keeps', async () => {
    // The rows are the lowest habit-rate departments and the candidate chart the departments with the most
    // candidates: Legal and Operations are in neither of the chart's two rows, but each row carries its own count.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      habitByDepartment: [
        { segment: 'Legal', licensedUsers: 20, activeUsers: 10, habitualUsers: 1, neverUsedUsers: 2, adoptionRatePct: 50, averageAdoptionScore: 20, recommendedCandidates: 7 },
        { segment: 'Operations', licensedUsers: 20, activeUsers: 10, habitualUsers: 2, neverUsedUsers: 4, adoptionRatePct: 50, averageAdoptionScore: 25, recommendedCandidates: 0 },
      ],
      opportunityByDepartment: [{ label: 'Finance', value: 9 }, { label: 'Sales', value: 8 }],
    }));
    await renderPage();

    const table = await screen.findByRole('table', { name: 'Department league table' });
    const legal = within(within(table).getByText('Legal').closest('tr') as HTMLElement);
    expect(legal.getByText('7')).toBeVisible();
    expect(legal.getByText('5 more candidates than never-used seats')).toBeVisible();
    const operations = within(within(table).getByText('Operations').closest('tr') as HTMLElement);
    expect(operations.getByText('0')).toBeVisible();
    expect(operations.getByText('4 more never-used seats than candidates')).toBeVisible();
    expect(operations.queryByText('\u2014')).toBeNull();
  });

  it.each([
    {
      language: 'en' as const,
      analyst: 'Analyst view',
      table: 'Department league table',
      reason: 'Hidden: with the tenant\u2019s recommended candidates, this count would reveal a department with fewer than 5 candidates',
      tableNote: /A larger count can be hidden too/,
      chartNote: /A department is also left out when subtracting the departments shown/,
    },
    {
      language: 'es' as const,
      analyst: 'Vista de analista',
      table: 'Tabla comparativa de departamentos',
      reason: 'Oculto: con los candidatos recomendados del inquilino, este recuento revelaría un departamento con menos de 5 candidatos',
      tableNote: /También se puede ocultar un recuento mayor/,
      chartNote: /También se omite un departamento/,
    },
  ])('says in $language why a larger candidate count is hidden from a reader without See PII, never as 0', async ({ language, analyst, table, reason, tableNote, chartNote }) => {
    // #844: Legal's two candidates are withheld for every reader. Finance's six went too, for a reader without See PII,
    // because the tenant's recommended candidates less the counts shown would otherwise give Legal's two back.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      habitByDepartment: [
        { segment: 'Legal', licensedUsers: 20, activeUsers: 10, habitualUsers: 1, neverUsedUsers: 2, adoptionRatePct: 50, averageAdoptionScore: 20, recommendedCandidates: null },
        { segment: 'Finance', licensedUsers: 30, activeUsers: 10, habitualUsers: 3, neverUsedUsers: 15, adoptionRatePct: 33.3, averageAdoptionScore: 22, recommendedCandidates: null, recommendedCandidatesWithheld: 'revealsSmallerGroup' },
      ],
      opportunityByDepartment: [{ label: 'Sales', value: 8 }],
      opportunityByDepartmentWithheld: 'revealsSmallerGroup',
    }));
    if (language === 'es') await loadCatalog('es');
    await renderPage({ language });

    const league = await screen.findByRole('table', { name: table }, { timeout: 5000 });
    const finance = within(within(league).getByText('Finance').closest('tr') as HTMLElement);
    expect(finance.getByLabelText(reason)).toHaveTextContent('\u2014');
    const legal = within(within(league).getByText('Legal').closest('tr') as HTMLElement);
    expect(legal.queryByLabelText(reason)).toBeNull();
    expect(screen.getByText(tableNote)).toBeVisible();

    fireEvent.click(screen.getByRole('tab', { name: analyst }));
    await screen.findByRole('tab', { name: analyst, selected: true });
    expect(await screen.findByText(chartNote, {}, { timeout: 5000 })).toBeVisible();
  });

  it.each([
    {
      language: 'en' as const,
      analyst: 'Analyst view',
      departments: /^One more department, the smallest listed, is left out of every department view too/,
      countries: /^One more country, the smallest listed, is left out too/,
      builders: /Agent builders are hidden for a department with too few of them to show/,
    },
    {
      language: 'es' as const,
      analyst: 'Vista de analista',
      departments: /^También se omite un departamento más, el más pequeño de la lista, en todas las vistas por departamento/,
      countries: /^También se omite un país más, el más pequeño de la lista/,
      builders: /Los creadores de agentes se ocultan en un departamento con demasiado pocos para mostrarse/,
    },
  ])('says in $language, under each view, that one more group is left out for a reader without See PII', async ({ language, analyst, departments, countries, builders }) => {
    // #844: the server left out the smallest listed department and country, and one more department's builders, so that
    // the tenant's figures less the groups shown cannot give back a group too small to list.
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      smallestGroupsWithheld: ['departments', 'countries', 'builderDepartments'],
    }));
    if (language === 'es') await loadCatalog('es');
    await renderPage({ language });

    expect(await screen.findByText(departments, {}, { timeout: 5000 })).toBeVisible();

    fireEvent.click(screen.getByRole('tab', { name: analyst }));
    await screen.findByRole('tab', { name: analyst, selected: true });
    // The adoption, intensity and side-by-side cards are each a view of the departments.
    expect((await screen.findAllByText(departments, {}, { timeout: 5000 })).length).toBeGreaterThanOrEqual(3);
    expect(screen.getByText(countries)).toBeVisible();
    expect(screen.getByText(builders)).toBeVisible();
  });

  it('says nothing more is left out when the server withheld nothing more, as for a reader with See PII', async () => {
    await renderPage();
    await screen.findByRole('table', { name: 'Department league table' }, { timeout: 5000 });

    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));
    await screen.findByRole('tab', { name: 'Analyst view', selected: true });
    await screen.findByText('Usage frequency and intensity', {}, { timeout: 5000 });
    expect(screen.queryByText(/is left out of every department view too/)).toBeNull();
    expect(screen.queryByText(/One more country/)).toBeNull();
  });

  it('shows Microsoft’s tenant prompt figures on the executive view, named and dated, beside the audit figures', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      microsoftReportDate: '2026-10-05T00:00:00Z',
      microsoftReportPeriodDays: 28,
      microsoftReportVersion: 'v2',
      microsoftReportPromptsSubmitted: 98765,
      microsoftReportAveragePromptsPerActiveUser: 41.6,
    }));

    await renderPage();

    const line = await screen.findByLabelText('Microsoft\'s Copilot usage report');
    expect(line.textContent).toContain('28-day period to 5 Oct 2026');
    expect(line.textContent).toContain('98,765');
    expect(line.textContent).toContain('41.6');
    // A different unit from the audit-log interactions: the headline figures are untouched by it.
    expect(document.body.textContent).not.toContain('101,165');
  });

  it('leaves the executive view as it was when no Microsoft summary has been imported', async () => {
    await renderPage();
    await screen.findAllByText('Where we stand');

    expect(screen.queryByText('Microsoft\'s Copilot usage report')).not.toBeInTheDocument();
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
        null,
        expect.objectContaining({ onProgress: expect.any(Function) }),
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

  it('says which sections the administrator\'s report filter leaves out, instead of showing them as empty', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({
        globalFilter: { clauses: [], matchedPeople: 40, directoryPeople: 200, viewerFound: true, unknownDimensions: [], dimensionNames: {} },
        unscopedLicensedUsers: null,
        unscopedSections: [],
        withheldSections: ['usageByApp', 'agents', 'purchasedSeats'],
        usageByApp: [],
        purchasedCopilotSeats: null,
        unassignedCopilotSeats: null,
      }),
    );
    const withheld = 'Not shown, because it describes the whole organisation and a portal administrator\'s report filter applies.';

    await renderPage();

    const banner = (await screen.findByText('Filtered:')).parentElement as HTMLElement;
    expect(banner.textContent).toContain('describe only the people this filter matches, not the whole tenant');
    expect(banner.textContent).toMatch(
      /Because a portal administrator's report filter applies, Copilot use by app \(licensed and unlicensed\), the agent inventory and purchased and unassigned seats are not shown/,
    );

    fireEvent.click(screen.getByRole('tab', { name: 'Analyst view' }));
    await screen.findByRole('tab', { name: 'Analyst view', selected: true });
    // The purchased-seats tile and the by-app card say why they are empty, rather than "unknown" or "not imported".
    expect((await screen.findAllByText(withheld, undefined, { timeout: 5000 })).length).toBeGreaterThanOrEqual(2);
    expect(screen.queryByText('No per-app breakdown is available', { exact: false })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Agents' }));
    expect(await screen.findByText(withheld, undefined, { timeout: 5000 })).toBeVisible();
  });

  it('says Microsoft\'s tenant report is withheld by the report filter in the method, not that it was never imported', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(
      summary({
        globalFilter: { clauses: [], matchedPeople: 40, directoryPeople: 200, viewerFound: true, unknownDimensions: [], dimensionNames: {} },
        withheldSections: ['microsoftReport'],
        microsoftReportDate: null,
        microsoftReportPeriodDays: null,
      }),
    );

    await renderPage();
    fireEvent.click(await screen.findByRole('tab', { name: 'How this is calculated' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Where the data comes from' }));

    const withheld = await screen.findAllByText(/not shown, because it describes the whole organisation and a portal administrator's report filter applies\./);
    expect(withheld.length).toBeGreaterThan(0);
    const method = withheld[withheld.length - 1];
    expect(method.textContent).toContain('Microsoft\'s own prompts submitted');
    expect(method.textContent).not.toContain('not imported');
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
        null,
        expect.objectContaining({ onProgress: expect.any(Function) }),
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
        null,
        expect.objectContaining({ onProgress: expect.any(Function) }),
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
    await waitFor(() => expect(screen.getByRole('button', { name: 'Print' })).not.toBeDisabled());

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

    // The tile opens the section that explains it, not the list.
    fireEvent.click(within(tileCard).getByRole('button', { name: 'Review on Licensed users' }));
    expect(await screen.findByRole('tab', { name: 'Licensed users', selected: true })).toBeVisible();
    expect(screen.getByRole('tab', { name: 'Time saved', selected: true })).toBeVisible();

    const model = screen.getByRole('tabpanel', { name: 'Time saved' });
    expect(within(model).getByText('Time already saved by seat holders')).toBeVisible();
    expect(within(model).getByText(/Copilot Chat and other non-agent surfaces default to zero minutes/)).toBeVisible();
    expect(within(model).getByText(/Agent and Cowork activity is left out of this figure/)).toBeVisible();

    const other = within(model).getByText('Copilot Chat and other apps').closest('tr') as HTMLElement;
    expect(within(other).getByText('20')).toBeVisible();
    expect(within(other).getByText('0 h')).toBeVisible();
    expect(within(model).getByText('17 h')).toBeVisible();
    expect(within(model).getByText('9 h')).toBeVisible();
    expect(within(model).getByText(/^3 seat holders are not in this figure/)).toBeVisible();
  });

  it('opens the Licensed users tab on the list, with the time saved a section away', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Licensed users' }));

    expect(await screen.findByRole('tab', { name: 'Seat holders', selected: true })).toBeVisible();
    expect(screen.getByRole('tabpanel', { name: 'Seat holders' })).toBeVisible();
    expect(screen.queryByRole('tabpanel', { name: 'Time saved' })).toBeNull();
    expect(screen.getByText('Time already saved by seat holders')).not.toBeVisible();

    fireEvent.click(screen.getByRole('tab', { name: 'Time saved' }));
    expect(screen.getByText('Time already saved by seat holders')).toBeVisible();
    expect(screen.queryByRole('tabpanel', { name: 'Seat holders' })).toBeNull();
  });

  it('shows the Licensed users list alone when there is no seat-holder estimate', async () => {
    await renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Licensed users' }));

    await waitFor(() => expect(vi.mocked(fetchLicensedUsers)).toHaveBeenCalled());
    expect(screen.queryByRole('tab', { name: 'Time saved' })).toBeNull();
    expect(screen.queryByText('Time already saved by seat holders')).toBeNull();
  });

  it('lets the reader edit a realised-value credit for the session', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Licensed users' }));
    fireEvent.click(await screen.findByRole('tab', { name: 'Time saved' }));

    const input = await screen.findByLabelText('Outlook minutes per action');
    fireEvent.change(input, { target: { value: '12' } });

    const model = screen.getByRole('tabpanel', { name: 'Time saved' });
    const outlook = within(model).getByText('Copilot interactions in Outlook').closest('tr') as HTMLElement;
    expect(within(outlook).getByText('20 h')).toBeVisible();
    expect(within(model).getByText('27 h')).toBeVisible();
    expect(within(model).getByText('14 h')).toBeVisible();
    const finance = within(model).getByText('Finance').closest('tr') as HTMLElement;
    expect(within(finance).getByText('14\u201327 h')).toBeVisible();

    // The overview's tile is the same model, so it follows the figure too.
    fireEvent.click(screen.getByRole('tab', { name: 'Executive view' }));
    expect(within(await tile('Time already saved by seat holders')).getByText('14\u201327 h')).toBeVisible();
  });

  it('refuses a credit outside the model\u2019s bounds rather than modelling it', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Licensed users' }));
    fireEvent.click(await screen.findByRole('tab', { name: 'Time saved' }));

    fireEvent.change(await screen.findByLabelText('Outlook minutes per action'), { target: { value: '600' } });

    const model = screen.getByRole('tabpanel', { name: 'Time saved' });
    expect(within(model).getByText('Enter a number from 0 to 60.')).toBeVisible();
    expect(within(model).getByText('17 h')).toBeVisible();
  });

  it('explains, rather than models, when no seat holder has per-action detail', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate({
      seatHolderTimeSavedEstimate: { ...seatHolderEstimate, cohortUsers: 0, excludedUsageReportSourcedUsers: 5, byDepartment: [] },
    }));

    await renderPage();
    fireEvent.click(screen.getByRole('tab', { name: 'Licensed users' }));
    fireEvent.click(await screen.findByRole('tab', { name: 'Time saved' }));

    const model = screen.getByRole('tabpanel', { name: 'Time saved' });
    expect(within(model).getByText(/^5 seat holders are not in this figure/)).toBeVisible();
    expect(within(model).queryByRole('table')).toBeNull();
    expect(within(model).queryByLabelText('Outlook minutes per action')).toBeNull();
  });

  it('translates the no-department placeholder in the realised-value table', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate({
      seatHolderTimeSavedEstimate: {
        ...seatHolderEstimate,
        byDepartment: [{ ...seatHolderEstimate.byDepartment[0], segment: '(no department)' }],
      },
    }));

    await renderPage({ language: 'es' });
    fireEvent.click(screen.getByRole('tab', { name: 'Usuarios con licencia' }));
    fireEvent.click(await screen.findByRole('tab', { name: 'Tiempo ahorrado' }));

    expect(await screen.findByText('(sin departamento)')).toBeVisible();
    expect(screen.queryByText('(no department)')).toBeNull();
  });

  it('does not offer to open the hidden Licensed users tab without See PII', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate());

    await renderPage({ access: { administration: false, seePii: false } });

    const tileCard = await tile('Time already saved by seat holders');
    expect(within(tileCard).getByText('9\u201317 h')).toBeVisible();
    expect(within(tileCard).queryByText('Review on Licensed users')).toBeNull();
  });

  it('has Spanish text for the realised seat-holder estimate', async () => {
    const es = await loadCatalog('es');
    expect(es['copilotAdoptionUsers.licensed.sections.timeSaved']).toBe('Tiempo ahorrado');
    expect(es['copilotAdoptionTimeSaved.seat.table.observed']).toBe('Acciones al mes');
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
    choosePeriod('lastMonth');

    fireEvent.click(screen.getByRole('tab', { name: 'Licence opportunities' }));
    expect(await screen.findByRole('tablist', { name: 'Licence opportunity sections' })).toBeVisible();
    expect(screen.getByText(/named reclaim and recommendation lists/i)).toBeInTheDocument();
    expect(screen.queryByText('Individual details are hidden')).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Cowork' }));
    expect(await screen.findByRole('tablist', { name: 'Cowork sections' })).toBeVisible();
    expect(screen.getByText(/named reclaim and recommendation lists/i)).toBeInTheDocument();
    expect(screen.queryByText('Individual details are hidden')).not.toBeInTheDocument();

    // The printout names the period chosen - not "Custom range", and not the rolling window it replaced.
    const printHeader = printCaption(/Last calendar month/, /30 Sept? 2026/);
    expect(printHeader.textContent).not.toMatch(/Last 28 days|Custom range/);
  });

  it('gives a reader without See PII the permission reason in the panels on a past range', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(withEstimate({
      fromUtc: '2026-09-01T00:00:00Z',
      toUtc: '2026-09-30T00:00:00Z',
      options: { ...options, usesExplicitDates: true, fromUtc: '2026-09-01T00:00:00Z', toUtc: '2026-09-30T00:00:00Z', toExclusiveUtc: '2026-10-01T00:00:00Z' },
    }));

    renderWithProvider(<CopilotAdoptionPage />, { access: { administration: false, seePii: false } });
    await screen.findByRole('tab', { name: 'Executive view', selected: true });
    choosePeriod('lastMonth');

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
    // The link has no href until the filters and SQL have loaded too (reportBusy), which can be after the tiles.
    const url = await waitFor(() => new URL((screen.getByText('Excel report').closest('a') as HTMLAnchorElement).href), { timeout: 5000 });
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
    // The link has no href until the filters and SQL have loaded too (reportBusy), which can be after the tiles.
    const url = await waitFor(() => new URL((screen.getByText('Excel report').closest('a') as HTMLAnchorElement).href), { timeout: 5000 });
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

describe('CopilotAdoptionPage shared links', () => {
  it('tells a reader without See PII that a shared link’s filter was not applied, and applies it for one with See PII', async () => {
    const link = serializeUserFilter({ clauses: [FABRIKAM_ONLY] })!;
    const openLink = (seePii: boolean) =>
      renderWithProvider(
        <MemoryRouter initialEntries={[`/insights/copilot-adoption?${USER_FILTER_URL_PARAM}=${encodeURIComponent(link)}`]}>
          <CopilotAdoptionPage />
        </MemoryRouter>,
        { access: { administration: false, seePii } },
      );

    const { unmount } = openLink(false);
    expect(await screen.findByText('The filter in this link was not applied')).toBeInTheDocument();
    await waitFor(() => expect(fetchAdoptionSummary).toHaveBeenCalled());
    expect(vi.mocked(fetchAdoptionSummary).mock.calls.every((call) => !call[4])).toBe(true);
    unmount();

    vi.mocked(fetchAdoptionSummary).mockClear();
    openLink(true);
    await waitFor(() => expect(fetchAdoptionSummary).toHaveBeenCalled());
    expect(vi.mocked(fetchAdoptionSummary).mock.calls[0][4]).toBe(link);
    expect(screen.queryByText('The filter in this link was not applied')).not.toBeInTheDocument();
  });
});

describe('CopilotAdoptionPage periods with their own dates', () => {
  const PAST_QUARTER_SUMMARY = () => summary({
    windowDays: 92,
    fromUtc: '2026-07-01T00:00:00Z',
    toUtc: '2026-09-30T00:00:00Z',
    options: { ...options, windowDays: 92, usesExplicitDates: true, fromUtc: '2026-07-01T00:00:00Z', toUtc: '2026-09-30T00:00:00Z', toExclusiveUtc: '2026-10-01T00:00:00Z' },
  });

  it('dates unlicensed use by the quarter analysed, never by the rolling window the selector last held', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(PAST_QUARTER_SUMMARY());
    await renderPage();
    choosePeriod('lastQuarter');

    fireEvent.click(screen.getByRole('tab', { name: 'Unlicensed usage' }));

    expect(await screen.findByText(/^Used Copilot from 1 Jul 2026 to 30 Sept? 2026 with no licence$/)).toBeInTheDocument();
    expect(screen.queryByText(/in the last 28 days/)).not.toBeInTheDocument();
  });

  it('keeps "the last N days" for a rolling window', async () => {
    await renderPage();

    fireEvent.click(screen.getByRole('tab', { name: 'Unlicensed usage' }));

    expect(await screen.findByText('Used Copilot in the last 28 days with no licence')).toBeInTheDocument();
  });
});

describe('CopilotAdoptionPage language switch', () => {
  /** The language picker in the header, as a reader uses it. */
  async function switchToSpanish() {
    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: /Language: English/ }));
    await user.click(await screen.findByRole('menuitemradio', { name: 'Espa\u00f1ol' }, { timeout: 5000 }));
  }

  function seatHolder(id: number): LicensedUserAdoptionRow {
    return {
      userId: id,
      userPrincipalName: `demo.user${String(id).padStart(7, '0')}@contoso.example`,
      mail: null,
      department: 'Finance',
      jobTitle: null,
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
    };
  }

  /** How many times each Copilot Adoption request has been made so far. */
  const requestCounts = () => [
    fetchAdoptionAvailability, fetchAdoptionSummary, fetchAdoptionFilters, fetchAdoptionSql, fetchLicensedUsers,
  ].map((fetcher) => vi.mocked(fetcher).mock.calls.length);

  it('switches language without asking the server again, and keeps the licensed-user list where the reader left it', async () => {
    await loadCatalog('es');
    // 120 seat holders: three pages of fifty.
    vi.mocked(fetchLicensedUsers).mockImplementation(async (_windowDays, _filters, skip, take) => ({
      total: 120,
      skip,
      take,
      warnings: [],
      rows: Array.from({ length: Math.min(take, 120 - skip) }, (_, i) => seatHolder(skip + i + 1)),
    }));
    renderWithProvider(<><LanguageSwitcher /><CopilotAdoptionPage /></>);
    fireEvent.click(await screen.findByRole('tab', { name: 'Licensed users' }, { timeout: 5000 }));

    fireEvent.change(await screen.findByRole('textbox', { name: 'Search licensed Copilot users' }, { timeout: 5000 }), {
      target: { value: 'contoso' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    const department = await screen.findByRole('columnheader', { name: 'Department' }, { timeout: 5000 });
    fireEvent.click(within(department).getByRole('button'));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled(), { timeout: 5000 });
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByText('Page 2 of 3', undefined, { timeout: 5000 })).toBeVisible();
    expect(vi.mocked(fetchLicensedUsers).mock.lastCall?.[1]).toMatchObject({ search: 'contoso', sortBy: 'department', sortDesc: false });
    expect(vi.mocked(fetchLicensedUsers).mock.lastCall?.[2]).toBe(50);
    const before = requestCounts();

    await switchToSpanish();

    expect(await screen.findByRole('tab', { name: 'Usuarios con licencia', selected: true }, { timeout: 5000 })).toBeVisible();
    expect(screen.getByRole('textbox', { name: 'Buscar usuarios con licencia de Copilot' })).toHaveValue('contoso');
    expect(screen.getByRole('columnheader', { name: 'Departamento' })).toHaveAttribute('aria-sort', 'ascending');
    expect(screen.getByText('P\u00e1gina 2 de 3')).toBeVisible();
    expect(screen.getByText('demo.user0000051@contoso.example')).toBeVisible();
    expect(requestCounts()).toEqual(before);
  });

  it('words a failed request in the language the reader switches to, without making it again', async () => {
    await loadCatalog('es');
    vi.mocked(fetchAdoptionFilters).mockRejectedValueOnce(
      new AdoptionRequestError({ key: 'errors.copilotAdoption.filtersFailed', values: { status: 500 } }, 500),
    );
    renderWithProvider(<><LanguageSwitcher /><CopilotAdoptionPage /></>);
    expect(await screen.findByText("Couldn't load the Copilot adoption filters (500).", undefined, { timeout: 5000 })).toBeVisible();

    await switchToSpanish();

    expect(await screen.findByText('No se han podido cargar los filtros de adopci\u00f3n de Copilot (500).', undefined, { timeout: 5000 })).toBeVisible();
    expect(fetchAdoptionFilters).toHaveBeenCalledTimes(1);
  });

  it('keeps an availability failure on screen until a later check succeeds, then clears it', async () => {
    const available: CopilotAdoptionAvailability = {
      available: true,
      copilotAuditImportEnabled: true,
      copilotUsageReportImportEnabled: true,
      userMetadataImportEnabled: true,
      m365UsageReportImportEnabled: true,
      messages: [],
    };
    let answer!: (availability: CopilotAdoptionAvailability) => void;
    vi.mocked(fetchAdoptionAvailability)
      .mockRejectedValueOnce(new AdoptionRequestError({ key: 'errors.copilotAdoption.availabilityFailed', values: { status: 503 } }, 503))
      .mockImplementationOnce(() => new Promise((resolve) => { answer = resolve; }));
    renderWithProvider(<CopilotAdoptionPage />);
    const failure = "Couldn't load the Copilot adoption availability (503).";
    expect(await screen.findByText(failure, undefined, { timeout: 5000 })).toBeVisible();

    const retry = screen.getByRole('button', { name: 'Retry request' });
    await waitFor(() => expect(retry).toBeEnabled());
    fireEvent.click(retry);
    // The last answer stands while the next is awaited, and the check cannot be started twice.
    await waitFor(() => expect(retry).toBeDisabled());
    expect(screen.getByText(failure)).toBeVisible();

    await act(async () => { answer(available); });
    expect(await screen.findByRole('tab', { name: 'Executive view', selected: true }, { timeout: 5000 })).toBeVisible();
    expect(screen.queryByText(failure)).not.toBeInTheDocument();
    expect(fetchAdoptionAvailability).toHaveBeenCalledTimes(2);
  });

  it('says where each figure comes from in whole Spanish sentences that agree with their source', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      dataSources: { ...summary().dataSources, m365UsageReportsAvailable: false },
    }));
    await loadCatalog('es');
    renderWithProvider(<CopilotAdoptionPage />, { language: 'es' });
    await screen.findByRole('tab', { name: 'Vista ejecutiva', selected: true }, { timeout: 5000 });
    fireEvent.click(screen.getByRole('tab', { name: 'C\u00f3mo se calcula' }));
    const sources = await screen.findByRole('button', { name: 'De d\u00f3nde proceden los datos' }, { timeout: 5000 });
    await waitFor(() => expect(sources).toBeEnabled());
    fireEvent.click(sources);

    const line = async (heading: string) => (await screen.findByText(heading, undefined, { timeout: 5000 })).parentElement!.textContent;
    expect(await line('Registro de auditor\u00eda de Copilot:')).toBe(
      'Registro de auditor\u00eda de Copilot: disponible. Cubre a todos los usuarios, incluido el uso de Copilot Chat sin licencia, y coincide exactamente con el periodo seleccionado.',
    );
    expect(await line('Informe de uso de Microsoft Copilot:')).toMatch(
      /^Informe de uso de Microsoft Copilot: instant\u00e1nea del 1 ene\.? 2026\. Solo cubre a los usuarios con licencia, /,
    );
    expect(await line('Informes de uso de Microsoft 365:')).toBe(
      'Informes de uso de Microsoft 365: no importados. Se usan para encontrar usuarios intensivos de Microsoft 365 que no tienen licencia de Copilot.',
    );
    expect(await line('Cifras de indicaciones del inquilino seg\u00fan Microsoft:')).toMatch(
      /^Cifras de indicaciones del inquilino seg\u00fan Microsoft: no importadas\. Las indicaciones enviadas/,
    );
  });

  it('writes the score settings in the Method tab with the reader\u2019s decimal comma', async () => {
    vi.mocked(fetchAdoptionSummary).mockResolvedValue(summary({
      windowDays: 90,
      // The server's own default weights are fractions of one.
      options: { ...options, windowDays: 90, frequencyWeight: 0.5, depthWeight: 0.3, breadthWeight: 0.2, depthTargetInteractionsPerActiveDay: 2.5 },
    }));
    await loadCatalog('es');
    renderWithProvider(<CopilotAdoptionPage />, { language: 'es' });
    await screen.findByRole('tab', { name: 'Vista ejecutiva', selected: true }, { timeout: 5000 });
    fireEvent.click(screen.getByRole('tab', { name: 'C\u00f3mo se calcula' }));

    const formula = await screen.findByText(/^frecuencia = min\(1, /, undefined, { timeout: 5000 });
    expect(formula.textContent).toContain('puntuacion = (frecuencia x 0,5 + profundidad x 0,3 + amplitud x 0,2)\n        / 1 x 100');
    expect(formula.textContent).toContain('/ 2,5) x confianza');
    expect(formula.textContent).not.toMatch(/\d\.\d/);

    // 20 interactions per 28 days, at 90 days: about 64.3.
    fireEvent.click(screen.getByRole('button', { name: 'C\u00f3mo se clasifican los candidatos a licencia' }));
    expect(await screen.findByText(/aproximadamente 64,3 aqu\u00ed/, undefined, { timeout: 5000 })).toBeVisible();
  });
});
