import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, configure, fireEvent, screen, waitFor, within } from '@testing-library/react';

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
  saveInvestmentSettings,
} from '../api/copilotInvestmentApi';
import { ILLUSTRATIVE_SCENARIO } from '../components/copilotInvestment/WhatIfDialog';
import type { CopilotInvestmentSummary, InvestmentPeoplePage } from '../types/copilotInvestment';

// Whole-page tests that open dialogs several times: under a parallel run the Fluent tree renders slowly, so the async
// matchers and each test get the same headroom LicenceActivityPage.test.tsx gives its page tests.
configure({ asyncUtilTimeout: 3000 });
vi.setConfig({ testTimeout: 20000 });

vi.mock('../api/copilotInvestmentApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/copilotInvestmentApi')>()),
  fetchInvestmentAvailability: vi.fn(),
  fetchInvestmentSummary: vi.fn(),
  fetchInvestmentTrend: vi.fn(),
  fetchInvestmentPeople: vi.fn(),
  fetchInvestmentPerson: vi.fn(),
  saveInvestmentSettings: vi.fn(),
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
      seats: {
        holders: 240, analysedHolders: 240, otherPeriodHolders: 0, notAnalysedHolders: 0, basis: 'currentAssignments', seatDays: 6720,
        monthlyRunRate: 7200, windowCost: 6623, analysedWindowCost: 6623, costPerActiveHolderMonthly: 36,
      },
      studio: { users: 40, credits: 21000, priorCredits: 18000, priorUsers: 35, holderCredits: 9000, windowValue: 210, holderWindowValue: 90 },
      credits: {
        observedUsers: 400, consumingUsers: 150, credits: 18500, holderCredits: 15000, nonHolderCredits: 3500, balanceValue: 185,
        periodStatus: 'unverified', otherUnitUsers: 20, unknownAmountUsers: 0, limitExhaustedUsers: 4, nearLimitUsers: 6, notEntitledUsers: 10,
      },
      modelledWindowCost: 6713,
      modelledMonthlyCost: 7300,
      monthlySeatCost: 7200,
      monthlyStudioCost: 100,
    },
    evidence: {
      holders: 240, analysedHolders: 240, activeHolders: 200, activeShare: 0.8333, idleHolders: 40, idleTooNewToJudge: 2, totalActiveDays: 1800,
      medianActiveDays: 8, p25ActiveDays: 3, p75ActiveDays: 14, activeDaysPerActiveHolderMonthly: 9.8, habitShare: 0.45, multiAppHolders: 120,
      agentHolders: 60, coworkHolders: 12,
      bands: [{ band: 'Champion', holders: 40 }],
      unlicensedActiveUsers: 30, unlicensedActiveDays: 120, recommendedCandidates: 9,
      distribution: [
        { key: 'none', minDaysPerMonth: 0, maxDaysPerMonth: 0, holders: 40, belowBreakEven: true },
        { key: 'range', minDaysPerMonth: 0, maxDaysPerMonth: 2, holders: 30, belowBreakEven: true },
        { key: 'range', minDaysPerMonth: 2, maxDaysPerMonth: 2.4, holders: 20, belowBreakEven: true },
        { key: 'range', minDaysPerMonth: 2.4, maxDaysPerMonth: 5, holders: 30, belowBreakEven: false },
        { key: 'range', minDaysPerMonth: 5, maxDaysPerMonth: 10, holders: 60, belowBreakEven: false },
        { key: 'range', minDaysPerMonth: 10, maxDaysPerMonth: 15, holders: 40, belowBreakEven: false },
        { key: 'range', minDaysPerMonth: 15, maxDaysPerMonth: null, holders: 20, belowBreakEven: false },
      ],
    },
    value: {
      available: true,
      unavailableReason: null,
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
    path: {
      available: true, monthlyCost: 7300, monthlyValueBase: 12200, monthlyGap: -4900, valuePerActiveDay: 6.25,
      extraActiveDaysPerMonth: null, extraDaysPerActiveHolder: null, activeHolders: 200, minutesNeeded: 8.9, minutesAssumed: 15,
      reclaimable: 38, reclaimMonthly: 1140, roiAfterReclaim: 1.95, evidencedWithoutSeat: 30, reassignable: 30, netNewSeats: 0, netNewMonthly: 0,
    },
    trajectory: {
      available: true, unavailableReason: null, seatHolders: 240,
      weeks: [
        { weekStart: '2026-09-28T00:00:00Z', activeHolders: 150, share: 0.625 },
        { weekStart: '2026-10-05T00:00:00Z', activeHolders: null, share: null },
        { weekStart: '2026-10-12T00:00:00Z', activeHolders: 170, share: 0.7083 },
        { weekStart: '2026-10-19T00:00:00Z', activeHolders: 180, share: 0.75 },
      ],
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
      quadrantMedianCredits: 42,
      quadrantDaysPerMonth: 4,
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
      { code: 'reclaimIdle', people: 38, monthlyAmount: 1140, credits: null, roiBaseAfter: 1.95, metric: null, signal: null, flag: 'reclaimCandidate', effect: 'freesSeatCost', assumesMonthlyBalances: false },
      { code: 'reviewHighSpendThinEvidence', people: 4, monthlyAmount: 42, credits: 4200, roiBaseAfter: null, metric: null, signal: null, flag: 'highSpendThinEvidence', effect: 'balanceIfMonthly', assumesMonthlyBalances: true },
    ],
    findings: [
      { code: 'valueRange', tone: 'warning', values: { low: 3900, base: 12200, high: 28500, roiLow: 0.54, roiBase: 1.68, roiHigh: 3.91, cost: 7300 } },
      { code: 'highSpendThinEvidence', tone: 'warning', values: { people: 4, credits: 4200 } },
      { code: 'trajectory', tone: 'good', values: { first: 150, last: 180, change: 0.2, weeks: 3, share: 0.75 } },
    ],
    trust: {
      level: 'medium',
      areas: [
        { area: 'evidence', level: 'high' },
        { area: 'cost', level: 'medium' },
        { area: 'assumptions', level: 'high' },
        { area: 'balances', level: 'medium' },
      ],
      reasons: [
        { code: 'seatsCurrentAssignments', impact: 'medium', area: 'cost', values: {} },
        { code: 'balancesUnverified', impact: 'medium', area: 'balances', values: {} },
      ],
    },
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
      valueBase: 0, valueUpside: 0, valueToCostBase: 0, breakEvenDaysPerMonth: 2.4, breakEvenGapDaysPerMonth: 2.4, signal: 'noEvidence', flags: ['highSpendThinEvidence'],
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

/**
 * A dialog opened again in the same test. Fluent's focus manager hides a dialog from assistive technology until focus is
 * inside it, and a busy parallel run can query it before that has happened; moving focus in, as Fluent itself does on
 * open, settles it - and the dialog must then be fully accessible. The page behind is hidden in turn until about 250 ms
 * after the dialog closes, so query the page with findBy* after closing one.
 */
async function reopenedDialog(): Promise<HTMLElement> {
  const dialog = await screen.findByRole('dialog', { hidden: true });
  const first = (await within(dialog).findAllByRole('button', { hidden: true }))[0];
  act(() => first.focus());
  await waitFor(() => expect(dialog).not.toHaveAttribute('aria-hidden'));
  return dialog;
}

describe('CopilotInvestmentPage', () => {
  it('leads with the answer and the modelled figures it rests on, each labelled as modelled', async () => {
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: false, seePii: false } });

    expect(
      await screen.findByText(/the licences return 1\.68\u00d7 in the base case \(0\.54\u00d7 to 3\.91\u00d7\): \u20ac4,900 a month more than their modelled cost/),
    ).toBeInTheDocument();
    expect(screen.getByText('Modelled cost a month')).toBeInTheDocument();
    expect(screen.getByText('\u20ac7,200 in licences + \u20ac100 in Copilot Studio credits')).toBeInTheDocument();
    expect(screen.getByText('Modelled value (base)')).toBeInTheDocument();
    expect(screen.getByText('+\u20ac4,900 a month')).toBeInTheDocument();
    expect(screen.getByText('1.68\u00d7')).toBeInTheDocument();
    expect(screen.getByText('Break-even')).toBeInTheDocument();
    expect(screen.getAllByText('Modelled').length).toBeGreaterThanOrEqual(6);
    // Balances are reported beside the return, never inside it.
    expect(screen.getByText('150 people with consumed credits \u00b7 not part of the return')).toBeInTheDocument();
    // The verdict says the value range once; the findings list does not repeat it.
    expect(screen.queryByText(/is worth \u20ac3,900 to \u20ac28,500/)).not.toBeInTheDocument();
    expect(screen.getByText(/More spend is not more evidence/)).toBeInTheDocument();
  });

  it('states what would change the answer, what each amount is and how far to trust each part', async () => {
    vi.mocked(fetchInvestmentSummary).mockResolvedValue(
      summary({
        path: {
          ...summary().path,
          monthlyGap: 2190, extraActiveDaysPerMonth: 584, extraDaysPerActiveHolder: 2.36, minutesNeeded: 18.8,
          reclaimable: 16, reclaimMonthly: 480, roiAfterReclaim: 0.84, evidencedWithoutSeat: 32, reassignable: 16, netNewSeats: 16, netNewMonthly: 480,
        },
      }),
    );
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: false, seePii: false } });

    expect(await screen.findByText(/\u20ac2,190 a month short of their modelled cost/)).toBeInTheDocument();
    expect(
      screen.getByText('Close the \u20ac2,190 a month shortfall with 584 more active licence-days a month - about 2.4 more for each holder who already uses Copilot.'),
    ).toBeInTheDocument();
    expect(screen.getByText(/frees \u20ac480 a month of licence cost and moves the base return to 0\.84\u00d7/)).toBeInTheDocument();
    expect(screen.getByText(/Reclaimed licences could cover 16 of them; licences for the other 16 would add \u20ac480 a month/)).toBeInTheDocument();
    // Amounts of different kinds never read as one column of savings.
    expect(screen.getByText('Frees \u20ac1,140 a month of licence cost')).toBeInTheDocument();
    expect(screen.getByText('\u20ac42.00 a month if the readings were a month\u2019s spend')).toBeInTheDocument();
    // The bars under break-even are marked in words as well as colour.
    expect(screen.getAllByText(/below break-even/).length).toBe(3);
    expect(screen.getByText('150 of 240 analysed licensed users are at or above break-even; 50 use Copilot less often and 40 not at all.')).toBeInTheDocument();
    // Confidence per question, and the weekly trend in words.
    expect(screen.getByText('Evidence of use')).toBeInTheDocument();
    expect(screen.getByText('Consumed credits (preview readings)')).toBeInTheDocument();
    expect(screen.getByText(/Weekly active licensed users went from 150 to 180/)).toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: /not covered by the audit import/ })).toBeInTheDocument();
  });

  it('gives a reader without See PII the aggregates only: no people tab, no own filter, no named drill-down', async () => {
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: false, seePii: false } });

    await screen.findByText('Modelled cost a month');
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
      balances: [], studioEnvironments: [], evidence: ['noActivity'],
    });
    fireEvent.click(screen.getByRole('button', { name: 'Open evidence for person7@contoso.example' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('No Copilot use in the period')).toBeInTheDocument();
    expect(within(dialog).getByText(/not how productive anyone is/)).toBeInTheDocument();
  });

  it.each(['en', 'es'] as const)('keeps translated signal and flag labels on one line inside their badges (%s)', async (language) => {
    await loadCatalog(language);
    vi.mocked(fetchInvestmentPeople).mockResolvedValue({
      ...people,
      rows: [{ ...people.rows[0], flags: ['highSpendThinEvidence', 'limitExhausted', 'nearLimit'] }],
    });
    renderWithProvider(<CopilotInvestmentPage />, { language });

    fireEvent.click(await screen.findByRole('tab', { name: language === 'es' ? 'Personas' : 'People' }));
    const table = await screen.findByRole('table', { name: language === 'es' ? 'Personas' : 'People' });
    expect(within(table).getByText(language === 'es' ? 'En el límite de créditos' : 'At credit limit')).toBeInTheDocument();
    const badges = table.querySelectorAll('.fui-Badge');
    expect(badges).toHaveLength(4);
    for (const badge of badges) expect(badge).toHaveStyle({ whiteSpace: 'nowrap', flexShrink: '0' });
  });

  it('sorts the whole list by any column header, from the mouse or the keyboard, and keeps the sort control in step', async () => {
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: true, seePii: true } });

    fireEvent.click(await screen.findByRole('tab', { name: 'People' }));
    expect(await screen.findByText('person7@contoso.example')).toBeInTheDocument();
    const header = (name: RegExp) => within(screen.getByRole('table', { name: 'People' })).getByRole('columnheader', { name });
    const lastFilters = () => vi.mocked(fetchInvestmentPeople).mock.calls.at(-1)!;
    expect(header(/Consumed credits/)).toHaveAttribute('aria-sort', 'none');
    expect(header(/Consumed credits/)).toHaveAttribute('title', 'Sort by Consumed credits (latest reading)');

    // A column sorts on the server, from the first person, largest first.
    fireEvent.click(within(header(/Consumed credits/)).getByRole('button'));
    await waitFor(() => expect(lastFilters()[1]).toMatchObject({ sortBy: 'credits', sortDesc: true }));
    expect(lastFilters()[2]).toBe(0);
    await waitFor(() => expect(header(/Consumed credits/)).toHaveAttribute('aria-sort', 'descending'));
    expect(screen.getByRole('combobox', { name: 'Sort by' })).toHaveValue('credits');

    // Enter on the focused header reverses it.
    fireEvent.keyDown(within(header(/Consumed credits/)).getByRole('button'), { key: 'Enter' });
    await waitFor(() => expect(lastFilters()[1]).toMatchObject({ sortBy: 'credits', sortDesc: false }));
    await waitFor(() => expect(header(/Consumed credits/)).toHaveAttribute('aria-sort', 'ascending'));

    // Names start A to Z; the sort control moves the header arrow too.
    fireEvent.click(within(header(/Person/)).getByRole('button'));
    await waitFor(() => expect(lastFilters()[1]).toMatchObject({ sortBy: 'user', sortDesc: false }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Sort by' }), { target: { value: 'signal' } });
    await waitFor(() => expect(header(/Signal/)).toHaveAttribute('aria-sort', 'descending'));
    expect(header(/Person/)).toHaveAttribute('aria-sort', 'none');

    // The licence is a tick or a cross with its words, never colour alone.
    expect(header(/Copilot licence/)).toBeInTheDocument();
    const tick = screen.getByRole('img', { name: 'Has a Microsoft 365 Copilot licence' });
    expect(tick).toHaveAttribute('title', 'Has a Microsoft 365 Copilot licence');
    expect(screen.getByRole('combobox', { name: 'Microsoft 365 Copilot licence' })).toBeInTheDocument();
  });

  it('says a licence is not known, rather than absent, when no adoption evidence was read', async () => {
    const withoutAdoption = summary();
    withoutAdoption.sources = { ...withoutAdoption.sources, adoption: { ...withoutAdoption.sources.adoption, available: false } };
    vi.mocked(fetchInvestmentSummary).mockResolvedValue(withoutAdoption);
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: true, seePii: true } });

    fireEvent.click(await screen.findByRole('tab', { name: 'People' }));
    expect(await screen.findByRole('img', { name: 'Licence not known: no adoption evidence was read' })).toBeInTheDocument();
    expect(screen.queryByRole('img', { name: 'Has a Microsoft 365 Copilot licence' })).not.toBeInTheDocument();
  });

  it('explains why money is withheld, tries clearly labelled example figures in this browser only, and goes back to the organisation figures', async () => {
    const withoutFigures = summary();
    withoutFigures.assumptions = {
      ...withoutFigures.assumptions,
      status: 'missing',
      values: null,
      missingForValue: ['currency', 'seatPricePerMonth', 'hourlyCost'],
    };
    vi.mocked(fetchInvestmentSummary).mockResolvedValue(withoutFigures);
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: true, seePii: true } });

    // The toolbar's button comes first; the executive panel offers the same dialog.
    fireEvent.click((await screen.findAllByRole('button', { name: 'What-if' }))[0]);
    let dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/No organisation figures are set yet/)).toBeInTheDocument();
    expect(within(dialog).getByRole('link', { name: 'Set the organisation\u2019s figures' })).toHaveAttribute('href', '#/admin/copilot-investment-assumptions');
    expect(within(dialog).getAllByText('Not set by the organisation').length).toBeGreaterThan(5);
    expect(within(dialog).getByText(/not Microsoft prices, not your organisation\u2019s figures and not measured savings/)).toBeInTheDocument();
    expect(within(dialog).getByText('Needed to model the return')).toBeInTheDocument();
    expect(within(dialog).getByText('Optional')).toBeInTheDocument();

    // One click applies the example as this reader's what-if: nothing is saved as the organisation's figures.
    fireEvent.click(within(dialog).getByRole('button', { name: 'Try illustrative example figures' }));
    await waitFor(() => expect(vi.mocked(fetchInvestmentSummary).mock.calls.at(-1)![0].scenario).toEqual({ ...ILLUSTRATIVE_SCENARIO }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(saveInvestmentSettings).not.toHaveBeenCalled();
    expect(await screen.findByText(/Illustrative example figures are in use/)).toBeInTheDocument();
    // The Excel export models the same figures as the page.
    await waitFor(() => expect(screen.getByText('Excel report').closest('a')?.getAttribute('href')).toContain('hourlyCost=45'));
    const href = new URL(screen.getByText('Excel report').closest('a')!.getAttribute('href')!, 'https://contoso.example');
    expect(href.searchParams.get('realisationBase')).toBe('0.5');
    expect(href.searchParams.get('seatPrice')).toBe(String(ILLUSTRATIVE_SCENARIO.seatPrice));

    // Clearing the example returns to the organisation's (empty) figures.
    fireEvent.click(screen.getByRole('button', { name: 'Clear example figures' }));
    await waitFor(() => expect(vi.mocked(fetchInvestmentSummary).mock.calls.at(-1)![0].scenario).toBeNull());
    expect(screen.queryByText(/Illustrative example figures are in use/)).not.toBeInTheDocument();

    // Realisation is entered as a percentage and sent as a share; out-of-range figures are refused before anything is sent.
    fireEvent.click(screen.getAllByRole('button', { name: 'What-if' })[0]);
    dialog = await reopenedDialog();
    const realisation = within(dialog).getAllByRole('textbox').at(-4)!;
    fireEvent.change(realisation, { target: { value: '140' } });
    expect(within(dialog).getByText('Enter a number from 0 to 100.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Apply' })).toBeDisabled();
    fireEvent.change(realisation, { target: { value: '40' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Apply' }));
    await waitFor(() => expect(vi.mocked(fetchInvestmentSummary).mock.calls.at(-1)![0].scenario).toEqual({ realisationBase: 0.4 }));
    expect(screen.queryByText(/Illustrative example figures are in use/)).not.toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());

    fireEvent.click(await screen.findByRole('button', { name: 'What-if (1 changed)' }));
    fireEvent.click(within(await reopenedDialog()).getByRole('button', { name: 'Back to organisation defaults' }));
    await waitFor(() => expect(vi.mocked(fetchInvestmentSummary).mock.calls.at(-1)![0].scenario).toBeNull());
  });

  it('tells a reader who cannot set the organisation figures who can', async () => {
    const withoutFigures = summary();
    withoutFigures.assumptions = { ...withoutFigures.assumptions, status: 'missing', values: null, missingForValue: ['hourlyCost'] };
    vi.mocked(fetchInvestmentSummary).mockResolvedValue(withoutFigures);
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: false, seePii: false } });

    fireEvent.click((await screen.findAllByRole('button', { name: 'What-if' }))[0]);
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText(/A portal administrator sets them for everyone under Administration/)).toBeInTheDocument();
    expect(within(dialog).queryByRole('link', { name: 'Set the organisation\u2019s figures' })).not.toBeInTheDocument();
  });

  it('says which assumptions are missing instead of modelling money with figures nobody chose', async () => {
    vi.mocked(fetchInvestmentSummary).mockResolvedValue(
      summary({
        value: { available: false, unavailableReason: 'assumptions', missing: ['hourlyCost'], scenarios: [], valuePerActiveHolderMonthly: null },
        breakEven: { available: false, minutesPerActiveDay: null, marginOfSafety: null, activeDaysPerMonthPerSeat: null, minutesPerWeekdayPerSeat: null, holdersAtOrAbove: 0, holdersBelow: 0, holdersNoEvidence: 0, shareAtOrAbove: null },
        path: { ...summary().path, available: false, monthlyGap: null, reclaimable: 0, evidencedWithoutSeat: 0 },
        findings: [{ code: 'assumptionsMissing', tone: 'warning', values: { missing: 1 } }],
        actions: [{ code: 'setAssumptions', people: 0, monthlyAmount: null, credits: null, roiBaseAfter: null, metric: 1, signal: null, flag: null, effect: 'none', assumesMonthlyBalances: false }],
      }),
    );
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: true, seePii: true } });

    expect(await screen.findByText('Money is not modelled yet: 1 assumptions are missing. The evidence of use below does not depend on them.')).toBeInTheDocument();
    expect(screen.getByText('Value cannot be modelled until the assumptions are set.')).toBeInTheDocument();
    expect(screen.getByText('Set the assumptions to see what would change the answer.')).toBeInTheDocument();
    expect(screen.getAllByText('Edit assumptions')[0]).toHaveAttribute('href', '#/admin/copilot-investment-assumptions');
  });

  it('says plainly when no licensed user\u2019s use was measured in this period, rather than valuing another period', async () => {
    vi.mocked(fetchInvestmentSummary).mockResolvedValue(
      summary({
        value: { available: false, unavailableReason: 'noMatchedHolders', missing: [], scenarios: [], valuePerActiveHolderMonthly: null },
        path: { ...summary().path, available: false, monthlyGap: null, reclaimable: 0, evidencedWithoutSeat: 0 },
        findings: [{ code: 'noMatchedHolders', tone: 'warning', values: { otherPeriod: 240, notAnalysed: 0 } }],
        actions: [],
      }),
    );
    renderWithProvider(<CopilotInvestmentPage />, { access: { administration: false, seePii: false } });

    expect(await screen.findByText(/No licensed user\u2019s Copilot use was measured in this period, so value and return are not modelled/)).toBeInTheDocument();
  });

  it('reads in Spanish, with numbers in Spanish conventions and tenant names left as stored', async () => {
    await loadCatalog('es');
    renderWithProvider(<CopilotInvestmentPage />, { language: 'es', access: { administration: false, seePii: false } });

    expect(await screen.findByText('Coste modelado al mes')).toBeInTheDocument();
    expect(screen.getByText('Rentabilidad modelada (central)')).toBeInTheDocument();
    expect(screen.getByText('1,68\u00d7')).toBeInTheDocument();
    // The workbook is written in the language the page is read in.
    expect(screen.getByText('Informe de Excel').closest('a')?.getAttribute('href')).toContain('language=es');
    fireEvent.click(screen.getByRole('tab', { name: 'Organización' }));
    expect(await screen.findByText('Ventas \u2013 \u0391\u03b8\u03ae\u03bd\u03b1')).toBeInTheDocument();
  });

  it('sends what-if figures culture-free and keeps the export on the same scope, in the reader\u2019s language', () => {
    const query = { windowDays: 90, userFilter: null, dimension: 'companyName', scenario: { hourlyCost: 62.5, realisationBase: 0.45, currency: 'EUR' } };
    const params = queryParams(query);
    expect(params.get('hourlyCost')).toBe('62.5');
    expect(params.get('realisationBase')).toBe('0.45');
    expect(params.get('windowDays')).toBe('90');
    const url = new URL(investmentWorkbookUrl(query, 'es'), 'https://contoso.example');
    expect(url.searchParams.get('dimension')).toBe('companyName');
    expect(url.searchParams.get('hourlyCost')).toBe('62.5');
    expect(url.searchParams.get('language')).toBe('es');
    expect(new URL(investmentWorkbookUrl(query, 'en'), 'https://contoso.example').searchParams.get('language')).toBe('en');
  });
});
