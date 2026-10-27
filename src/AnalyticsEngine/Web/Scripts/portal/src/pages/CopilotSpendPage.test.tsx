import { beforeEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, screen, waitFor, within } from '@testing-library/react';

import { renderWithProvider } from '../test/renderWithProvider';
import CopilotSpendPage from './CopilotSpendPage';
import { loadCatalog } from '../i18n';
import { fetchSpendPeople, fetchSpendPerson, fetchSpendSummary, fetchSpendTrend, spendWorkbookUrl } from '../api/copilotSpendApi';
import type { CopilotSpendSummary, SpendPeoplePage } from '../types/copilotSpend';

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
      earliestDate: '2026-06-01', coversWindow: true, credits: 1250, people: 40, unmatchedCredits: 50,
      days: [
        { date: '2026-10-25', credits: 400, people: 12 },
        { date: '2026-10-26', credits: 850, people: 30 },
      ],
      agents: [{ agentId: 'agent-1', agentName: 'Contoso helpdesk agent', credits: 1180, days: 2 }],
      agentLedgerCredits: 1180,
      environments: [{ environmentId: 'env-1', environmentName: `Contoso \u2013 ${GREEK}`, credits: 1250, people: 40, days: 2 }],
      breakdownFailureCode: null,
    },
    balances: {
      tablesPresent: true, collectionEnabled: true, hasData: true, failureCode: null, snapshotDate: '2026-10-26', snapshotBandFrom: '2026-10-24',
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
      dimension: 'department', dimensionName: null, minGroupSize: 5, foldedGroups: 2, hidden: false,
      groups: [
        { label: `Ventas \u2013 ${GREEK}`, emptyKey: null, people: 30, studioPeople: 25, studioCredits: 900, shareOfStudio: 0.75, balancePeople: 20, balanceCredits: 6000, shareOfBalances: 0.62, unknownAmountPeople: 2 },
        { label: null, emptyKey: 'otherSmallGroups', people: 7, studioPeople: 5, studioCredits: 300, shareOfStudio: 0.25, balancePeople: 4, balanceCredits: 3680, shareOfBalances: 0.38, unknownAmountPeople: 2 },
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
    expect(screen.getByText('Latest balances, in credits')).toBeInTheDocument();
    expect(screen.getByText('9,800')).toBeInTheDocument();
    expect(screen.queryByText('11,050')).not.toBeInTheDocument();
    expect(screen.getByText('Latest reading, not a bill')).toBeInTheDocument();
    expect(screen.getByText('Period not confirmed by the source')).toBeInTheDocument();
    expect(screen.getByText(/Two sources, kept apart/)).toBeInTheDocument();
    expect(screen.getByRole('listitem', { name: '26 Oct 2026: 850 credits, 30 people' })).toBeInTheDocument();
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

    fireEvent.click(await screen.findByRole('tab', { name: 'Copilot credit balances' }));
    expect(await screen.findByText('Source last changed the readings')).toBeInTheDocument();
    expect(screen.getByText('Not confirmed by the source')).toBeInTheDocument();
    expect(screen.getByText(/Readings on different dates are the same balances read again/)).toBeInTheDocument();
    expect(await screen.findByRole('listitem', { name: '26 Oct 2026: 9,800 credits, 90 people with a balance' })).toBeInTheDocument();
    expect(screen.getByText(/a filtered view does not show them/)).toBeInTheDocument();
    expect(screen.queryByText('Accounts not matched to a person')).not.toBeInTheDocument();

    // The agent ledger is not kept per person, so a filtered view says why it is missing rather than showing the tenant's.
    fireEvent.click(screen.getByRole('tab', { name: 'Copilot Studio billed usage' }));
    expect(await screen.findByText(/Not shown in a filtered view: the agent ledger is recorded per agent/)).toBeInTheDocument();
    expect(screen.queryByText('Contoso helpdesk agent')).not.toBeInTheDocument();
    expect(screen.getByRole('table', { name: 'By environment' })).toBeInTheDocument();
  });

  it('lists people for a reader with See PII, sorted on the server by any column header, with no reading shown as absent', async () => {
    renderWithProvider(<CopilotSpendPage />, { access: { administration: false, seePii: true } });

    fireEvent.click(await screen.findByRole('tab', { name: 'People' }));
    expect(await screen.findByText('person7@contoso.example')).toBeInTheDocument();
    const table = screen.getByRole('table', { name: 'People' });
    const header = (name: RegExp) => within(table).getByRole('columnheader', { name });
    expect(header(/Studio credits/)).toHaveAttribute('aria-sort', 'descending');
    expect(screen.getByRole('img', { name: 'No balance reading in Copilot credits' })).toBeInTheDocument();
    expect(screen.getByText('At credit limit')).toBeInTheDocument();
    expect(within(table).getByText('lead3@contoso.example')).toBeInTheDocument();
    expect(header(/Manager/)).toHaveAttribute('aria-sort', 'none');

    fireEvent.click(within(header(/Latest balance/)).getByRole('button'));
    await waitFor(() => expect(vi.mocked(fetchSpendPeople).mock.calls.at(-1)![1]).toMatchObject({ sortBy: 'balanceCredits', sortDesc: true }));
    expect(vi.mocked(fetchSpendPeople).mock.calls.at(-1)![2]).toBe(0);
    fireEvent.keyDown(within(header(/Latest balance/)).getByRole('button'), { key: 'Enter' });
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
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByText('Billed Copilot Studio usage by environment')).toBeInTheDocument();
    expect(within(dialog).getByText('Contoso (default)')).toBeInTheDocument();
    expect(within(dialog).getByText(/^\d[\d,.]* credits on 4 days$/)).toBeInTheDocument();
    expect(within(dialog).getByText(/26 Oct 2026: 1,050 credits/)).toBeInTheDocument();
    expect(within(dialog).getByText('Each reading is the balance on that date, not a day\u2019s spend.')).toBeInTheDocument();
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
});
