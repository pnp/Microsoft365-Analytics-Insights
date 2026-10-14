import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { screen, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import {
  AGENT_FIGURES_SCOPE_KEYS,
  AGENT_REACH_DEPARTMENT_THRESHOLD,
  CombinedSegmentTable,
  type AgentAdoptionTotals,
} from './CombinedViews';
import { AgentReachCells, AgentReachHeaderCells } from './AgentReachColumns';
import AgentsPanel from './AgentsPanel';
import { AgentHealth } from '../../types/copilotAdoption';
import type {
  AdoptionCombinedSegmentRow,
  AgentEstateSummary,
  AgentUsageRow,
  CopilotAdoptionOptions,
} from '../../types/copilotAdoption';
import { EN_CATALOG, loadCatalog } from '../../i18n';
import ES_COPILOT_ADOPTION from '../../i18n/catalog/es/copilotAdoption';

const COPILOT_ADOPTION_DIR = join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'CopilotAdoption');

const GREEK = 'Καλημέρα κόσμε';

function segment(over: Partial<AdoptionCombinedSegmentRow>): AdoptionCombinedSegmentRow {
  return {
    segment: 'Finance',
    licensedUsers: 30,
    licensedActiveUsers: 10,
    interactionsPerLicensedUser: 4,
    licensedAgentUserPct: 10,
    unlicensedActiveUsers: 6,
    interactionsPerUnlicensedUser: 12,
    unlicensedAgentUserPct: 20,
    agentActiveUsers: 16,
    agentUsers: 4,
    agentUserPct: 25,
    distinctAgents: 3,
    agentsPer100ActiveUsers: 18.8,
    agentInteractions: 50,
    interactionsPerActiveAgent: 16.7,
    agentBuilders: 2,
    ...over,
  };
}

const ROWS: AdoptionCombinedSegmentRow[] = [
  segment({}),
  // Tenant data renders exactly as stored. Builders unknown: the authoring feed is not imported.
  segment({ segment: GREEK, agentBuilders: null, interactionsPerActiveAgent: null, distinctAgents: 0, agentsPer100ActiveUsers: 0 }),
];

const TOTALS: AgentAdoptionTotals = {
  agentFiguresScope: 'customerBuiltAgents',
  agentBreadthDepartments: 3,
  agentBreadthDepartmentsWithAgentUsers: 2,
  agentBreadthDepartmentPct: 66.7,
  agentActiveUsers: 1500,
  agentBreadthAgentUsers: 600,
  agentBreadthUserPct: 40,
  agentDepthAgentsPer100ActiveUsers: 13.3,
  agentDepthInteractionsPerActiveAgent: 1234.5,
  agentUnknownOriginAgents: 3,
  agentBuilders: null,
  agentsInThreeOrMoreDepartments: 4,
  agentsInThreeOrMoreDepartmentsUnknownOrigin: 2,
};

function headerTexts(): string[] {
  return screen.getAllByRole('columnheader').map((th) => th.textContent ?? '');
}

