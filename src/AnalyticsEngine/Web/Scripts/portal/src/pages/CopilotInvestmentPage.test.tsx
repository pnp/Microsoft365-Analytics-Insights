import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';

import { renderWithProvider } from '../test/renderWithProvider';
import CopilotInvestmentPage from './CopilotInvestmentPage';
import { loadCatalog } from '../i18n';
import {
  fetchInvestmentAvailability,
  fetchInvestmentPeople,
  fetchInvestmentPerson,
  fetchInvestmentSummary,
  fetchInvestmentTrend,
  investmentWorkbookUrl,
  queryParams,
} from '../api/copilotInvestmentApi';
import type { CopilotInvestmentSummary, InvestmentPeoplePage } from '../types/copilotInvestment';

vi.mock('../api/copilotInvestmentApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/copilotInvestmentApi')>()),
  fetchInvestmentAvailability: vi.fn(),
  fetchInvestmentSummary: vi.fn(),
  fetchInvestmentTrend: vi.fn(),
  fetchInvestmentPeople: vi.fn(),
  fetchInvestmentPerson: vi.fn(),
}));

vi.mock('../api/userFilterApi', () => ({
  fetchUserFilterDimensions: vi.fn(async () => ({ people: 400, loadedUtc: '2026-10-27T00:00:00Z', dimensions: [] })),
  fetchUserFilterValues: vi.fn(async () => ({ values: [], truncated: false })),
}));

