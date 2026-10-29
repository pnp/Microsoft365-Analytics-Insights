import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, configure, fireEvent, screen, waitFor, within } from '@testing-library/react';

import { renderWithProvider } from '../test/renderWithProvider';
import CopilotSpendPage from './CopilotSpendPage';
import { loadCatalog } from '../i18n';
import { fetchSpendPeople, fetchSpendPerson, fetchSpendSummary, fetchSpendTrend, spendWorkbookUrl } from '../api/copilotSpendApi';
import type {
  CopilotSpendSummary,
  SpendBand,
  SpendDay,
  SpendDistribution,
  SpendParetoPoint,
  SpendPeoplePage,
  SpendPersonDetail,
  SpendPersonUsage,
  SpendRankedPerson,
} from '../types/copilotSpend';
import { CopilotResourceTypeKind } from '../types/copilotAdoption';

// Whole-page tests through several tabs and a dialog: the same headroom LicenceActivityPage.test.tsx gives its page tests.
configure({ asyncUtilTimeout: 3000 });
vi.setConfig({ testTimeout: 20000 });

vi.mock('../api/copilotSpendApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/copilotSpendApi')>()),
  fetchSpendSummary: vi.fn(),
  fetchSpendTrend: vi.fn(),
  fetchSpendPeople: vi.fn(),
  fetchSpendPerson: vi.fn(),
}));

vi.mock('../api/userFilterApi', () => ({
  fetchUserFilterDimensions: vi.fn(async () => ({ people: 400, loadedUtc: '2026-10-27T00:00:00Z', dimensions: [] })),
  fetchUserFilterValues: vi.fn(async () => ({ values: [], truncated: false })),
}));
vi.mock('../api/costApiHealthApi', () => ({ fetchCostApiHealth: vi.fn(async () => []) }));

const GREEK = '\u0391\u03b8\u03ae\u03bd\u03b1';

/** A synthetic Contoso view: illustrative figures and rounded counts, no real tenant data. */
function summary(overrides: Partial<CopilotSpendSummary> = {}): CopilotSpendSummary {
  return {
    generatedUtc: '2026-10-27T09:00:00Z',
    fromUtc: '2026-09-30T00:00:00Z',
    toUtc: '2026-10-27T00:00:00Z',
    windowDays: 28,
    filtered: false,
    userFilter: null,
    globalFilter: null,
    studio: {
      importEnabled: true, hasData: true, failureCode: null, lastImportUtc: '2026-10-27T02:00:00Z', lastImportFailed: false,
      earliestDate: '2026-06-01', coversWindow: true, latestDate: '2026-10-25', available: true, credits: 1250, people: 40, unmatchedCredits: 50,
      days: [
        { date: '2026-10-24', status: 'noneRecorded', credits: null, people: null },
        { date: '2026-10-25', status: 'recorded', credits: 1250, people: 40 },
        { date: '2026-10-26', status: 'notReported', credits: null, people: null },
      ],
      agents: [{ agentId: 'agent-1', agentName: 'Contoso helpdesk agent', credits: 1180, days: 2 }],
      agentLedgerCredits: 1180,
      environments: [{ environmentId: 'env-1', environmentName: `Contoso \u2013 ${GREEK}`, emptyKey: null, folded: 0, withheld: false, credits: 1250, people: 40, days: 2 }],
      breakdownFailureCode: null,
    },
    balances: {
      tablesPresent: true, collectionEnabled: true, hasData: true, available: true, failureCode: null, snapshotDate: '2026-10-26', snapshotBeforeWindow: false, snapshotBandFrom: '2026-10-24',
      firstObservationDate: '2026-07-01', periodStatus: 'unverified', oldestFreshness: '2026-10-25T00:00:00Z', newestFreshness: '2026-10-26T00:00:00Z',
      credits: 9800, observedPeople: 300, consumingPeople: 90, partialPeople: 3, unknownAmountPeople: 4, otherUnitPeople: 1,
      limitExhaustedPeople: 2, notEntitledPeople: 5, unresolvedAccounts: 3, unresolvedCredits: 120, sweepsInWindow: 28, completedSweepsInWindow: 26,
      services: [
        { category: 'copilotChatAgents', unit: 'copilotCredit', consumers: 80, quantity: 9000, unknownAmountUsers: 4, limitExhaustedUsers: 2, notEntitledUsers: 5, unevaluatedUsers: 1, policies: ['policy-1'] },
      ],
      policies: [
        { id: 'policy-1', displayName: `Contoso pilot \u2013 ${GREEK}`, type: 'allUsers', status: 'enabled', billingMethods: 'payAsYouGo', periodLimit: null, perUserLimit: null, services: [{ serviceId: 'copilotChatAgents', state: 'enabled' }], assignedGroups: null, modifiedAt: null },
      ],
      tenantBalances: [],
    },
    organisation: {
      dimension: 'department', dimensionName: null, minGroupSize: 5, foldedGroups: 2, moreGroups: 0, unavailableCode: null, hidden: false,
      groups: [
        { label: `Ventas \u2013 ${GREEK}`, emptyKey: null, folded: 0, withheld: false, people: 30, studioPeople: 25, studioCredits: 900, shareOfStudio: 0.75, balancePeople: 20, balanceCredits: 6000, shareOfBalances: 0.62, unknownAmountPeople: 2 },
        { label: null, emptyKey: 'otherSmallGroups', folded: 2, withheld: false, people: 7, studioPeople: 5, studioCredits: 300, shareOfStudio: 0.25, balancePeople: 4, balanceCredits: 3680, shareOfBalances: 0.38, unknownAmountPeople: 2 },
      ],
    },
    peopleCount: 120,
    individualDataHidden: false,
    ...overrides,
  };
}

const people: SpendPeoplePage = {
  total: 2,
  rows: [
    {
      userId: 7, userPrincipalName: 'person7@contoso.example', department: 'Sales', jobTitle: 'Analyst', company: null, country: null, manager: 'lead3@contoso.example',
      studioCredits: 120.5, studioDays: 4, balanceCredits: 1050, balanceDate: '2026-10-26', balancePartial: false, unknownAmounts: false,
      otherUnits: false, limitExhausted: true, notEntitled: false,
    },
    {
      userId: 9, userPrincipalName: 'person9@contoso.example', department: GREEK, jobTitle: null, company: null, country: null, manager: null,
      studioCredits: 40, studioDays: 2, balanceCredits: null, balanceDate: null, balancePartial: false, unknownAmounts: false,
      otherUnits: false, limitExhausted: false, notEntitled: false,
    },
  ],
};

/** The period's calendar days, 30 Sep to 26 Oct 2026, as the server lists them for one person. */
const WINDOW = Array.from({ length: 27 }, (_, i) => new Date(Date.UTC(2026, 8, 30 + i)).toISOString().slice(0, 10));

/** One person's ledger days: charges on the given days, the ledger not yet reaching 26 Oct, and a known zero on every other day. */
function ledgerDays(recorded: Record<string, number>): SpendDay[] {
  return WINDOW.map((date): SpendDay => {
    if (recorded[date] !== undefined) return { date, status: 'recorded', credits: recorded[date], people: 1 };
    if (date === '2026-10-26') return { date, status: 'notReported', credits: null, people: null };
    return { date, status: 'noneRecorded', credits: null, people: null };
  });
}

