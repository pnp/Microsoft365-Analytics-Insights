import { afterEach, describe, it, expect, vi } from 'vitest';
import { act, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import AgentsPanel from './AgentsPanel';
import { AgentHealth } from '../../types/copilotAdoption';
import { requestPrint, resetPrintPreparation } from '../shared/printPreparation';
import type {
  AgentEstateSummary,
  AgentUsageRow,
  AgentUserRow,
  CopilotAdoptionOptions,
} from '../../types/copilotAdoption';

const OPTIONS: CopilotAdoptionOptions = {
  windowDays: 30,
  historyDays: 365,
  workingDaysPerWeek: 5,
  frequencyTargetRatio: 0.6,
  depthTargetInteractionsPerActiveDay: 5,
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
  agentMinUsers: 3,
  agentHistoryDays: 120,
  opportunityUnlicensedCopilotWeight: 40,
  opportunityCollaborationWeight: 25,
  opportunityEmailWeight: 20,
  opportunityDocumentWeight: 15,
  opportunityCopilotTarget: 20,
  opportunityCollaborationTarget: 30,
  opportunityEmailTarget: 40,
  opportunityDocumentTarget: 20,
  opportunityRecommendScore: 60,
  usageReportLagDays: 3,
  topSegments: 10,
  minSeatsPerSegment: 5,
  maxLicensedUsersScored: 50000,
  maxOpportunityCandidates: 50000,
  maxAgents: 1000,
  maxUnlicensedUsersScored: 50000,
  depthMinActiveDays: 3,
  reclaimGraceDays: 30,
  activationWindowDays: 30,
  opportunityCopilotTargetBasisDays: 28,
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
  copilotMinutesSavedPerMeeting: 5,
  copilotMinutesSavedPerMailThread: 0.5,
  copilotMinutesSavedPerDocument: 1,
  coworkEstimateLowerBoundRatio: 0.5,
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
  accountabilityDimension: 'directManager',
  maxCoworkUsersScored: 50000,
};

/** A machine-generated identifier of the length that used to push the rest of the table off screen. */
const LONG_KEY = `CopilotStudio.Declarative.T_${'0'.repeat(180)}.gpt.${'a'.repeat(60)}`;

function agent(over: Partial<AgentUsageRow>): AgentUsageRow {
  return {
    agentId: 1,
    name: 'Contoso Agent',
    agentKey: null,
    origin: 'customerBuilt',
    isCustomAgent: true,
    interactions: 100,
    users: 10,
    licensedUsers: 4,
    activeDays: 12,
    appsUsed: 2,
    interactionsPerUser: 10,
    firstUsedUtc: '2026-01-01T00:00:00Z',
    lastUsedUtc: '2026-02-01T00:00:00Z',
    daysSinceLastUse: 3,
    health: AgentHealth.Keep,
    healthName: 'Keep',
    healthReason: 'Used recently by enough people.',
    ...over,
  };
}

const AGENTS: AgentUsageRow[] = [
  agent({ agentId: 1, name: 'Contoso Expenses Helper', agentKey: 'CopilotStudio.Declarative.T_expenses' }),
  agent({ agentId: 2, name: 'Fabrikam Travel Booker', agentKey: LONG_KEY }),
  agent({ agentId: 3, name: 'Northwind Ticket Triage', agentKey: null, origin: 'microsoft', isCustomAgent: false }),
];

const ESTATE: AgentEstateSummary = {
  historyDays: 120,
  activeAgents: 3,
  knownAgents: 3,
  customAgents: 2,
  unknownOriginAgents: 0,
  agentUsers: 25,
  licensedAgentUsers: 10,
  agentInteractions: 300,
  interactionsPerAgentUser: 12,
  mostPopularAgent: null,
  mostVersatileAgent: null,
  healthBreakdown: [{ label: 'Keep', value: 3 }],
  // Left empty on purpose: the treemap and the department chart would otherwise render the same
  // agent names as the table, and a name assertion could then pass without the table filtering.
  usageByDepartment: [],
  usageByAgent: [],
  agents: AGENTS,
};

function renderPanel() {
  return renderWithProvider(
    <AgentsPanel estate={ESTATE} agents={AGENTS} options={OPTIONS} sql={null} />,
  );
}

describe('AgentsPanel inventory', () => {
  it('filters the inventory by agent name as you type', async () => {
    const user = userEvent.setup();
    renderPanel();

    expect(screen.getByText('3 of 3 agents')).toBeInTheDocument();

    await user.type(screen.getByLabelText('Search agents by name or ID'), 'travel');

    expect(screen.getByText('Fabrikam Travel Booker')).toBeInTheDocument();
    expect(screen.queryByText('Contoso Expenses Helper')).not.toBeInTheDocument();
    expect(screen.queryByText('Northwind Ticket Triage')).not.toBeInTheDocument();
    expect(screen.getByText('1 of 3 agents')).toBeInTheDocument();
  });

  it('matches on the agent ID too, so an identifier copied from elsewhere finds its agent', async () => {
    const user = userEvent.setup();
    renderPanel();

    await user.type(screen.getByLabelText('Search agents by name or ID'), 'T_EXPENSES');

    expect(screen.getByText('Contoso Expenses Helper')).toBeInTheDocument();
    expect(screen.queryByText('Fabrikam Travel Booker')).not.toBeInTheDocument();
  });

  it('says so when nothing matches, and restores the list when the search is cleared', async () => {
    const user = userEvent.setup();
    renderPanel();

    await user.type(screen.getByLabelText('Search agents by name or ID'), 'no such agent');
    expect(screen.getByText('No agents match these filters.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Next' })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Clear agent search' }));

    expect(screen.getByText('Contoso Expenses Helper')).toBeInTheDocument();
    expect(screen.getByText('3 of 3 agents')).toBeInTheDocument();
  });

  it('keeps the full agent ID reachable on hover, since the column truncates it', () => {
    renderPanel();

    // Only the affordance is assertable here: vitest runs with `css: false` and jsdom has no layout
    // engine, so the truncation itself (max-width + text-overflow) cannot be verified from a test.
    // That was checked in a headless browser against a 180-character key.
    expect(screen.getByText(LONG_KEY)).toHaveAttribute('title', LONG_KEY);
  });
});

describe('AgentsPanel inventory paging', () => {
  const MANY_AGENTS = Array.from({ length: 105 }, (_, index) => agent({
    agentId: index + 1,
    name: `Contoso Agent ${String(index + 1).padStart(3, '0')}`,
    agentKey: `contoso-key-${index + 1}`,
    interactions: 105 - index,
    health: index % 2 === 0 ? AgentHealth.Keep : AgentHealth.Review,
    origin: index % 2 === 0 ? 'customerBuilt' : 'microsoft',
    isCustomAgent: index % 2 === 0,
  }));

  function panel(agents = MANY_AGENTS) {
    return <AgentsPanel estate={{ ...ESTATE, agents }} agents={agents} options={OPTIONS} sql={null} />;
  }

  function inventory() {
    return within(screen.getByText('Agent inventory').closest('.fui-Card') as HTMLElement);
  }

  afterEach(() => {
    vi.restoreAllMocks();
    resetPrintPreparation();
  });

  it('bounds the rendered rows, preserves the busiest-first order and reaches the final partial page', async () => {
    const user = userEvent.setup();
    renderWithProvider(panel());
    const table = inventory();

    expect(table.getAllByRole('row')).toHaveLength(51);
    expect(table.getAllByRole('row')[1]).toHaveTextContent('Contoso Agent 001');
    expect(table.getByText('Showing 1-50 of 105 agents')).toBeInTheDocument();
    expect(table.getByText('Page 1 of 3')).toBeInTheDocument();
    expect(table.getByRole('button', { name: 'Previous' })).toBeDisabled();
    expect(table.queryByText('Contoso Agent 051')).not.toBeInTheDocument();

    await user.click(table.getByRole('button', { name: 'Next' }));
    expect(table.getByText('Showing 51-100 of 105 agents')).toBeInTheDocument();
    expect(table.getAllByRole('row')[1]).toHaveTextContent('Contoso Agent 051');

    await user.click(table.getByRole('button', { name: 'Next' }));
    expect(table.getAllByRole('row')).toHaveLength(6);
    expect(table.getByText('Showing 101-105 of 105 agents')).toBeInTheDocument();
    expect(table.getByText('Page 3 of 3')).toBeInTheDocument();
    expect(table.getByRole('button', { name: 'Next' })).toBeDisabled();

    await user.click(table.getByRole('button', { name: 'Previous' }));
    expect(table.getByText('Contoso Agent 051')).toBeInTheDocument();
  });

  it.each(['Contoso Agent 105', 'contoso-key-105'])('searches all pages for %s and resets the page when cleared', async (search) => {
    const user = userEvent.setup();
    renderWithProvider(panel());
    const table = inventory();
    await user.click(table.getByRole('button', { name: 'Next' }));

    await user.type(table.getByLabelText('Search agents by name or ID'), search);

    expect(table.getByText('Contoso Agent 105')).toBeInTheDocument();
    expect(table.getAllByRole('row')).toHaveLength(2);
    expect(table.getByText('Showing 1-1 of 1 agents')).toBeInTheDocument();
    expect(table.getByText('1 of 105 agents')).toBeInTheDocument();
    expect(table.getByRole('button', { name: 'Next' })).toBeDisabled();

    await user.click(table.getByRole('button', { name: 'Clear agent search' }));
    expect(table.getByText('Page 1 of 3')).toBeInTheDocument();
    expect(table.getByText('Contoso Agent 001')).toBeInTheDocument();
  });

  it.each(['health', 'custom'])('resets to the first page when the %s filter changes', async (filter) => {
    const user = userEvent.setup();
    renderWithProvider(panel());
    const table = inventory();
    await user.click(table.getByRole('button', { name: 'Next' }));
    await user.click(table.getByRole('button', { name: 'Next' }));

    if (filter === 'health') {
      await user.selectOptions(table.getByLabelText('Filter agents by health'), String(AgentHealth.Keep));
    } else {
      await user.click(table.getByRole('checkbox', { name: 'Customer-built agents only' }));
    }

    expect(table.getByText('Page 1 of 2')).toBeInTheDocument();
    expect(table.getByText('Showing 1-50 of 53 agents')).toBeInTheDocument();
    expect(table.getByText('Contoso Agent 001')).toBeInTheDocument();
    expect(table.queryByText('Contoso Agent 002')).not.toBeInTheDocument();
    await user.click(table.getByRole('button', { name: 'Next' }));
    expect(table.getAllByRole('row')).toHaveLength(4);
    expect(table.getByText('Showing 51-53 of 53 agents')).toBeInTheDocument();
  });

  it('returns to the first page when a refreshed inventory is shorter', async () => {
    const user = userEvent.setup();
    const rendered = renderWithProvider(panel());
    await user.click(inventory().getByRole('button', { name: 'Next' }));

    rendered.rerender(panel(MANY_AGENTS.slice(0, 3)));

    expect(inventory().getByText('Showing 1-3 of 3 agents')).toBeInTheDocument();
    expect(inventory().getByText('Contoso Agent 001')).toBeInTheDocument();
    expect(inventory().getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('prints every filtered agent and restores the current page afterwards', async () => {
    const user = userEvent.setup();
    renderWithProvider(panel());
    const table = inventory();
    await user.click(table.getByRole('checkbox', { name: 'Customer-built agents only' }));
    await user.click(table.getByRole('button', { name: 'Next' }));
    const captured = { rows: 0, range: '' };
    vi.spyOn(window, 'print').mockImplementation(() => {
      captured.rows = table.getAllByRole('row').length - 1;
      captured.range = table.getByText(/^Showing /).textContent ?? '';
      expect(table.queryByText('Contoso Agent 002')).not.toBeInTheDocument();
      expect(table.getByText('Contoso Agent 001')).toBeInTheDocument();
      expect(table.queryByRole('button', { name: 'Next' })).not.toBeInTheDocument();
    });

    await act(async () => {
      await expect(requestPrint()).resolves.toEqual({ kind: 'printed' });
    });

    expect(captured).toEqual({ rows: 53, range: 'Showing 1-53 of 53 agents' });
    expect(table.getByText('Page 2 of 2')).toBeInTheDocument();
    expect(table.getAllByRole('row')).toHaveLength(4);
    expect(table.getByRole('button', { name: 'Next' }).closest('[data-print="hide"]')).not.toBeNull();
  });
});

describe('AgentsPanel agent origin', () => {
  // One agent of each origin, as the server sends them (#639). isCustomAgent is false for Microsoft's agents
  // and for unknown ones alike, which is why the Type column reads origin instead.
  const MIXED: AgentUsageRow[] = [
    agent({ agentId: 1, name: 'Contoso Expenses Helper', origin: 'customerBuilt', isCustomAgent: true }),
    agent({ agentId: 2, name: 'Copilot Cowork', origin: 'microsoft', isCustomAgent: false }),
    agent({ agentId: 3, name: 'Καλημέρα κόσμε agent', origin: 'unknown', isCustomAgent: false }),
  ];
  const MIXED_ESTATE: AgentEstateSummary = { ...ESTATE, customAgents: 1, unknownOriginAgents: 1, agents: MIXED };

  function renderMixed() {
    return renderWithProvider(
      <AgentsPanel estate={MIXED_ESTATE} agents={MIXED} options={OPTIONS} sql={null} />,
    );
  }

  function typeOf(name: string): string {
    const row = screen.getByText(name).closest('tr') as HTMLElement;
    return within(row).getAllByRole('cell')[1].textContent ?? '';
  }

  it('labels each agent Customer-built, Microsoft or Unknown', () => {
    renderMixed();

    expect(typeOf('Contoso Expenses Helper')).toBe('Customer-built');
    expect(typeOf('Copilot Cowork')).toBe('Microsoft');
    expect(typeOf('Καλημέρα κόσμε agent')).toBe('Unknown');
  });

  it('keeps only customer-built agents when "Customer-built agents only" is ticked', async () => {
    const user = userEvent.setup();
    renderMixed();

    await user.click(screen.getByRole('checkbox', { name: 'Customer-built agents only' }));

    expect(screen.getByText('Contoso Expenses Helper')).toBeInTheDocument();
    expect(screen.queryByText('Copilot Cowork')).not.toBeInTheDocument();
    expect(screen.queryByText('Καλημέρα κόσμε agent')).not.toBeInTheDocument();
    expect(screen.getByText('1 of 3 agents')).toBeInTheDocument();
  });

  it('shows how many agents are of unknown origin beside the customer-built count', () => {
    renderMixed();

    expect(screen.getByText('3 known: 1 customer-built, 1 of unknown origin')).toBeInTheDocument();
  });
});

describe('AgentsPanel heaviest agent users', () => {
  function person(over: Partial<AgentUserRow>): AgentUserRow {
    return {
      userId: 1,
      userPrincipalName: 'heavy.user@contoso.com',
      mail: 'heavy.user@contoso.com',
      emailDomain: 'contoso.com',
      department: 'Finance',
      jobTitle: 'Analyst',
      interactions: 180,
      agentsUsed: 3,
      activeDays: 14,
      lastUsedUtc: '2026-02-01T00:00:00Z',
      topAgentName: 'Contoso Expenses Helper',
      topAgentInteractions: 120,
      holdsCopilotSeat: true,
      ...over,
    };
  }

  const PEOPLE: AgentUserRow[] = [
    person({ userId: 1 }),
    person({
      userId: 2,
      userPrincipalName: 'chat.only@fabrikam.com',
      department: 'Καλημέρα κόσμε',
      interactions: 95,
      topAgentName: '(unnamed agent)',
      holdsCopilotSeat: false,
    }),
  ];

  function renderWith(props: Partial<Parameters<typeof AgentsPanel>[0]>) {
    return renderWithProvider(
      <AgentsPanel
        estate={ESTATE}
        agents={AGENTS}
        options={{ ...OPTIONS, maxAgentUsersScored: 20000 }}
        sql={null}
        {...props}
      />,
    );
  }

  it('names the heaviest agent users and whether each holds a Copilot licence, for a See PII reader', () => {
    renderWith({ topUsers: PEOPLE, canSeePii: true });

    const card = screen.getByText('Heaviest agent users').closest('.fui-Card') as HTMLElement;
    expect(within(card).getByText('The 2 people who used Copilot agents most in the selected period - 1 of them hold a Copilot licence.')).toBeInTheDocument();

    const rows = within(card).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText('heavy.user@contoso.com')).toBeInTheDocument();
    expect(within(rows[0]).getByText('Licensed')).toBeInTheDocument();
    expect(within(rows[0]).getByText('120 interactions')).toBeInTheDocument();
    expect(within(rows[1]).getByText('chat.only@fabrikam.com')).toBeInTheDocument();
    expect(within(rows[1]).getByText('No licence')).toBeInTheDocument();
    // Tenant data renders exactly as stored; the server's own placeholder goes through the catalog.
    expect(within(rows[1]).getByText('Καλημέρα κόσμε')).toBeInTheDocument();
    expect(within(rows[1]).getByText('(unnamed agent)')).toBeInTheDocument();
  });

  it('says the list is hidden, rather than showing an empty one, to a reader without See PII', () => {
    renderWith({ topUsers: [], canSeePii: false });

    expect(screen.queryByText('Heaviest agent users')).toBeNull();
    expect(screen.getByRole('note')).toBeInTheDocument();
  });

  it('says a filtered view\u2019s list may be short when the tenant-wide read was capped', () => {
    renderWith({ topUsers: PEOPLE, topUsersCapped: true, canSeePii: true });

    expect(screen.getByText(/Only the 20,000 heaviest agent users in the tenant were read/)).toBeInTheDocument();
  });
});

describe('AgentsPanel headline figures', () => {
  function tile(label: string): HTMLElement {
    return screen.getAllByText(label)
      .map((element) => element.closest('.fui-Card'))
      .find((card) => card !== null) as HTMLElement;
  }

  it('shows a past period the audit log could not measure as a dash, never as the inventory\u2019s figures', () => {
    renderWithProvider(
      <AgentsPanel
        estate={{ ...ESTATE, activeAgents: null, agentInteractions: null, interactionsPerAgentUser: null }}
        agents={AGENTS}
        options={OPTIONS}
        sql={null}
      />,
    );

    expect(within(tile('Active agents')).getByText('\u2014')).toBeInTheDocument();
    const intensity = tile('Interactions per agent user');
    expect(within(intensity).getByText('\u2014')).toBeInTheDocument();
    expect(within(intensity).getByText('Not measured for this period')).toBeInTheDocument();
    expect(within(tile('Agent users')).getByText('25')).toBeInTheDocument();
  });

  it('shows measured figures as numbers', () => {
    renderWithProvider(
      <AgentsPanel estate={{ ...ESTATE, interactionsPerAgentUser: 12.5 }} agents={AGENTS} options={OPTIONS} sql={null} />,
    );

    expect(within(tile('Active agents')).getByText('3')).toBeInTheDocument();
    const intensity = tile('Interactions per agent user');
    expect(within(intensity).getByText('12.5')).toBeInTheDocument();
    expect(within(intensity).getByText('300 agent interactions in total')).toBeInTheDocument();
  });
});

/**
 * A period with dates of its own - here the last calendar quarter, 92 days. The page's selector still holds the rolling
 * window it last had, so nothing on this tab may describe the figures as "the last 28 days".
 */
describe('AgentsPanel for a period with its own dates', () => {
  const PAST_QUARTER: CopilotAdoptionOptions = {
    ...OPTIONS,
    windowDays: 92,
    usesExplicitDates: true,
    fromUtc: '2026-07-01T00:00:00Z',
    toUtc: '2026-09-30T00:00:00Z',
    toExclusiveUtc: '2026-10-01T00:00:00Z',
  };

  it('dates the headline figures by the period, and the department chart by its own window up to today', async () => {
    const user = userEvent.setup();
    renderWithProvider(
      <AgentsPanel
        estate={{ ...ESTATE, usageByDepartment: [{ label: 'Finance', value: 40 }] }}
        agents={AGENTS}
        options={PAST_QUARTER}
        sql={null}
      />,
    );

    await user.click(screen.getByRole('button', { name: 'How "Agent users" is calculated' }));
    expect(await screen.findByText(/^Distinct people who used at least one agent from 1 Jul 2026 to 30 Sept? 2026, licensed or not\.$/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'How "Agent usage by department" is calculated' }));
    expect(await screen.findByText(/^Agent interactions in the last 92 days up to today, grouped by/)).toBeInTheDocument();
    expect(screen.queryByText(/at least one agent in the last \d+ days/)).not.toBeInTheDocument();
    expect(screen.queryByText(/^Agent interactions in the last (28|30) days/)).not.toBeInTheDocument();
  });

  it('keeps "the last N days" for a rolling window, with N the window analysed', async () => {
    const user = userEvent.setup();
    renderWithProvider(<AgentsPanel estate={ESTATE} agents={AGENTS} options={OPTIONS} sql={null} />);

    await user.click(screen.getByRole('button', { name: 'How "Agent users" is calculated' }));
    expect(await screen.findByText('Distinct people who used at least one agent in the last 30 days, licensed or not.')).toBeInTheDocument();
  });

  it('tells a See PII reader why a past period lists nobody, rather than leaving the list out', () => {
    renderWithProvider(
      <AgentsPanel estate={ESTATE} agents={AGENTS} options={PAST_QUARTER} sql={null} topUsers={[]} canSeePii />,
    );

    expect(screen.getByText(/^This period does not end today, so the heaviest agent users are not listed/)).toBeInTheDocument();
    expect(screen.queryByText('Heaviest agent users')).not.toBeInTheDocument();
  });
});
