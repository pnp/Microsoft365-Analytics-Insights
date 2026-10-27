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
    const dialog = await screen.findByRole('dialog');
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
    });