/** A synthetic Contoso report: illustrative test inputs and rounded counts, no real tenant data. */
function summary(overrides: Partial<CopilotInvestmentSummary> = {}): CopilotInvestmentSummary {
  return {
    generatedUtc: '2026-10-27T09:00:00Z',
    fromUtc: '2026-09-30T00:00:00Z',
    toUtc: '2026-10-27T00:00:00Z',
    windowDays: 28,
    weekdays: 20,
    monthsInWindow: 0.92,
    filtered: false,
    userFilter: null,
    globalFilter: null,
    assumptions: {
      status: 'complete',
      scenarioApplied: false,
      version: 3,
      updatedUtc: '2026-10-20T10:00:00Z',
      note: 'Contoso business case \u2013 \u0394\u03bf\u03ba\u03b9\u03bc\u03ae',
      values: {
        currency: 'EUR',
        seatPricePerMonth: 30,
        creditPrice: 0.01,
        studioCreditPrice: 0.01,
        hourlyCost: 50,
        minutesPerActiveDay: { conservative: 8, base: 15, upside: 25 },
        realisation: { conservative: 0.3, base: 0.5, upside: 0.7 },
        note: null,
      },
      provenance: { seatPricePerMonth: 'tenant', hourlyCost: 'tenant', creditPrice: 'tenant', studioCreditPrice: 'tenant', 'minutesPerActiveDay.base': 'tenant', 'realisation.base': 'tenant' },
      missingForValue: [],
      storageDurable: true,
    },
    sources: {
      adoption: {
        available: true, auditAvailable: true, usageReportAvailable: true, usageReportDate: null, figuresIncomplete: false,
        licensedUsersCapped: false, holdersNotAnalysed: 0, auditSignalHolders: 200, usageReportSignalHolders: 0, analysisGeneratedUtc: '2026-10-27T08:00:00Z',
      },
      credits: {
        tablesPresent: true, collectionEnabled: true, hasData: true, firstObservationDate: '2026-07-27', snapshotDate: '2026-10-26',
        snapshotBandFrom: '2026-10-24', periodStatus: 'unverified', observedUsers: 400, unresolvedObjects: 3, unresolvedCredits: 120,
        partialUsers: 2, staleUsers: 0, oldestFreshness: null, newestFreshness: null, sweepsInWindow: 26, completedSweepsInWindow: 23,
        directoryUsers: 403, collectorObserved: 403, collectorFailed: 0, collectorDenied: 0, collectorPending: 0, collectorPhase: 'complete',
        collectorLastCompleted: '2026-10-26T12:45:00Z', contextObservedAt: '2026-10-26T05:30:00Z', failureCode: null,
      },
      studio: { hasData: true, importEnabled: true, lastImportUtc: '2026-10-26T03:00:00Z', lastImportFailed: false, unresolvedCredits: 0, failureCode: null },
    },
    investment: {
      currency: 'EUR',
      seats: { holders: 240, analysedHolders: 240, basis: 'currentAssignments', seatDays: 6720, monthlyRunRate: 7200, windowCost: 6623, analysedWindowCost: 6623, costPerActiveHolderMonthly: 36 },
      studio: { users: 40, credits: 21000, priorCredits: 18000, priorUsers: 35, holderCredits: 9000, windowValue: 210, holderWindowValue: 90 },
      credits: {
        observedUsers: 400, consumingUsers: 150, credits: 18500, holderCredits: 15000, nonHolderCredits: 3500, balanceValue: 185,
        periodStatus: 'unverified', otherUnitUsers: 20, unknownAmountUsers: 0, limitExhaustedUsers: 4, nearLimitUsers: 6, notEntitledUsers: 10,
      },
      modelledWindowCost: 6713,
      modelledMonthlyCost: 7300,
    },
    evidence: {
      holders: 240, analysedHolders: 240, activeHolders: 200, activeShare: 0.8333, idleHolders: 40, idleTooNewToJudge: 2, totalActiveDays: 1800,
      medianActiveDays: 8, p25ActiveDays: 3, p75ActiveDays: 14, activeDaysPerActiveHolderMonthly: 9.8, habitShare: 0.45, multiAppHolders: 120,
      agentHolders: 60, coworkHolders: 12,
      bands: [{ band: 'Champion', holders: 40 }],
      unlicensedActiveUsers: 30, unlicensedActiveDays: 120, recommendedCandidates: 9,
      distribution: [
        { key: 'none', minDaysPerMonth: 0, maxDaysPerMonth: 0, holders: 40, breakEvenBucket: false },
        { key: '1to2', minDaysPerMonth: 0, maxDaysPerMonth: 2, holders: 30, breakEvenBucket: false },
        { key: '2to5', minDaysPerMonth: 2, maxDaysPerMonth: 5, holders: 50, breakEvenBucket: true },
        { key: '5to10', minDaysPerMonth: 5, maxDaysPerMonth: 10, holders: 60, breakEvenBucket: false },
        { key: '10to15', minDaysPerMonth: 10, maxDaysPerMonth: 15, holders: 40, breakEvenBucket: false },
        { key: '15plus', minDaysPerMonth: 15, maxDaysPerMonth: null, holders: 20, breakEvenBucket: false },
      ],
    },
    value: {
      available: true,
      missing: [],
      scenarios: [
        { scenario: 'conservative', minutesPerActiveDay: 8, realisation: 0.3, assistedHours: 240, realisedHours: 72, value: 3600, monthlyValue: 3900, roiMultiple: 0.54, net: -3113, holdersAtOrAboveBreakEven: 40 },
        { scenario: 'base', minutesPerActiveDay: 15, realisation: 0.5, assistedHours: 450, realisedHours: 225, value: 11250, monthlyValue: 12200, roiMultiple: 1.68, net: 4537, holdersAtOrAboveBreakEven: 120 },
        { scenario: 'upside', minutesPerActiveDay: 25, realisation: 0.7, assistedHours: 750, realisedHours: 525, value: 26250, monthlyValue: 28500, roiMultiple: 3.91, net: 19537, holdersAtOrAboveBreakEven: 170 },
      ],
      valuePerActiveHolderMonthly: 61,
    },
    breakEven: { available: true, minutesPerActiveDay: 8.9, marginOfSafety: 0.41, activeDaysPerMonthPerSeat: 2.4, minutesPerWeekdayPerSeat: 3.3, holdersAtOrAbove: 120, holdersBelow: 80, holdersNoEvidence: 40, shareAtOrAbove: 0.5 },
    sensitivity: {
      grid: (['conservative', 'base', 'upside'] as const).flatMap((m) =>
        (['conservative', 'base', 'upside'] as const).map((r) => ({ minutesScenario: m, realisationScenario: r, roiMultiple: m === 'conservative' && r === 'conservative' ? 0.54 : 1.5, value: 1000 })),
      ),
      roiBaseHourlyMinus20: 1.34,
      roiBaseHourlyPlus20: 2.02,
      roiBaseIfBalancesMonthly: 1.64,
      roiBaseAfterReclaim: 1.95,
    },
    cohorts: [{ band: 'Champion', holders: 40, medianActiveDays: 18, activeDays: 720, shareOfHolders: 0.17, shareOfActiveDays: 0.4, cost: 1100, valueBase: 4500, roiBase: 4.1, credits: 6000, studioCredits: 3000 }],
    organisation: {
      dimension: 'department', dimensionName: null, foldedGroups: 1, minGroupSize: 5, hidden: false,
      groups: [{ label: 'Ventas \u2013 \u0391\u03b8\u03ae\u03bd\u03b1', emptyKey: null, holders: 60, activeHolders: 55, idleHolders: 5, activeShare: 0.92, activeDaysPerHolderMonthly: 11, cost: 1650, valueBase: 4100, roiBase: 2.48, credits: 7000, creditConsumers: 40, studioCredits: 5000, unlicensedActiveUsers: 4 }],
    },
    concentration: {
      activeDays: { population: 240, topShare: 0.42, topFraction: 0.2, gini: 0.48, lorenz: [0, 0.02, 0.06, 0.12, 0.2, 0.3, 0.4, 0.52, 0.65, 0.8, 1] },
      credits: { population: 150, topShare: 0.55, topFraction: 0.1, gini: 0.71, lorenz: [0, 0.01, 0.02, 0.04, 0.07, 0.1, 0.15, 0.22, 0.3, 0.45, 1] },
      quadrants: [
        { key: 'highSpendStrongEvidence', people: 50, credits: 9000 },
        { key: 'highSpendThinEvidence', people: 25, credits: 7000 },
        { key: 'lowSpendStrongEvidence', people: 45, credits: 1500 },
        { key: 'lowSpendThinEvidence', people: 30, credits: 1000 },
      ],
    },
    metered: { people: 60, activeUsers: 30, activeDays: 120, studioUsers: 20, studioCredits: 12000, studioWindowValue: 120, creditUsers: 25, credits: 3500, creditBalanceValue: 35, studioAboveSeatPrice: 0, balanceAboveSeatPriceIfMonthly: 1, evidencedCandidates: 12, recommendedCandidates: 9 },
    services: [
      { category: 'copilotChatAgents', unit: 'copilotCredit', consumers: 120, quantity: 11000, unknownAmountUsers: 0, limitExhaustedUsers: 4, notEntitledUsers: 10, unevaluatedUsers: 1, policies: ['contoso-demo-policy-all-users'] },
      { category: 'previewAgentService', unit: 'unknownFutureValue', consumers: 20, quantity: 31, unknownAmountUsers: 0, limitExhaustedUsers: 0, notEntitledUsers: 0, unevaluatedUsers: 0, policies: ['contoso-demo-policy-agents-pilot'] },
    ],
    policies: [
      { id: 'p1', displayName: 'Contoso pilot \u2013 \u0395\u03bb\u03bb\u03ac\u03b4\u03b1 (paused)', type: 'selectedGroups', status: 'disabled', billingMethods: 'payAsYouGo', periodLimit: { kind: 'limited', value: 20000, unit: 'copilotCredit' }, perUserLimit: { kind: 'unlimited', value: null, unit: null }, services: [{ serviceId: 'copilotChatAgents', state: 'enabled' }], assignedGroups: 1, modifiedAt: null },
    ],
    tenantBalances: [{ billingMethod: 'prepaidCredits', purchased: 150000, consumed: 10000, remaining: 140000, unit: 'copilotCredit', lastModified: '2026-10-26T04:30:00Z' }],
    actions: [
      { code: 'reclaimIdle', people: 38, monthlyAmount: 1140, credits: null, roiBaseAfter: 1.95, metric: null, signal: null, flag: 'reclaimCandidate', assumesMonthlyBalances: false },
      { code: 'reviewHighSpendThinEvidence', people: 4, monthlyAmount: 42, credits: 4200, roiBaseAfter: null, metric: null, signal: null, flag: 'highSpendThinEvidence', assumesMonthlyBalances: true },
    ],
    findings: [
      { code: 'valueRange', tone: 'warning', values: { low: 3900, base: 12200, high: 28500, roiLow: 0.54, roiBase: 1.68, roiHigh: 3.91, cost: 7300 } },
      { code: 'highSpendThinEvidence', tone: 'warning', values: { people: 4, credits: 4200 } },
    ],
    trust: { level: 'medium', reasons: [{ code: 'balancesUnverified', impact: 'medium', values: {} }] },
    individualDataHidden: false,
    ...overrides,
  };
}