const ranked = (rows: [number, string | null, number, number][]): SpendRankedPerson[] =>
  rows.map(([userId, department, credits, share]) => ({ userId, userPrincipalName: `person${userId}@contoso.example`, department, credits, share }));
const bands = (rows: [number, number, number, number | null][]): SpendBand[] => rows.map(([from, to, people, credits]) => ({ from, to, people, credits }));
const pareto = (consumers: number, rows: [number, number][]): SpendParetoPoint[] => [
  { people: 0, peopleShare: 0, creditShare: 0 },
  ...rows.map(([people, creditShare]) => ({ people, peopleShare: people / consumers, creditShare })),
];

// The spreads below are what the server's algorithms compute for synthetic Contoso populations: the 1,200 Studio credits
// matched to 40 people (the summary's 1,250 less 50 unmatched), and the 9,680 consumed credits read for 90 people.
const STUDIO_SPREAD: SpendDistribution = {
  consumers: 40, minPeople: 5, zeroPeople: null, total: 1200, median: 14.7, mean: 30, p90: 49.2, min: 0.4, max: 340,
  gini: 0.6098, top10Share: 0.5105, top20Share: 0.6508, peopleForHalf: 4, halfFromFewerThanMin: false,
  pareto: pareto(40, [[1, 0.2833], [2, 0.3838], [3, 0.4597], [4, 0.5105], [8, 0.6508], [13, 0.7682], [18, 0.8492], [23, 0.9085], [28, 0.9519], [33, 0.9819], [40, 1]]),
  bands: bands([
    [0.2, 0.5, 1, 0.4], [0.5, 1, 0, 0], [1, 2, 1, 1.5], [2, 5, 4, 14.4], [5, 10, 8, 60.2],
    [10, 20, 11, 158.9], [20, 50, 11, 352], [50, 100, 2, 152.1], [100, 200, 1, 120.5], [200, 500, 1, 340],
  ]),
  top: ranked([
    [3, 'Finance', 340, 0.2833], [7, 'Sales', 120.5, 0.1004], [54, 'Legal', 91.1, 0.0759], [53, 'Operations', 61, 0.0508], [52, 'Marketing', 49.2, 0.041],
    [51, 'Finance', 42.1, 0.0351], [9, GREEK, 40, 0.0333], [50, 'Sales', 37.1, 0.0309], [49, 'Legal', 33.3, 0.0278], [48, 'Operations', 30.3, 0.0252],
  ]),
  lowest: ranked([
    [11, null, 0.4, 0.0003], [12, GREEK, 1.5, 0.0012], [20, 'Sales', 2.2, 0.0018], [21, 'Finance', 3.3, 0.0028], [22, 'Marketing', 4.1, 0.0034],
    [23, 'Operations', 4.8, 0.004], [24, 'Legal', 5.4, 0.0045], [25, 'Sales', 6, 0.005], [26, 'Finance', 6.6, 0.0055], [27, 'Marketing', 7.2, 0.006],
  ]),
  withheld: false,
};

/** The same spread for a reader without See PII: nobody named, no one person's amount, and every band at least five people. */
const STUDIO_SPREAD_ANONYMISED: SpendDistribution = {
  ...STUDIO_SPREAD,
  min: null, max: null, top10Share: null, peopleForHalf: null, halfFromFewerThanMin: true,
  pareto: pareto(40, [[8, 0.6508], [13, 0.7682], [18, 0.8492], [23, 0.9085], [28, 0.9519], [33, 0.9819], [40, 1]]),
  bands: bands([[0.2, 5, 6, null], [5, 10, 8, null], [10, 20, 11, null], [20, 500, 15, null]]),
  top: [], lowest: [],
};

const BALANCE_SPREAD: SpendDistribution = {
  consumers: 90, minPeople: 5, zeroPeople: 205, total: 9680, median: 60.3, mean: 107.555556, p90: 221.8, min: 2, max: 1050,
  gini: 0.5542, top10Share: 0.4286, top20Share: 0.5955, peopleForHalf: 13, halfFromFewerThanMin: false,
  pareto: pareto(90, [
    [2, 0.1869], [4, 0.2807], [5, 0.3167], [7, 0.3776], [9, 0.4286], [11, 0.4731], [13, 0.5125], [18, 0.5955], [23, 0.6628], [29, 0.7292], [34, 0.7755],
    [40, 0.8227], [45, 0.8562], [50, 0.8853], [56, 0.915], [61, 0.9361], [67, 0.9572], [72, 0.9717], [77, 0.9835], [83, 0.994], [90, 1],
  ]),
  bands: bands([
    [2, 5, 2, 6.8], [5, 10, 2, 16.3], [10, 20, 9, 136.9], [20, 50, 25, 848.9], [50, 100, 24, 1710.7],
    [100, 200, 17, 2381.2], [200, 500, 8, 2267.9], [500, 1000, 2, 1261.3], [1000, 2000, 1, 1050],
  ]),
  top: ranked([
    [7, 'Sales', 1050, 0.1085], [147, 'Marketing', 758.9, 0.0784], [146, 'Finance', 502.4, 0.0519], [145, 'Sales', 405.5, 0.0419], [144, 'Legal', 348.6, 0.036],
    [143, 'Operations', 309.3, 0.032], [142, 'Marketing', 280, 0.0289], [141, 'Finance', 256.8, 0.0265], [140, 'Sales', 237.8, 0.0246], [139, 'Legal', 221.8, 0.0229],
  ]),
  lowest: ranked([
    [13, 'Finance', 2, 0.0002], [60, 'Sales', 4.8, 0.0005], [61, 'Finance', 7.3, 0.0008], [62, 'Marketing', 9, 0.0009], [63, 'Operations', 10.5, 0.0011],
    [64, 'Legal', 11.8, 0.0012], [65, 'Sales', 13, 0.0013], [66, 'Finance', 14.2, 0.0015], [67, 'Marketing', 15.3, 0.0016], [68, 'Operations', 16.4, 0.0017],
  ]),
  withheld: false,
};

const BALANCE_SPREAD_ANONYMISED: SpendDistribution = {
  ...BALANCE_SPREAD,
  min: null, max: null,
  pareto: pareto(90, [
    [9, 0.4286], [18, 0.5955], [23, 0.6628], [29, 0.7292], [34, 0.7755], [40, 0.8227], [45, 0.8562], [50, 0.8853],
    [56, 0.915], [61, 0.9361], [67, 0.9572], [72, 0.9717], [77, 0.9835], [83, 0.994], [90, 1],
  ]),
  bands: bands([[2, 20, 13, null], [20, 50, 25, null], [50, 100, 24, null], [100, 200, 17, null], [200, 2000, 11, null]]),
  top: [], lowest: [],
};

const NO_FIGURES: Omit<SpendDistribution, 'consumers' | 'minPeople' | 'zeroPeople' | 'withheld'> = {
  total: null, median: null, mean: null, p90: null, min: null, max: null, gini: null, top10Share: null, top20Share: null,
  peopleForHalf: null, halfFromFewerThanMin: false, pareto: [], bands: [], top: [], lowest: [],
};
const ONE_PERSON_STUDIO: SpendDistribution = { ...NO_FIGURES, consumers: 1, minPeople: 5, zeroPeople: null, withheld: true };
const NOBODY_BALANCES: SpendDistribution = { ...NO_FIGURES, consumers: 0, minPeople: 5, zeroPeople: 1, total: 0, withheld: false };

