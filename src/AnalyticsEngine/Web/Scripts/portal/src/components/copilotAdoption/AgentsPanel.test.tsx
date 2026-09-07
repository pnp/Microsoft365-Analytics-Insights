import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../../test/renderWithProvider';
import AgentsPanel from './AgentsPanel';
import { AgentHealth } from '../../types/copilotAdoption';
import type {
  AgentEstateSummary,
  AgentUsageRow,
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
};

/** A machine-generated identifier of the length that used to push the rest of the table off screen. */
const LONG_KEY = `CopilotStudio.Declarative.T_${'0'.repeat(180)}.gpt.${'a'.repeat(60)}`;

function agent(over: Partial<AgentUsageRow>): AgentUsageRow {
  return {
    agentId: 1,
    name: 'Contoso Agent',
    agentKey: null,
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
  agent({ agentId: 3, name: 'Northwind Ticket Triage', agentKey: null, isCustomAgent: false }),
];

const ESTATE: AgentEstateSummary = {
  historyDays: 120,
  activeAgents: 3,
  knownAgents: 3,
  customAgents: 2,
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
    <AgentsPanel estate={ESTATE} agents={AGENTS} options={OPTIONS} windowDays={30} sql={null} />,
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