const people: InvestmentPeoplePage = {
  total: 1,
  currency: 'EUR',
  valueAvailable: true,
  rows: [
    {
      userId: 7, userPrincipalName: 'person7@contoso.example', department: 'Sales', jobTitle: null, country: null, company: null, manager: null,
      licensed: true, seatLicences: null, seatDays: 28, seatBasis: 'currentAssignments', band: 'Dormant', adoptionScore: 5, signalSource: 'audit',
      activeDays: 0, activeDaysPerMonth: 0, habitShare: 0, appsUsed: 0, agentsUsed: 0, coworkInteractions: 0, interactions: 0, lastActivityUtc: null,
      tooNewToJudge: false, reclaimEligibility: null, accountEnabled: true, recommendedForLicence: false, credits: 1050, creditsValue: 10.5,
      creditObservationDate: '2026-10-26', studioCredits: 0, studioCost: 0, seatCost: 27.6, cost: 27.6, hoursBase: 0, valueConservative: 0,
      valueBase: 0, valueUpside: 0, valueToCostBase: 0, breakEvenGapDaysPerMonth: 2.4, signal: 'noEvidence', flags: ['highSpendThinEvidence'],
    },
  ],
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchInvestmentAvailability).mockResolvedValue({ adoptionAvailable: true, studioImportEnabled: true, creditCollectionEnabled: true, notes: [] });
  vi.mocked(fetchInvestmentSummary).mockResolvedValue(summary());
  vi.mocked(fetchInvestmentTrend).mockResolvedValue({ points: [], snapshotDate: '2026-10-26', failureCode: null });
  vi.mocked(fetchInvestmentPeople).mockResolvedValue(people);
});