function withSpread(studio: SpendDistribution, balances: SpendDistribution, overrides: Partial<CopilotSpendSummary> = {}): CopilotSpendSummary {
  const base = summary(overrides);
  return { ...base, studio: { ...base.studio, distribution: studio }, balances: { ...base.balances, distribution: balances } };
}

/** Interactions and interactions with agents on the days person7 used Copilot; every other day of the period is a zero. */
const PERSON7_DAYS: Record<string, [number, number]> = {
  '2026-10-20': [40, 14], '2026-10-22': [52, 18], '2026-10-23': [38, 12], '2026-10-25': [44, 16], '2026-10-26': [12, 6],
};

const PERSON7_USAGE: SpendPersonUsage = {
  turnsAssessed: true, turns: 186, noAgentTurns: 120, agentTurns: 48, coworkTurns: 12, makerTestTurns: 6, activeDays: 5, estimateCredits: 9.6, pricedTurns: 24,
  days: WINDOW.map((date) => ({ date, turns: PERSON7_DAYS[date]?.[0] ?? 0, agentTurns: PERSON7_DAYS[date]?.[1] ?? 0 })),
  apps: [
    { app: 'Teams', count: 82, days: 5 },
    { app: 'Word', count: 46, days: 4 },
    { app: 'Outlook', count: 40, days: 3 },
    { app: 'cowork', count: 12, days: 2 },
    { app: 'Copilot Studio', count: 6, days: 1 },
  ],
  agents: [
    { agentId: 'agent-1', name: 'Contoso helpdesk agent', kindKey: 'customEngine', originKey: 'customerBuilt', cowork: false, turns: 30, testTurns: 6, estimateCredits: 9.6, pricedTurns: 24, days: 4, lastDate: '2026-10-25' },
    { agentId: 'agent-2', name: 'Contoso policy assistant', kindKey: 'declarative', originKey: 'customerBuilt', cowork: false, turns: 24, testTurns: 0, estimateCredits: null, pricedTurns: 0, days: 3, lastDate: '2026-10-26' },
    { agentId: 'Copilot.M365Copilot.CoworkChat', name: 'Cowork', kindKey: 'unknown', originKey: 'microsoft', cowork: true, turns: 12, testTurns: 0, estimateCredits: null, pricedTurns: 0, days: 2, lastDate: '2026-10-23' },
  ],
  promptsAssessed: true,
  prompts: {
    prompts: 64, conversations: 21, days: 5, averageWords: 23.4, withAttachments: 16, withLinks: 9, withMentions: 5, withContext: 10,
    firstUtc: '2026-10-20T08:00:00Z', lastUtc: '2026-10-26T14:05:00Z',
  },
  promptApps: [
    { app: 'IPM.SkypeTeams.Message.Copilot.Teams', count: 30, days: 4 },
    { app: 'IPM.SkypeTeams.Message.Copilot.Word', count: 22, days: 3 },
    { app: 'IPM.SkypeTeams.Message.Copilot.BizChat', count: 12, days: 2 },
  ],
  categoriesAssessed: true,
  categories: [
    { categoryId: 'content-drafting', name: null, nameKey: 'promptCategories.defaults.content-drafting.name', humanMode: 'directing', prompts: 26, lastUtc: '2026-10-26T14:05:00Z' },
    { categoryId: 'meeting-summary', name: null, nameKey: 'promptCategories.defaults.meeting-summary.name', humanMode: 'supervising', prompts: 18, lastUtc: '2026-10-25T16:00:00Z' },
    { categoryId: 'other', name: null, nameKey: null, humanMode: null, prompts: 6, lastUtc: '2026-10-23T11:00:00Z' },
  ],
  notClassifiedPrompts: 14,
  phrasesAssessed: true,
  phrases: [
    { phrase: 'quarterly forecast', prompts: 12, lastUtc: '2026-10-26T14:05:00Z' },
    { phrase: 'pricing', prompts: 9, lastUtc: '2026-10-26T14:05:00Z' },
    { phrase: GREEK, prompts: 5, lastUtc: '2026-10-23T11:00:00Z' },
  ],
  recent: [
    {
      createdUtc: '2026-10-26T14:05:00Z', appClass: 'IPM.SkypeTeams.Message.Copilot.Word', words: 31, attachments: 1, links: 0, mentions: 1, context: 2,
      categoryId: 'content-drafting', categoryName: null, categoryNameKey: 'promptCategories.defaults.content-drafting.name', phrases: ['quarterly forecast', 'pricing'],
    },
    {
      createdUtc: '2026-10-25T09:30:00Z', appClass: null, words: 0, attachments: 0, links: 0, mentions: 0, context: 0,
      categoryId: null, categoryName: null, categoryNameKey: null, phrases: [],
    },
  ],
  resourcesAssessed: true,
  resourceTypes: [{ label: 'docx', value: 12, kind: CopilotResourceTypeKind.TenantContent }],
};

const PERSON7: SpendPersonDetail = {
  person: people.rows[0],
  ranks: { studioRank: 2, studioConsumers: 40, studioShare: 0.1004, balanceRank: 1, balanceConsumers: 90, balanceShare: 0.1085 },
  services: [
    {
      category: 'copilotChatAgents', policyId: 'policy-1', consumed: 1050, unit: 'copilotCredit', allowed: true, decisionReason: 'limitExhausted',
      perUserLimit: 1050, perUserLimitKind: 'limited', remaining: 0, lastModified: null, unevaluatedReason: null,
    },
  ],
  // Out of date order on purpose: the dialog draws the readings in date order whatever order they arrive in.
  balances: [
    { date: '2026-10-26', credits: 1050, quality: 2, limitExhausted: true },
    { date: '2026-10-24', credits: null, quality: 1, limitExhausted: false },
    { date: '2026-10-25', credits: 900, quality: 2, limitExhausted: false },
  ],
  studioEnvironments: [{ environmentId: 'env-1', environmentName: `Contoso \u2013 ${GREEK}`, credits: 120.5, activeDays: 4 }],
  studioDays: ledgerDays({ '2026-10-20': 30.5, '2026-10-22': 42, '2026-10-23': 18, '2026-10-25': 30 }),
  usage: PERSON7_USAGE,
  usageFailureCode: null,
};

/** Billed Studio usage but no reading, on a deployment without the Copilot audit data or the interaction history. */
const PERSON9: SpendPersonDetail = {
  person: people.rows[1],
  ranks: { studioRank: 7, studioConsumers: 40, studioShare: 0.0333, balanceRank: null, balanceConsumers: 90, balanceShare: null },
  services: [],
  balances: [],
  studioEnvironments: [{ environmentId: 'env-1', environmentName: `Contoso \u2013 ${GREEK}`, credits: 40, activeDays: 2 }],
  studioDays: ledgerDays({ '2026-10-21': 25, '2026-10-24': 15 }),
  usage: {
    turnsAssessed: false, turns: 0, noAgentTurns: 0, agentTurns: 0, coworkTurns: 0, makerTestTurns: 0, activeDays: 0, estimateCredits: null, pricedTurns: 0,
    days: [], apps: [], agents: [], promptsAssessed: false, prompts: null, promptApps: [], categoriesAssessed: false, categories: [], notClassifiedPrompts: 0,
    phrasesAssessed: false, phrases: [], recent: [], resourcesAssessed: false, resourceTypes: [],
  },
  usageFailureCode: null,
};