describe('Department table: agent breadth, depth and builders (#646, #647)', () => {
  it('adds the columns in English, with help text, and a dash for what was not measured', () => {
    renderWithProvider(<CombinedSegmentTable rows={ROWS} agentTotals={TOTALS} minSeatsPerSegment={5} />);

    expect(headerTexts()).toEqual(expect.arrayContaining([
      'Using customer-built agents', 'Customer-built agents', 'Agents per 100 users', 'Interactions per agent', 'Agent builders',
    ]));
    expect(screen.getByRole('columnheader', { name: 'Agent builders' })).toHaveAttribute(
      'title',
      expect.stringContaining('A count, never a name'),
    );

    const finance = within(screen.getByText('Finance').closest('tr')!);
    expect(finance.getByText('25%')).toHaveAttribute('title', '4 of 16 active users');
    expect(finance.getByText('18.8')).toBeInTheDocument();
    expect(finance.getByText('16.7')).toBeInTheDocument();

    const greek = within(screen.getByText(GREEK).closest('tr')!);
    expect(greek.getAllByText('\u2014')).toHaveLength(2);
  });

  it('states the tenant-wide figures above the table and names which agents they count', () => {
    renderWithProvider(<CombinedSegmentTable rows={ROWS} agentTotals={TOTALS} minSeatsPerSegment={5} />);

    expect(screen.getByText('Agent breadth and depth')).toBeInTheDocument();
    expect(screen.getByText(/Agents counted: customer-built agents only - Microsoft's agents and agents of unknown origin are not counted\./)).toBeInTheDocument();
    expect(screen.getByTestId('agent-total-unknown')).toHaveTextContent(
      '3 agents of unknown origin were used in this period and are not counted, so these figures are a floor.',
    );
    expect(within(screen.getByTestId('agent-total-departments')).getByText('2 of 3 (66.7%)')).toBeInTheDocument();
    expect(within(screen.getByTestId('agent-total-users')).getByText('600 of 1,500 (40%)')).toBeInTheDocument();
    expect(within(screen.getByTestId('agent-total-per100')).getByText('13.3')).toBeInTheDocument();
    expect(within(screen.getByTestId('agent-total-perAgent')).getByText('1,234.5')).toBeInTheDocument();
    expect(within(screen.getByTestId('agent-total-builders')).getByText('\u2014')).toBeInTheDocument();
    expect(within(screen.getByTestId('agent-total-reach')).getByText('Agents used in 3 or more departments')).toBeInTheDocument();
    expect(within(screen.getByTestId('agent-total-reach')).getByText('4')).toBeInTheDocument();
    expect(within(screen.getByTestId('agent-total-reach')).getByText('+2 of unknown origin, not counted')).toBeInTheDocument();
  });

  it('says one agent of unknown origin in the singular, and nothing when there is none', () => {
    const { unmount } = renderWithProvider(
      <CombinedSegmentTable rows={ROWS} agentTotals={{ ...TOTALS, agentUnknownOriginAgents: 1 }} minSeatsPerSegment={5} />,
    );
    expect(screen.getByTestId('agent-total-unknown')).toHaveTextContent(
      '1 agent of unknown origin was used in this period and is not counted, so these figures are a floor.',
    );
    unmount();

    renderWithProvider(
      <CombinedSegmentTable
        rows={ROWS}
        agentTotals={{ ...TOTALS, agentUnknownOriginAgents: 0, agentsInThreeOrMoreDepartmentsUnknownOrigin: 0 }}
        minSeatsPerSegment={5}
      />,
    );
    expect(screen.queryByTestId('agent-total-unknown')).toBeNull();
    expect(screen.queryByText(/of unknown origin, not counted/)).toBeNull();
  });

  it('renders the same in Spanish, with Spanish number formatting and tenant data untouched', async () => {
    await loadCatalog('es');
    renderWithProvider(<CombinedSegmentTable rows={ROWS} agentTotals={TOTALS} minSeatsPerSegment={5} />, { language: 'es' });

    expect(await screen.findByText('Amplitud y profundidad de los agentes', undefined, { timeout: 5000 })).toBeInTheDocument();
    expect(headerTexts()).toEqual(expect.arrayContaining([
      'Usan agentes de su organización', 'Agentes de su organización', 'Agentes por cada 100 usuarios', 'Interacciones por agente', 'Creadores de agentes',
    ]));
    expect(screen.getByText(/Agentes contabilizados: solo los agentes de su organización: no se cuentan los agentes de Microsoft ni los de origen desconocido\./)).toBeInTheDocument();
    expect(screen.getByTestId('agent-total-unknown')).toHaveTextContent(
      '3 agentes de origen desconocido se usaron en este periodo y no se cuentan, por lo que estas cifras son un mínimo.',
    );
    expect(within(screen.getByTestId('agent-total-reach')).getByText('+2 de origen desconocido, no contados')).toBeInTheDocument();
    expect(within(screen.getByTestId('agent-total-departments')).getByText('2 de 3 (66,7%)')).toBeInTheDocument();
    expect(within(screen.getByTestId('agent-total-perAgent')).getByText('1234,5')).toBeInTheDocument();
    expect(within(screen.getByText('Finance').closest('tr')!).getByText('18,8')).toBeInTheDocument();
    expect(screen.getByText(GREEK)).toBeInTheDocument();
    expect(screen.queryByText('Using customer-built agents')).toBeNull();
  });
});

const OPTIONS: CopilotAdoptionOptions = {
  windowDays: 28,
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

function agent(over: Partial<AgentUsageRow>): AgentUsageRow {
  return {
    agentId: 1,
    name: 'Contoso Expenses Helper',
    agentKey: null,
    origin: 'customerBuilt',
    isCustomAgent: true,
    interactions: 100,
    users: 11,
    licensedUsers: 4,
    activeDays: 12,
    appsUsed: 2,
    interactionsPerUser: 9,
    firstUsedUtc: '2026-01-01T00:00:00Z',
    lastUsedUtc: '2026-02-01T00:00:00Z',
    daysSinceLastUse: 3,
    health: AgentHealth.Keep,
    healthName: 'Keep',
    healthReason: 'Used recently by enough people.',
    windowUsers: 11,
    departments: 4,
    homeDepartment: GREEK,
    homeDepartmentSharePct: 45.5,
    ...over,
  };
}

const AGENTS: AgentUsageRow[] = [
  agent({ agentId: 1 }),
  agent({ agentId: 2, name: 'Fabrikam Travel Booker', departments: 2, homeDepartment: null, homeDepartmentSharePct: 75, windowUsers: 4 }),
  agent({ agentId: 3, name: 'Northwind Ticket Triage', departments: null, homeDepartment: null, homeDepartmentSharePct: null, windowUsers: null }),
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
  usageByDepartment: [],
  usageByAgent: [],
  agents: AGENTS,
};

describe('Agent inventory: reach across departments (#647)', () => {
  it('shows departments reached and the home department with its share, withholding a small one', () => {
    renderWithProvider(<AgentsPanel estate={ESTATE} agents={AGENTS} options={OPTIONS} windowDays={28} sql={null} />);

    expect(screen.getByRole('columnheader', { name: 'Departments' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Home department' })).toHaveAttribute(
      'title',
      expect.stringContaining('fewer than 5 of its users'),
    );

    const spread = within(screen.getByText('Contoso Expenses Helper').closest('tr')!);
    expect(spread.getByText('4')).toBeInTheDocument();
    expect(spread.getByText(GREEK)).toBeInTheDocument();
    expect(spread.getByText('45.5% of 11 users')).toBeInTheDocument();

    const small = within(screen.getByText('Fabrikam Travel Booker').closest('tr')!);
    expect(small.getByText('Not named (fewer than 5 users)')).toBeInTheDocument();
    expect(small.getByText('75% of 4 users')).toBeInTheDocument();

    const unknown = within(screen.getByText('Northwind Ticket Triage').closest('tr')!);
    expect(unknown.getAllByText('\u2014').length).toBeGreaterThanOrEqual(2);
  });

  it('renders the reach columns in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <table>
        <thead>
          <tr>
            <AgentReachHeaderCells minSeatsPerSegment={5} />
          </tr>
        </thead>
        <tbody>
          <tr>
            <AgentReachCells agent={AGENTS[1]} minSeatsPerSegment={5} />
          </tr>
        </tbody>
      </table>,
      { language: 'es' },
    );

    expect(await screen.findByRole('columnheader', { name: 'Departamento de origen' }, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Departamentos' })).toBeInTheDocument();
    expect(screen.getByText('Sin nombre (menos de 5 usuarios)')).toBeInTheDocument();
    expect(screen.getByText('75% de 4 usuarios')).toBeInTheDocument();
  });
});

describe('Agent breadth and reach constants stay in step with the server', () => {
  it('uses the same department threshold as CopilotAdoptionService', () => {
    const service = readFileSync(join(COPILOT_ADOPTION_DIR, 'CopilotAdoptionService.cs'), 'utf8');
    const threshold = /public const int AgentReachDepartmentThreshold = (\d+);/.exec(service)?.[1];
    expect(Number(threshold)).toBe(AGENT_REACH_DEPARTMENT_THRESHOLD);
  });

  it('words every agent scope the server can send, in both languages', () => {
    const models = readFileSync(join(COPILOT_ADOPTION_DIR, 'CopilotAdoptionAgentModels.cs'), 'utf8');
    const scopeClass = /public static class CopilotAgentFigureScope\s*\{([\s\S]*?)\n {4}\}/.exec(models)?.[1] ?? '';
    const scopes = [...scopeClass.matchAll(/public const string \w+ = "([^"]+)";/g)].map((m) => m[1]);

    expect(scopes.length).toBeGreaterThan(0);
    expect(Object.keys(AGENT_FIGURES_SCOPE_KEYS).sort()).toEqual([...scopes].sort());
    for (const key of Object.values(AGENT_FIGURES_SCOPE_KEYS)) {
      const spanish = (ES_COPILOT_ADOPTION as Record<string, string>)[key];
      expect(EN_CATALOG[key]).toBeTruthy();
      expect(spanish).toBeTruthy();
      expect(spanish).not.toBe(EN_CATALOG[key]);
    }
  });
});