describe('CopilotInvestmentPage', () => {
  it('leads with modelled investment, value, return and break-even, each labelled as modelled', async () => {
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: false, seePii: false } });

    expect(await screen.findByText('Seat run rate')).toBeInTheDocument();
    expect(screen.getByText('Modelled value (base)')).toBeInTheDocument();
    expect(screen.getByText('Modelled return (base)')).toBeInTheDocument();
    expect(screen.getByText('1.7\u00d7')).toBeInTheDocument();
    expect(screen.getByText('Break-even')).toBeInTheDocument();
    expect(screen.getAllByText('Modelled').length).toBeGreaterThanOrEqual(4);
    // Balances are reported beside the return, never inside it.
    expect(screen.getByText('150 people with a balance \u00b7 not part of the return')).toBeInTheDocument();
    expect(screen.getByText(/Observed use is worth/)).toBeInTheDocument();
  });

  it('gives a reader without See PII the aggregates only: no people tab, no own filter, no named drill-down', async () => {
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: false, seePii: false } });

    await screen.findByText('Seat run rate');
    expect(screen.queryByRole('tab', { name: 'People' })).not.toBeInTheDocument();
    expect(screen.queryByText(/See the \d+ people/)).not.toBeInTheDocument();
    expect(screen.queryByText('Edit assumptions')).not.toBeInTheDocument();
    expect(fetchInvestmentPeople).not.toHaveBeenCalled();
    const query = vi.mocked(fetchInvestmentSummary).mock.calls[0][0];
    expect(query.userFilter ?? null).toBeNull();
    expect(query.dimension).toBe('department');
  });

  it('lets a reader with See PII drill from an action into exactly the people it names', async () => {
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: true, seePii: true } });

    fireEvent.click(await screen.findByRole('button', { name: 'See the 4 people' }));
    expect(await screen.findByText('person7@contoso.example')).toBeInTheDocument();
    await waitFor(() => expect(fetchInvestmentPeople).toHaveBeenCalled());
    const filters = vi.mocked(fetchInvestmentPeople).mock.calls.at(-1)![1];
    expect(filters.flag).toBe('highSpendThinEvidence');
    expect(screen.getAllByText('High spend, thin evidence').length).toBeGreaterThan(0);

    vi.mocked(fetchInvestmentPerson).mockResolvedValue({
      person: people.rows[0], currency: 'EUR', activityAssessed: true, apps: [], weeks: [], agentTurns: 0, coworkTurns: 0, totalTurns: 0, activeWeeks: 0,
      promptsAssessed: false, prompts: 0, responses: 0, promptApps: [], promptCategories: [],
      creditServices: [{ category: 'coworkAgent', policyId: 'contoso-demo-policy-all-users', consumed: 1050, unit: 'copilotCredit', allowed: true, decisionReason: 'allowed', perUserLimit: 1200, perUserLimitKind: 'limited', remaining: 150, lastModified: null, unevaluatedReason: null }],
      balances: [], studioAgents: [], evidence: ['noActivity'],
    });
    fireEvent.click(screen.getByRole('button', { name: 'Open evidence for person7@contoso.example' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('No Copilot use in the period')).toBeInTheDocument();
    expect(within(dialog).getByText(/not how productive anyone is/)).toBeInTheDocument();
  });

  it('says which assumptions are missing instead of modelling money with figures nobody chose', async () => {
    vi.mocked(fetchInvestmentSummary).mockResolvedValue(
      summary({
        value: { available: false, missing: ['hourlyCost'], scenarios: [], valuePerActiveHolderMonthly: null },
        breakEven: { available: false, minutesPerActiveDay: null, marginOfSafety: null, activeDaysPerMonthPerSeat: null, minutesPerWeekdayPerSeat: null, holdersAtOrAbove: 0, holdersBelow: 0, holdersNoEvidence: 0, shareAtOrAbove: null },
        findings: [{ code: 'assumptionsMissing', tone: 'warning', values: { missing: 1 } }],
        actions: [{ code: 'setAssumptions', people: 0, monthlyAmount: null, credits: null, roiBaseAfter: null, metric: 1, signal: null, flag: null, assumesMonthlyBalances: false }],
      }),
    );
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: true, seePii: true } });

    expect(await screen.findByText('Value and return are not modelled: 1 assumptions are missing.')).toBeInTheDocument();
    expect(screen.getByText('Value cannot be modelled until the assumptions are set.')).toBeInTheDocument();
    expect(screen.getAllByText('Edit assumptions')[0]).toHaveAttribute('href', '#/admin/copilot-investment-assumptions');
  });

  it('reads in Spanish, with numbers in Spanish conventions and tenant names left as stored', async () => {
    await loadCatalog('es');
    renderWithProvider(<CopilotInvestmentPage />, { language: 'es', access: { administration: false, seePii: false } });

    expect(await screen.findByText('Coste mensual de licencias')).toBeInTheDocument();
    expect(screen.getByText('Rentabilidad modelada (central)')).toBeInTheDocument();
    expect(screen.getByText('1,7\u00d7')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'Organización' }));
    expect(await screen.findByText('Ventas \u2013 \u0391\u03b8\u03ae\u03bd\u03b1')).toBeInTheDocument();
  });

  it('sends what-if figures culture-free and keeps the export on the same scope', () => {
    const query = { windowDays: 90, userFilter: null, dimension: 'companyName', scenario: { hourlyCost: 62.5, realisationBase: 0.45, currency: 'EUR' } };
    const params = queryParams(query);
    expect(params.get('hourlyCost')).toBe('62.5');
    expect(params.get('realisationBase')).toBe('0.45');
    expect(params.get('windowDays')).toBe('90');
    expect(investmentWorkbookUrl(query)).toContain('dimension=companyName');
    expect(investmentWorkbookUrl(query)).toContain('hourlyCost=62.5');
  });
});