/** The interactions were read, but the prompts and the referenced content could not be, and nor could the ledger days. */
const PERSON7_FAILED: SpendPersonDetail = {
  ...PERSON7,
  studioDays: null,
  usage: {
    ...PERSON7_USAGE,
    prompts: null, promptApps: [], recent: [], categoriesAssessed: false, categories: [], phrasesAssessed: false, phrases: [],
    resourcesAssessed: false, resourceTypes: [],
  },
  usageFailureCode: 'personUsageUnavailable',
};

/** The text printed after a label: a tile's or a fact's value. */
const valueAfter = (scope: HTMLElement, label: string) => within(scope).getByText(label).nextElementSibling?.textContent;
/** A bar's printed value: label, then the bar's track, then the value. */
const barValue = (scope: HTMLElement, label: string) => within(scope).getByText(label).nextElementSibling?.nextElementSibling?.textContent;
/** The part of the dialog under a sub-heading. */
const part = (scope: HTMLElement, heading: string) => within(scope).getByRole('heading', { name: heading }).parentElement!;
/** One of the dialog's headline figures, by its label. */
const kpi = (dialog: HTMLElement, label: string) => within(within(dialog).getByText(label).parentElement!);

/**
 * The person dialog, once focus is inside it. Without layout, Fluent finds nothing to focus in jsdom and focuses the
 * surface itself, which never activates the focus manager's modal; about 250 ms later the manager hides the inactive
 * dialog from assistive technology, so a run slower than that loses every role query inside it (CI run 40758824589).
 * Focusing Close, which stays mounted while the details load, activates it as Fluent intends. The page behind is then
 * hidden until about 250 ms after the dialog closes, so query the page with findBy* after closing.
 */
async function openedDialog(): Promise<HTMLElement> {
  const dialog = await screen.findByRole('dialog', { hidden: true });
  const close = within(dialog).getByRole('button', { name: 'Close', hidden: true });
  act(() => close.focus());
  await waitFor(() => expect(dialog).not.toHaveAttribute('aria-hidden'));
  return dialog;
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchSpendSummary).mockResolvedValue(summary());
  vi.mocked(fetchSpendTrend).mockResolvedValue({ points: [{ date: '2026-10-26', observedUsers: 300, consumingUsers: 90, credits: 9800, limitExhaustedUsers: 2 }], snapshotDate: '2026-10-26', failureCode: null });
  vi.mocked(fetchSpendPeople).mockResolvedValue(people);
});

