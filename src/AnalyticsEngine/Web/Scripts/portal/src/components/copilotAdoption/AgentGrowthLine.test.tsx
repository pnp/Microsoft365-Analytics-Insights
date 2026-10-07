import { describe, it, expect } from 'vitest';
import { screen, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import AgentGrowthLine from './AgentGrowthLine';
import AgentsPanel from './AgentsPanel';
import type { AgentEstateSummary, AgentGrowthWindow, CopilotAdoptionOptions } from '../../types/copilotAdoption';

/** Fourteen closed 28-day windows ending 4 October 2026, most recent first, all measured. */
function windows(overrides: Partial<Record<number, Partial<AgentGrowthWindow>>> = {}): AgentGrowthWindow[] {
  const lastSettled = Date.UTC(2026, 9, 4);
  const day = 24 * 60 * 60 * 1000;
  return Array.from({ length: 14 }, (_, n) => {
    const to = lastSettled - 28 * n * day;
    return {
      windowsAgo: n,
      fromUtc: new Date(to - 27 * day).toISOString(),
      toUtc: new Date(to).toISOString(),
      activeAgents: 30 - 2 * n,
      agentUsers: 1200 - 50 * n,
      agentInteractions: 15000 - 500 * n,
      interactionsPerAgentUser: 12.5,
      copilotStudioBilledAgents: n < 7 ? 4 : null,
      ...overrides[n],
    };
  });
}

function estate(growth: AgentGrowthWindow[], over: Partial<AgentEstateSummary> = {}): AgentEstateSummary {
  return {
    historyDays: 120,
    activeAgents: 30,
    knownAgents: 40,
    customAgents: 20,
    agentUsers: 1200,
    licensedAgentUsers: 600,
    agentInteractions: 15000,
    interactionsPerAgentUser: 12.5,
    mostPopularAgent: null,
    mostVersatileAgent: null,
    healthBreakdown: [],
    usageByDepartment: [],
    usageByAgent: [],
    agents: [],
    growthScope: 'allAgents',
    growthAuditHistoryStartUtc: '2025-01-15T09:00:00Z',
    growth,
    ...over,
  };
}

function figure(label: string): HTMLElement {
  return screen.getByText(label).parentElement as HTMLElement;
}

describe('AgentGrowthLine', () => {
  it('sets the latest closed 28-day window against the same 28 days a year earlier', () => {
    renderWithProvider(<AgentGrowthLine estate={estate(windows())} lagDays={3} sql="SELECT 1" billingSql="SELECT 2" />);

    expect(screen.getByText('Agent growth, year on year')).toBeInTheDocument();
    expect(screen.getByText(
      'Agents used on at least one day in the 28 days to 4 Oct 2026, against the same 28 days a year earlier, to 5 Oct 2025.',
    )).toBeInTheDocument();

    expect(within(figure('Active agents')).getByText('30')).toBeInTheDocument();
    expect(within(figure('Active agents')).getByText('A year earlier: 4')).toBeInTheDocument();
    expect(within(figure('Agent users')).getByText('1,200')).toBeInTheDocument();
    expect(within(figure('Agent users')).getByText('A year earlier: 550')).toBeInTheDocument();
    expect(within(figure('Agent interactions')).getByText('15,000')).toBeInTheDocument();
    expect(within(figure('Interactions per agent user')).getByText('12.5')).toBeInTheDocument();

    expect(screen.getByRole('img', { name: /^Active agents in each of the last 14 28-day windows, oldest first: 4, 6, 8,/ }))
      .toBeInTheDocument();
  });

  it('keeps the Copilot Studio billing evidence apart, names its source, and shows the caveat on screen', () => {
    renderWithProvider(<AgentGrowthLine estate={estate(windows())} lagDays={3} />);

    const evidence = figure('Agents billed in Copilot Studio');
    expect(within(evidence).getByText('4')).toBeInTheDocument();
    expect(within(evidence).getByText('Not measured a year earlier')).toBeInTheDocument();
    expect(within(evidence).getByText(
      'Separate evidence of autonomous runs, from Copilot Studio billing. Never added to active agents.',
    )).toBeInTheDocument();

    expect(screen.getByText(
      'Active agents, agent users and interactions: the Copilot audit log, user-initiated use only. Agents billed: Copilot Studio billing (Power Platform).',
    )).toBeInTheDocument();
    expect(screen.getByText(/^This compares your organisation with itself\. The 2026 Work Trend Index.s 15x is year-on-year growth/))
      .toBeInTheDocument();
    expect(screen.getByText(/not a target, and not a benchmark for one organisation\.$/)).toBeInTheDocument();
  });

  it('shows an unmeasured year-ago window as not measured, never as zero, and says why', () => {
    const growth = windows({
      13: { activeAgents: null, agentUsers: null, agentInteractions: null, interactionsPerAgentUser: null },
    });
    renderWithProvider(
      <AgentGrowthLine estate={estate(growth, { growthAuditHistoryStartUtc: '2025-10-01T08:00:00Z' })} lagDays={3} />,
    );

    expect(within(figure('Active agents')).getByText('Not measured a year earlier')).toBeInTheDocument();
    expect(within(figure('Active agents')).queryByText('A year earlier: 0')).toBeNull();
    expect(screen.getByText(
      'A dash means not measured: the Copilot audit history starts on 1 Oct 2025, so it does not cover that window in full.',
    )).toBeInTheDocument();
    expect(screen.getByRole('img', { name: /oldest first: not measured, 6, 8,/ })).toBeInTheDocument();
  });

  it('says when no billing data covers the series at all', () => {
    const growth = windows(Object.fromEntries(Array.from({ length: 14 }, (_, n) => [n, { copilotStudioBilledAgents: null }])));
    renderWithProvider(<AgentGrowthLine estate={estate(growth)} lagDays={3} />);

    const evidence = figure('Agents billed in Copilot Studio');
    expect(within(evidence).getByText('\u2014')).toBeInTheDocument();
    expect(within(evidence).getByText('No Copilot Studio billing data covers these windows.')).toBeInTheDocument();
  });

  it('names the customer-built scope once the series is counted that way', () => {
    renderWithProvider(<AgentGrowthLine estate={estate(windows(), { growthScope: 'customerBuilt' })} lagDays={3} />);

    expect(screen.getByText(/^Agents your organisation built, used on at least one day in the 28 days to 4 Oct 2026/))
      .toBeInTheDocument();
  });

  it('renders nothing when the series was not computed', () => {
    renderWithProvider(<AgentGrowthLine estate={estate([])} lagDays={3} />);

    expect(screen.queryByText('Agent growth, year on year')).toBeNull();
    expect(screen.queryByRole('img')).toBeNull();
  });

  it('reads in Spanish, with Spanish number and date formats', async () => {
    await loadCatalog('es');
    renderWithProvider(<AgentGrowthLine estate={estate(windows())} lagDays={3} />, { language: 'es' });

    expect(screen.getByText('Crecimiento de agentes, interanual')).toBeInTheDocument();
    expect(screen.getByText(
      'Agentes usados al menos un día en los 28 días hasta el 4 oct 2026, frente a los mismos 28 días un año antes, hasta el 5 oct 2025.',
    )).toBeInTheDocument();
    expect(within(figure('Agentes activos')).getByText('Un año antes: 4')).toBeInTheDocument();
    expect(within(figure('Usuarios de agentes')).getByText('1200')).toBeInTheDocument();
    expect(within(figure('Interacciones de agentes')).getByText('15.000')).toBeInTheDocument();
    expect(within(figure('Interacciones por usuario de agente')).getByText('12,5')).toBeInTheDocument();
    expect(within(figure('Agentes facturados en Copilot Studio')).getByText('Sin medir un año antes')).toBeInTheDocument();
    expect(screen.getByText(/^Esta comparación es de su organización consigo misma\. El crecimiento interanual de 15 veces/))
      .toBeInTheDocument();
    expect(screen.getByRole('img', { name: /^Agentes activos en cada uno de los últimos 14 periodos de 28 días/ }))
      .toBeInTheDocument();
  });
});

describe('AgentsPanel with the growth series', () => {
  const OPTIONS = {
    windowDays: 28,
    historyDays: 365,
    agentReviewInactiveDays: 30,
    agentRetireInactiveDays: 90,
    agentNewDays: 30,
    agentMinUsers: 3,
    agentHistoryDays: 120,
    usageReportLagDays: 3,
    topSegments: 10,
    maxAgentUsersScored: 20000,
  } as CopilotAdoptionOptions;

  it('puts the growth line on the Agents tab, with the SQL behind it', () => {
    renderWithProvider(
      <AgentsPanel
        estate={estate(windows())}
        agents={[]}
        options={OPTIONS}
        windowDays={28}
        sql={{ agentGrowth: 'SELECT 1', agentGrowthBilling: 'SELECT 2' }}
      />,
    );

    expect(screen.getByText('Agent growth, year on year')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /SQL/i }).length).toBeGreaterThanOrEqual(2);
  });

  it('still shows the growth line when no agent is recent enough for the inventory', () => {
    renderWithProvider(
      <AgentsPanel
        estate={estate(windows({ 0: { activeAgents: 0, agentUsers: 0, agentInteractions: 0, interactionsPerAgentUser: null } }), {
          knownAgents: 0,
          activeAgents: 0,
        })}
        agents={[]}
        options={OPTIONS}
        windowDays={28}
        sql={null}
      />,
    );

    expect(screen.getByText('No Copilot agents found')).toBeInTheDocument();
    expect(screen.getByText('Agent growth, year on year')).toBeInTheDocument();
    expect(within(figure('Active agents')).getByText('0')).toBeInTheDocument();
    expect(within(figure('Active agents')).getByText('A year earlier: 4')).toBeInTheDocument();
  });

  it('leaves the empty state alone when the series has nothing measured either', () => {
    const blank = windows(Object.fromEntries(Array.from({ length: 14 }, (_, n) => [n, {
      activeAgents: null, agentUsers: null, agentInteractions: null, interactionsPerAgentUser: null,
    }])));
    renderWithProvider(
      <AgentsPanel estate={estate(blank, { knownAgents: 0 })} agents={[]} options={OPTIONS} windowDays={28} sql={null} />,
    );

    expect(screen.getByText('No Copilot agents found')).toBeInTheDocument();
    expect(screen.queryByText('Agent growth, year on year')).toBeNull();
  });
});