describe('CopilotSpendPage', () => {
  it('shows both sources side by side and never adds them, with no prices or judgements, to a reader without See PII', async () => {
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: false } });

    expect(await screen.findByText('Credits billed in the period')).toBeInTheDocument();
    expect(screen.getByText('1,250')).toBeInTheDocument();
    expect(screen.getAllByText('Consumed credits (latest reading)').length).toBeGreaterThan(0);
    expect(screen.getByText('9,800')).toBeInTheDocument();
    expect(screen.queryByText('11,050')).not.toBeInTheDocument();
    expect(screen.getByText('Latest reading, not a bill')).toBeInTheDocument();
    expect(screen.getByText('Period not confirmed by the source')).toBeInTheDocument();
    expect(screen.getByText(/Two sources, kept apart/)).toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: '25 Oct 2026: 1,250 credits, 40 people' })).toBeInTheDocument();
    // Every calendar day keeps its place: a day with nothing recorded, and one after the ledger's latest day, said in words.
    expect(screen.getByRole('listitem', { name: '24 Oct 2026: no charges recorded' })).toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: '26 Oct 2026: not in the ledger yet' })).toBeInTheDocument();
    expect(screen.getByText(/Latest day in the ledger: 25 Oct 2026\./)).toBeInTheDocument();
    expect(screen.getByText(`Ventas \u2013 ${GREEK}`)).toBeInTheDocument();
    expect(screen.getByText('Other small groups (2)')).toBeInTheDocument();
    // Nothing here models money or recommends anything.
    expect(screen.queryByText(/break-even|return|reclaim|EUR|\u20ac/i)).not.toBeInTheDocument();
    // Aggregates only: no people tab, no own filter, no manager grouping requested.
    expect(screen.queryByRole('tab', { name: 'People' })).not.toBeInTheDocument();
    expect(fetchSpendPeople).not.toHaveBeenCalled();
    const query = vi.mocked(fetchSpendSummary).mock.calls[0][0];
    expect(query.userFilter ?? null).toBeNull();
    expect(query.dimension).toBe('department');
    expect(screen.getByText('Excel report').closest('a')?.getAttribute('href')).toBe(spendWorkbookUrl(query, 'en'));

    // Studio: the per-person ledger by environment adds up; the agent ledger is its own route with its own total, never added.
    fireEvent.click(screen.getByRole('tab', { name: 'Copilot Studio billed usage' }));
    const environments = await screen.findByRole('table', { name: 'By environment' });
    expect(within(environments).getByText(`Contoso \u2013 ${GREEK}`)).toBeInTheDocument();
    const agents = screen.getByRole('table', { name: 'By agent (tenant agent ledger)' });
    expect(within(agents).getByText('Contoso helpdesk agent')).toBeInTheDocument();
    expect(screen.getByText(/its total of 1,180 credits can differ from the per-person total above and is never added to it/)).toBeInTheDocument();
    expect(screen.queryByText('2,430')).not.toBeInTheDocument();
  });

  it('describes the balances as readings with their freshness, and keeps tenant-wide context out of a filtered view', async () => {
    vi.mocked(fetchSpendSummary).mockResolvedValue(
      summary({
        filtered: true,
        studio: { ...summary().studio, unmatchedCredits: null, agents: null, agentLedgerCredits: null },
        balances: { ...summary().balances, policies: [], unresolvedAccounts: null, unresolvedCredits: null },
      }),
    );
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: false } });

    fireEvent.click(await screen.findByRole('tab', { name: 'Copilot credits consumed (readings)' }));
    expect(await screen.findByText('Source last changed the readings')).toBeInTheDocument();
    expect(screen.getByText('Not confirmed by the source')).toBeInTheDocument();
    expect(screen.getByText(/Readings on different dates can count the same consumption again/)).toBeInTheDocument();
    expect(await screen.findByRole('listitem', { name: '26 Oct 2026: 9,800 credits, 90 people with consumed credits' })).toBeInTheDocument();
    expect(screen.getByText(/a filtered view does not show them/)).toBeInTheDocument();
    expect(screen.queryByText('Accounts not matched to a person')).not.toBeInTheDocument();

    // The agent ledger is not kept per person, so a filtered view says why it is missing rather than showing the tenant's.
    fireEvent.click(screen.getByRole('tab', { name: 'Copilot Studio billed usage' }));
    expect(await screen.findByText(/Not shown in a filtered view: the agent ledger is recorded per agent/)).toBeInTheDocument();
    expect(screen.queryByText('Contoso helpdesk agent')).not.toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'By environment' })).toBeInTheDocument();
  });

  it.each(['en', 'es'] as const)('keeps translated people notes on one line inside their badges (%s)', async (language) => {
    await loadCatalog(language);
    vi.mocked(fetchSpendPeople).mockResolvedValue({
      ...people,
      rows: [{ ...people.rows[0], balancePartial: true, unknownAmounts: true, otherUnits: true, notEntitled: true }],
    });
    renderWithProvider(<CopilotSpendPage />, { language });

    fireEvent.click(await screen.findByRole('tab', { name: language === 'es' ? 'Personas' : 'People' }));
    const table = await screen.findByRole('table', { name: language === 'es' ? 'Personas' : 'People' });
    expect(within(table).getByText(language === 'es' ? 'En el límite de créditos' : 'At credit limit')).toBeInTheDocument();
    const badges = table.querySelectorAll('.fui-Badge');
    expect(badges).toHaveLength(5);
    for (const badge of badges) expect(badge).toHaveStyle({ whiteSpace: 'nowrap', flexShrink: '0' });
  });

  it('lists people for a reader with See PII, sorted on the server by any column header, with no reading shown as absent', async () => {
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: true } });

    fireEvent.click(await screen.findByRole('tab', { name: 'People' }));
    expect(await screen.findByText('person7@contoso.example')).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'People' });
    const header = (name: RegExp) => within(table).getByRole('columnheader', { name });
    expect(header(/Studio credits/)).toHaveAttribute('aria-sort', 'descending');
    expect(screen.getByRole('img', { name: 'No reading of Copilot credits consumed' })).toBeInTheDocument();
    expect(screen.getByText('At credit limit')).toBeInTheDocument();
    expect(within(table).getByText('lead3@contoso.example')).toBeInTheDocument();
    expect(header(/Manager/)).toHaveAttribute('aria-sort', 'none');

    fireEvent.click(within(header(/Consumed credits/)).getByRole('button'));
    await waitFor(() => expect(vi.mocked(fetchSpendPeople).mock.calls.at(-1)![1]).toMatchObject({ sortBy: 'balanceCredits', sortDesc: true }));
    expect(vi.mocked(fetchSpendPeople).mock.calls.at(-1)![2]).toBe(0);
    fireEvent.keyDown(within(header(/Consumed credits/)).getByRole('button'), { key: 'Enter' });
    await waitFor(() => expect(vi.mocked(fetchSpendPeople).mock.calls.at(-1)![1]).toMatchObject({ sortBy: 'balanceCredits', sortDesc: false }));
    expect(screen.getByRole('combobox', { name: 'Sort by' })).toHaveValue('balanceCredits');

    fireEvent.change(screen.getByRole('combobox', { name: 'Source' }), { target: { value: 'studio' } });
    await waitFor(() => expect(vi.mocked(fetchSpendPeople).mock.calls.at(-1)![1]).toMatchObject({ origin: 'studio' }));

    vi.mocked(fetchSpendPerson).mockResolvedValue({
      person: people.rows[0],
      services: [{ category: 'coworkAgent', policyId: 'policy-1', consumed: 1050, unit: 'copilotCredit', allowed: true, decisionReason: 'limitExhausted', perUserLimit: 1050, perUserLimitKind: 'limited', remaining: 0, lastModified: null, unevaluatedReason: null }],
      balances: [{ date: '2026-10-25', credits: 900, quality: 2, limitExhausted: false }, { date: '2026-10-26', credits: 1050, quality: 2, limitExhausted: true }],
      studioEnvironments: [{ environmentId: 'env-1', environmentName: 'Contoso (default)', credits: 120.5, activeDays: 4 }],
    });
    fireEvent.click(screen.getByRole('button', { name: 'Open the consumption of person7@contoso.example' }));
    const dialog = await openedDialog();
    expect(within(dialog).getByText('Billed Copilot Studio usage by environment')).toBeInTheDocument();
    expect(within(dialog).getByText('Contoso (default)')).toBeInTheDocument();
    expect(within(dialog).getByText(/^\d[\d,.]* credits on 4 days$/)).toBeInTheDocument();
    expect(within(dialog).getByText(/26 Oct 2026: 1,050 credits/)).toBeInTheDocument();
    expect(within(dialog).getByText('Each reading is the credits consumed as of that date, not a day\u2019s spend.')).toBeInTheDocument();
  });

  it('reads in Spanish, with Spanish numbers and the export in Spanish, and tenant names left as stored', async () => {
    await loadCatalog('es');
    renderWithProvider(<CopilotSpendPage />, { language: 'es', access: { administration: false, seePii: false } });

    expect(await screen.findByText('Créditos facturados en el periodo')).toBeInTheDocument();
    expect(screen.getByText('Gasto en Copilot')).toBeInTheDocument();
    expect(screen.getByText('9800')).toBeInTheDocument();
    expect(screen.getByText(`Ventas \u2013 ${GREEK}`)).toBeInTheDocument();
    expect(screen.getByText('Informe de Excel').closest('a')?.getAttribute('href')).toContain('language=es');
  });

  it('keeps the page and its Group by control while a new grouping loads, and drops stale or failed answers', async () => {
    const pending: { resolve: (s: CopilotSpendSummary) => void; reject: (e: Error) => void; signal?: AbortSignal }[] = [];
    vi.mocked(fetchSpendSummary).mockImplementation((query, signal) =>
      query.dimension === 'department' ? Promise.resolve(summary()) : new Promise((resolve, reject) => pending.push({ resolve, reject, signal })),
    );
    const grouped = (dimension: string, label: string) =>
      summary({ organisation: { ...summary().organisation, dimension, groups: [{ ...summary().organisation.groups[0], label }] } });
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: true } });

    fireEvent.click(await screen.findByRole('tab', { name: 'Organisation' }));
    const select = screen.getByRole('combobox', { name: 'Group by' });
    select.focus();
    fireEvent.change(select, { target: { value: 'country' } });
    // The previous grouping stays, labelled as what it is; the control the reader is using is never unmounted.
    expect(await screen.findByText('Grouping by Country or region\u2026')).toBeInTheDocument();
    expect(within(screen.getByRole('table', { name: 'Organisation' })).getByRole('columnheader', { name: 'Department' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Group by' })).toBe(select);
    expect(document.activeElement).toBe(select);

    // Changed again before the first answer: that answer is stale and never shown.
    fireEvent.change(select, { target: { value: 'companyName' } });
    await waitFor(() => expect(pending.length).toBe(2));
    expect(pending[0].signal?.aborted).toBe(true);
    pending[1].resolve(grouped('companyName', 'Contoso Ltd'));
    expect(await screen.findByText('Contoso Ltd')).toBeInTheDocument();
    pending[0].resolve(grouped('country', 'Spain'));
    await new Promise((r) => setTimeout(r, 0));
    expect(screen.queryByText('Spain')).not.toBeInTheDocument();
    expect(within(screen.getByRole('table', { name: 'Organisation' })).getByRole('columnheader', { name: 'Company' })).toBeInTheDocument();

    // A regrouping that fails keeps the last good table and says what went wrong.
    fireEvent.change(select, { target: { value: 'country' } });
    await waitFor(() => expect(pending.length).toBe(3));
    pending[2].reject(new Error('Could not load the grouping'));
    expect(await screen.findByText('Could not load the grouping')).toBeInTheDocument();
    expect(screen.getByText('Contoso Ltd')).toBeInTheDocument();
  });

  it('shows a source that is off as not available rather than zero, keeps a known zero, and says why a row or a grouping has no figures', async () => {
    const groups = summary().organisation.groups;
    vi.mocked(fetchSpendSummary).mockResolvedValue(
      summary({
        studio: {
          ...summary().studio, importEnabled: false, hasData: false, available: false, credits: null, people: null, unmatchedCredits: null,
          days: [], environments: [], agents: null, agentLedgerCredits: null, earliestDate: null, latestDate: null,
        },
        organisation: {
          ...summary().organisation,
          groups: [
            { ...groups[0], studioPeople: null, studioCredits: null, shareOfStudio: null, balanceCredits: 0, shareOfBalances: 0 },
            { ...groups[1], withheld: true, people: 2, studioPeople: null, studioCredits: null, shareOfStudio: null, balancePeople: null, balanceCredits: null, shareOfBalances: null, unknownAmountPeople: null },
          ],
        },
      }),
    );
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: false } });

    expect(await screen.findByText('The Copilot Studio credit import is turned off, so there is no billed usage to show.')).toBeInTheDocument();
    // Without the ledger the groups are ordered by consumed credits, and the heading says so.
    expect(screen.getByText('The groups with the most consumed credits at the latest reading, by Department.')).toBeInTheDocument();
    expect(screen.queryByText(/people with billed usage/)).not.toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'Organisation' });
    expect(within(table).getAllByRole('img', { name: 'Not available' })).toHaveLength(2);
    expect(within(table).getAllByRole('img', { name: 'Fewer than 5 people: figures not shown' })).toHaveLength(5);
    expect(within(table).getByText('0')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Copilot Studio billed usage' }));
    expect((await screen.findAllByText(/Not available: the Copilot Studio ledger has not been imported or could not be read/)).length).toBe(2);
  });

  it('gives the right reason for a missing agent ledger and a grouping that cannot be made', async () => {
    vi.mocked(fetchSpendSummary).mockResolvedValue(
      summary({
        studio: { ...summary().studio, agents: null, agentLedgerCredits: null },
        organisation: { ...summary().organisation, groups: [], unavailableCode: 'directoryUnavailable' },
      }),
    );
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: false } });

    expect(await screen.findByText(/The people cannot be grouped right now: the user directory could not be read/)).toBeInTheDocument();
    expect(screen.queryByText('No Copilot consumption in this period.')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('tab', { name: 'Copilot Studio billed usage' }));
    // Unfiltered, so not "withheld from a filtered view": the ledger was simply not there.
    expect(await screen.findByText('The agent ledger has not been imported, so there is no breakdown by agent.')).toBeInTheDocument();
    expect(screen.queryByText(/Not shown in a filtered view/)).not.toBeInTheDocument();
  });

  it('warns when the newest reading is older than the period, rather than passing it off as the period', async () => {
    vi.mocked(fetchSpendSummary).mockResolvedValue(summary({ balances: { ...summary().balances, snapshotBeforeWindow: true, snapshotDate: '2026-10-10' } }));
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: false } });

    expect(await screen.findByText(/The newest reading is from 10 Oct 2026, before the selected period starts/)).toBeInTheDocument();
  });

  it('shows how consumption is spread across people, one source at a time, and names the largest and smallest consumers to a reader with See PII', async () => {
    vi.mocked(fetchSpendSummary).mockResolvedValue(withSpread(STUDIO_SPREAD, BALANCE_SPREAD));
    vi.mocked(fetchSpendPerson).mockResolvedValue(PERSON7);
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: true } });

    const sources = await screen.findByRole('group', { name: 'Source shown' });
    const panel = sources.parentElement!;
    const studioHeadline = '4 of the 40 people who consumed credits (10%) account for half of the credits.';
    expect(within(panel).getByText(studioHeadline)).toBeInTheDocument();
    expect(within(panel).getByRole('img', { name: `Cumulative share of credits against share of people, largest consumers first. ${studioHeadline}` })).toBeInTheDocument();
    expect(valueAfter(panel, 'Median per person')).toBe('14.7');
    expect(valueAfter(panel, 'Average per person')).toBe('30');
    expect(valueAfter(panel, '90th percentile')).toBe('49.2');
    expect(valueAfter(panel, 'Share consumed by the top 10%')).toBe('51%');
    expect(valueAfter(panel, 'Share consumed by the top 20%')).toBe('65%');
    expect(valueAfter(panel, 'Concentration (Gini)')).toBe('0.61');
    expect(valueAfter(panel, 'Lowest per person (above zero)')).toBe('0.4');
    expect(valueAfter(panel, 'Highest per person')).toBe('340');
    // The ledger records charges only, so it has no readings of zero to count.
    expect(within(panel).queryByText('People with a reading of zero')).not.toBeInTheDocument();
    // Every band keeps its place, an empty one included, with the credits it holds.
    expect(barValue(panel, '0.2 to under 0.5')).toBe('1 person (0.4 credits)');
    expect(barValue(panel, '0.5 to under 1')).toBe('0 people (0 credits)');
    expect(barValue(panel, '10 to under 20')).toBe('11 people (159 credits)');
    expect(barValue(panel, '100 to under 200')).toBe('1 person (121 credits)');

    const largest = within(panel).getByRole('table', { name: 'Largest consumers' });
    expect(within(largest).getByRole('columnheader', { name: 'Billed credits' })).toBeInTheDocument();
    expect(within(largest).getByRole('cell', { name: '340 credits' })).toBeInTheDocument();
    expect(within(largest).getByText('28%')).toBeInTheDocument();
    // The department reads under the person, in the same cell, rather than in a column of its own.
    expect(within(largest).getByRole('cell', { name: /person3@contoso\.example\s*Finance$/ })).toBeInTheDocument();
    const smallest = within(panel).getByRole('table', { name: 'Smallest consumers above zero' });
    expect(within(smallest).getByText('Department not set')).toBeInTheDocument();
    // A share too small to show as 0.1% is still above zero, and says so.
    expect(within(smallest).getByText('<0.1%')).toBeInTheDocument();
    expect(within(panel).getByText('Each bar compares the person with the largest consumer in this view. Share is of all the credits in this view.')).toBeInTheDocument();

    const readings = within(sources).getByRole('button', { name: 'Credits consumed (readings)' });
    fireEvent.click(readings);
    expect(readings).toHaveAttribute('aria-pressed', 'true');
    expect(within(sources).getByRole('button', { name: 'Billed Studio usage' })).toHaveAttribute('aria-pressed', 'false');
    expect(within(panel).getByText('13 of the 90 people who consumed credits (14%) account for half of the credits.')).toBeInTheDocument();
    expect(valueAfter(panel, 'People with a reading of zero')).toBe('205');
    expect(valueAfter(panel, 'Highest per person')).toBe('1,050');
    const largestReadings = within(panel).getByRole('table', { name: 'Largest consumers' });
    expect(within(largestReadings).getByRole('columnheader', { name: 'Consumed credits (latest reading)' })).toBeInTheDocument();

    fireEvent.click(within(largestReadings).getByRole('button', { name: 'Open the consumption of person7@contoso.example' }));
    const dialog = await openedDialog();
    expect(within(dialog).getByText('Copilot consumption of person7@contoso.example')).toBeInTheDocument();
    expect(vi.mocked(fetchSpendPerson).mock.calls[0][1]).toBe(7);
  });

  it('keeps the spread anonymous for a reader without See PII: nobody named, no one person\u2019s amount, and bands of at least five people', async () => {
    vi.mocked(fetchSpendSummary).mockResolvedValue(withSpread(STUDIO_SPREAD_ANONYMISED, BALANCE_SPREAD_ANONYMISED));
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: false } });

    const sources = await screen.findByRole('group', { name: 'Source shown' });
    const panel = sources.parentElement!;
    expect(
      within(panel).getByText('Fewer than 5 of the 40 people who consumed credits account for half of the credits; the exact number is not shown for so few people.'),
    ).toBeInTheDocument();
    expect(barValue(panel, '0.2 to under 5')).toBe('6 people');
    expect(
      within(panel).getByText('Naming the largest and smallest consumers needs the See PII permission. Without it, each band covers at least 5 people and no single person\u2019s amount is shown.'),
    ).toBeInTheDocument();
    expect(within(panel).queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryAllByRole('button', { name: /^Open the consumption of/ })).toHaveLength(0);
    expect(valueAfter(panel, 'Median per person')).toBe('14.7');
    for (const withheld of ['Share consumed by the top 10%', 'Lowest per person (above zero)', 'Highest per person']) {
      expect(within(panel).queryByText(withheld)).not.toBeInTheDocument();
    }

    fireEvent.click(within(sources).getByRole('button', { name: 'Credits consumed (readings)' }));
    expect(within(panel).getByText('13 of the 90 people who consumed credits (14%) account for half of the credits.')).toBeInTheDocument();
    expect(valueAfter(panel, 'Share consumed by the top 10%')).toBe('43%');
    expect(within(panel).queryByText('Highest per person')).not.toBeInTheDocument();
    expect(within(panel).queryByRole('table')).not.toBeInTheDocument();
  });

  it('withholds the spread when too few people consumed credits, and says so when nobody did', async () => {
    vi.mocked(fetchSpendSummary).mockResolvedValue(
      withSpread(ONE_PERSON_STUDIO, NOBODY_BALANCES, {
        filtered: true,
        peopleCount: 1,
        studio: {
          ...summary().studio, credits: 62.5, people: 1, unmatchedCredits: null, agents: null, agentLedgerCredits: null,
          days: [
            { date: '2026-10-24', status: 'noneRecorded', credits: null, people: null },
            { date: '2026-10-25', status: 'recorded', credits: 62.5, people: 1 },
            { date: '2026-10-26', status: 'notReported', credits: null, people: null },
          ],
          environments: [{ environmentId: null, environmentName: null, emptyKey: 'otherEnvironments', folded: 1, withheld: true, credits: null, people: 1, days: null }],
        },
        balances: {
          ...summary().balances, credits: 0, consumingPeople: 0, observedPeople: 1, partialPeople: 0, unknownAmountPeople: 0, otherUnitPeople: 0,
          limitExhaustedPeople: 0, notEntitledPeople: 0, unresolvedAccounts: null, unresolvedCredits: null, services: [], policies: [],
        },
      }),
    );
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: false } });

    const sources = await screen.findByRole('group', { name: 'Source shown' });
    const panel = sources.parentElement!;
    expect(within(panel).getByText('Fewer than 5 people consumed credits in this view, so the spread is not shown.')).toBeInTheDocument();
    for (const absent of ['Median per person', 'Cumulative share of credits', 'People by amount consumed']) {
      expect(within(panel).queryByText(absent)).not.toBeInTheDocument();
    }

    fireEvent.click(within(sources).getByRole('button', { name: 'Credits consumed (readings)' }));
    expect(within(panel).getByText('Nobody in this view consumed credits from this source.')).toBeInTheDocument();
    expect(within(panel).queryByText('Median per person')).not.toBeInTheDocument();
  });

  it('opens a person with both sources as line charts and what they used Copilot for, from their prompts\u2019 metadata', async () => {
    vi.mocked(fetchSpendPerson).mockImplementation(async (_query, userId) => (userId === 7 ? PERSON7 : PERSON9));
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: true } });

    fireEvent.click(await screen.findByRole('tab', { name: 'People' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Open the consumption of person7@contoso.example' }));
    const dialog = await openedDialog();
    await within(dialog).findByText('Billed Copilot Studio credits (period)');
    expect(within(dialog).getByText('Sales \u00b7 Analyst \u00b7 Manager: lead3@contoso.example')).toBeInTheDocument();

    const studio = kpi(dialog, 'Billed Copilot Studio credits (period)');
    expect(studio.getByText('121')).toBeInTheDocument();
    expect(studio.getByText('Billed on 4 days')).toBeInTheDocument();
    expect(studio.getByText('Ranked 2 of 40 people, 10% of the total')).toBeInTheDocument();
    const balance = kpi(dialog, 'Consumed credits (latest reading)');
    expect(balance.getByText('1,050')).toBeInTheDocument();
    expect(balance.getByText('Read on 26 Oct 2026')).toBeInTheDocument();
    expect(balance.getByText('Ranked 1 of 90 people, 11% of the total')).toBeInTheDocument();
    expect(balance.getByText('At credit limit')).toBeInTheDocument();
    expect(kpi(dialog, 'Copilot interactions').getByText('186')).toBeInTheDocument();
    expect(kpi(dialog, 'Copilot interactions').getByText('On 5 days')).toBeInTheDocument();
    expect(kpi(dialog, 'Prompts').getByText('64')).toBeInTheDocument();
    expect(kpi(dialog, 'Prompts').getByText('In 21 conversations')).toBeInTheDocument();

    // Each source over time is drawn as a line, not listed as text.
    for (const chart of [
      'Line chart of this person\u2019s billed Copilot Studio credits per day',
      'Line chart of this person\u2019s consumed credits at each reading',
      'Line chart of this person\u2019s Copilot interactions per day, in total and with agents',
    ]) {
      expect(within(dialog).getByRole('img', { name: chart }).querySelector('path[fill="none"][stroke]')).not.toBeNull();
    }
    // The readings arrive out of order and are drawn in date order; a reading without an amount keeps its place.
    const readingList = within(dialog).getByRole('list', { name: 'Readings by date' });
    expect(within(readingList).getAllByRole('listitem').map((item) => item.getAttribute('aria-label'))).toEqual([
      '24 Oct 2026: amount not stated',
      '25 Oct 2026: 900 credits',
      '26 Oct 2026: 1,050 credits, at the credit limit',
    ]);
    expect(within(dialog).getByRole('listitem', { name: '20 Oct 2026: 30.5 credits' })).toBeInTheDocument();
    expect(within(dialog).getByRole('listitem', { name: '21 Oct 2026: no charges recorded' })).toBeInTheDocument();
    expect(within(dialog).getByRole('listitem', { name: '26 Oct 2026: not in the ledger yet' })).toBeInTheDocument();
    expect(barValue(dialog, `Contoso \u2013 ${GREEK}`)).toBe('121 credits on 4 days');
    expect(barValue(dialog, 'copilotChatAgents')).toBe('1,050 \u00b7 Limit used up');

    expect(within(dialog).getByRole('listitem', { name: '20 Oct 2026: 40 interactions, 14 with agents' })).toBeInTheDocument();
    expect(within(dialog).getByRole('listitem', { name: '21 Oct 2026: 0 interactions, 0 with agents' })).toBeInTheDocument();
    const kinds = part(dialog, 'Interactions by kind');
    expect(barValue(kinds, 'Copilot without an agent')).toBe('120 interactions (65%)');
    expect(barValue(kinds, 'With an agent')).toBe('48 interactions (26%)');
    expect(barValue(kinds, 'Cowork')).toBe('12 interactions (6.5%)');
    expect(barValue(kinds, 'Testing agents in Copilot Studio')).toBe('6 interactions (3.2%)');
    const apps = part(dialog, 'Interactions by app');
    expect(barValue(apps, 'Teams')).toBe('82 interactions on 5 days');
    expect(barValue(apps, 'Copilot Studio')).toBe('6 interactions on 1 day');

    const agents = within(dialog).getByRole('table', { name: 'Agents used' });
    expect(within(agents).getByRole('columnheader', { name: 'In the test pane' })).toBeInTheDocument();
    const helpdesk = within(within(agents).getByRole('row', { name: /Contoso helpdesk agent/ }));
    for (const text of ['Custom engine agent', 'Customer-built', '30', '6', '9.6', '25 Oct 2026']) expect(helpdesk.getByText(text)).toBeInTheDocument();
    const policy = within(within(agents).getByRole('row', { name: /Contoso policy assistant/ }));
    for (const text of ['Declarative agent', '24', '26 Oct 2026']) expect(policy.getByText(text)).toBeInTheDocument();
    // An agent with no estimate shows none, never a zero.
    expect(policy.getAllByText('\u2014').length).toBeGreaterThan(0);
    // Cowork is named as the source names it, with a badge, and its kind is not invented.
    expect(within(agents).getAllByText('Cowork')).toHaveLength(2);
    const cowork = within(within(agents).getAllByRole('row').find((row) => row.textContent?.includes('Kind not recorded'))!);
    expect(cowork.getByText('Microsoft')).toBeInTheDocument();
    expect(
      within(dialog).getByText(
        'Estimated credits: 9.6, for 24 interactions with custom engine agents your organisation built. This is the importer\u2019s own estimate, not a bill, and it is never added to billed or consumed credits.',
      ),
    ).toBeInTheDocument();

    const prompts = part(dialog, 'Their prompts');
    expect(valueAfter(prompts, 'Days with prompts')).toBe('5');
    expect(valueAfter(prompts, 'Average words per prompt')).toBe('23.4');
    expect(valueAfter(prompts, 'Prompts with files attached')).toBe('16 (25%)');
    expect(valueAfter(prompts, 'Prompts with links')).toBe('9 (14%)');
    expect(valueAfter(prompts, 'Prompts with @mentions')).toBe('5 (7.8%)');
    expect(valueAfter(prompts, 'Prompts with context items')).toBe('10 (16%)');
    expect(valueAfter(prompts, 'First prompt')).toBe('20 Oct 2026');
    expect(valueAfter(prompts, 'Latest prompt')).toBe('26 Oct 2026');
    const goals = part(dialog, 'Prompts by goal');
    expect(barValue(goals, 'Content drafting')).toBe('26 prompts \u00b7 Directing');
    expect(barValue(goals, 'Meeting summaries')).toBe('18 prompts \u00b7 Supervising');
    expect(barValue(goals, 'Other')).toBe('6 prompts');
    expect(barValue(goals, 'Not classified')).toBe('14 prompts');
    const promptApps = part(dialog, 'Prompts by app');
    expect(barValue(promptApps, 'Teams')).toBe('30 prompts');
    expect(barValue(promptApps, 'Word')).toBe('22 prompts');
    expect(barValue(promptApps, 'BizChat')).toBe('12 prompts');
    expect(within(dialog).getByTitle('quarterly forecast: 12 prompts')).toBeInTheDocument();
    expect(within(dialog).getByTitle(`${GREEK}: 5 prompts`)).toBeInTheDocument();

    // The latest prompts are described by their metadata: what was typed is never stored, so it is never shown.
    const [, latest, earlier] = within(within(dialog).getByRole('table', { name: 'Most recent prompts' })).getAllByRole('row');
    for (const text of [/14:05/, 'Word', 'Content drafting', 'quarterly forecast, pricing', '31']) expect(within(latest).getByText(text)).toBeInTheDocument();
    expect(within(earlier).getByText('25 Oct 2026')).toBeInTheDocument();
    expect(within(earlier).getByText('App not recorded')).toBeInTheDocument();
    expect(within(earlier).getAllByText('\u2014')).toHaveLength(2);
    expect(within(dialog).getByTitle('docx: 12 References')).toBeInTheDocument();
  });

  it('says what could not be read about a person, and tells unknown apart from none', async () => {
    vi.mocked(fetchSpendPerson).mockImplementation(async (_query, userId) => (userId === 7 ? PERSON7_FAILED : PERSON9));
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: true } });

    fireEvent.click(await screen.findByRole('tab', { name: 'People' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Open the consumption of person7@contoso.example' }));
    let dialog = await openedDialog();
    expect(await within(dialog).findByText('What this person used Copilot for could not be read just now.')).toBeInTheDocument();
    const partFailed = 'Not available: this could not be read just now, so it is unknown, not none.';
    // The prompts and what Copilot referenced failed; the interactions that were read are still shown.
    expect(within(dialog).getAllByText(partFailed)).toHaveLength(2);
    expect(kpi(dialog, 'Copilot interactions').getByText('186')).toBeInTheDocument();
    expect(within(dialog).getByRole('heading', { name: 'Interactions by kind' })).toBeInTheDocument();
    expect(within(dialog).queryByText('Billed Copilot Studio credits by day')).not.toBeInTheDocument();
    expect(within(dialog).getAllByText('Not available')).toHaveLength(1);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    fireEvent.click(await screen.findByRole('button', { name: 'Open the consumption of person9@contoso.example' }));
    dialog = await openedDialog();
    expect(
      await within(dialog).findByText('Not available: the Copilot audit data is not available on this deployment, so this is unknown, not none.'),
    ).toBeInTheDocument();
    expect(
      within(dialog).getByText('Not available: this deployment has not imported the Copilot interaction history, so prompts are unknown, not none.'),
    ).toBeInTheDocument();
    expect(within(dialog).getByText('Not available: the Copilot audit data on what Copilot referenced is not available on this deployment.')).toBeInTheDocument();
    expect(within(dialog).getAllByText('Not available')).toHaveLength(2);
    expect(within(dialog).getByText('No reading of Copilot credits consumed')).toBeInTheDocument();
    expect(within(dialog).getByText('No consumption reading in this period.')).toBeInTheDocument();
    expect(within(dialog).queryByText('What this person used Copilot for could not be read just now.')).not.toBeInTheDocument();
    expect(within(dialog).queryByText(partFailed)).not.toBeInTheDocument();
    expect(within(dialog).getByText('Ranked 7 of 40 people, 3.3% of the total')).toBeInTheDocument();
  });
});
