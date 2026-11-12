import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';

import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import AgentCostsPage from './AgentCostsPage';
import {
  fetchAllDetailRows,
  fetchAvailability,
  fetchAzureBreakdown,
  fetchBreakdown,
  fetchDetail,
  fetchFilterOptions,
  fetchSummary,
  fetchTopUsers,
  fetchTrend,
} from '../api/agentCostsApi';
import type {
  AgentCostAvailability,
  AgentCostBreakdownRow,
  AgentCostDetailPage,
  AgentCostSummary,
} from '../types/agentCosts';

vi.mock('../api/agentCostsApi', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api/agentCostsApi')>()),
  fetchAvailability: vi.fn(),
  fetchFilterOptions: vi.fn(),
  fetchSummary: vi.fn(),
  fetchTrend: vi.fn(),
  fetchTopUsers: vi.fn(),
  fetchBreakdown: vi.fn(),
  fetchDetail: vi.fn(),
  fetchAzureBreakdown: vi.fn(),
  fetchAllDetailRows: vi.fn(),
}));

const availability: AgentCostAvailability = {
  copilotStudioCreditsEnabled: true,
  azureCostsEnabled: false,
  hasCopilotStudioCreditData: true,
  hasPerUserCreditData: true,
  copilotStudioConnectionRequired: false,
  hasAzureCostData: false,
  copilotStudioCreditsHasRunCleanly: true,
  azureCostsHaveRunCleanly: true,
  copilotStudioCreditsLastImportUtc: null,
  azureCostsLastImportUtc: null,
  copilotStudioCreditsLastError: null,
  azureCostsLastError: null,
  perUserCreditsLastImportUtc: null,
  perUserCreditsLastError: null,
  capacityLastError: null,
  azureDimensionsWithData: [],
  creditDimensionsWithData: [],
  earliestUsageDate: null,
  latestUsageDate: null,
  messages: [],
};

/** What the API gives a reader without See PII when some billed lines had too few people: figures from the rest. */
const flooredSummary: AgentCostSummary = {
  billedCredits: 1234,
  nonBilledCredits: 10,
  distinctAgents: 2,
  distinctEnvironments: 1,
  daysWithUsage: 2,
  peakDistinctUsersOnASlice: 12,
  unclassifiedHarnessCredits: 0,
  minPeoplePerSlice: 5,
  smallSlicesWithheld: true,
  withheld: false,
  capacity: null,
  azureCost: [],
};

/** ...and when every billed line in the selection had too few people: nothing but the fact. */
const withheldSummary: AgentCostSummary = {
  ...flooredSummary,
  billedCredits: null,
  nonBilledCredits: null,
  distinctAgents: null,
  distinctEnvironments: null,
  daysWithUsage: null,
  peakDistinctUsersOnASlice: null,
  unclassifiedHarnessCredits: null,
  withheld: true,
};

const helpdesk: AgentCostBreakdownRow = {
  key: '00000000-0000-0000-0000-000000000001',
  label: 'Contoso helpdesk agent',
  billedCredits: 1234,
  nonBilledCredits: 10,
  activeDays: 2,
  peakDistinctUsers: 12,
  folded: 0,
  withheld: false,
};

const withheldRow = (folded: number): AgentCostBreakdownRow => ({
  key: null,
  label: null,
  billedCredits: null,
  nonBilledCredits: null,
  activeDays: null,
  peakDistinctUsers: null,
  folded,
  withheld: true,
});

const flooredDetail: AgentCostDetailPage = {
  rows: [{
    usageDate: '2026-06-01T00:00:00',
    environmentId: '00000000-0000-0000-0000-0000000000e1',
    environmentName: 'Contoso shared',
    agentId: helpdesk.key,
    agentName: 'Contoso helpdesk agent',
    harness: null,
    featureName: 'Generative answer',
    billedCredits: 1234,
    nonBilledCredits: 10,
    distinctUsers: 12,
  }],
  totalRows: 1,
  page: 1,
  pageSize: 50,
  minPeoplePerSlice: 5,
  smallSlicesWithheld: true,
};

const withoutSeePii = { administration: false, seePii: false };

beforeEach(async () => {
  vi.clearAllMocks();
  await loadCatalog('es');
  vi.mocked(fetchAvailability).mockResolvedValue(availability);
  vi.mocked(fetchFilterOptions).mockResolvedValue({ agents: [], environments: [], harnesses: [], features: [] });
  vi.mocked(fetchSummary).mockResolvedValue(flooredSummary);
  vi.mocked(fetchTrend).mockResolvedValue([
    { date: '2026-06-01T00:00:00', billedCredits: 1000, nonBilledCredits: 10 },
    { date: '2026-06-02T00:00:00', billedCredits: 234, nonBilledCredits: 0 },
  ]);
  vi.mocked(fetchTopUsers).mockResolvedValue([]);
  vi.mocked(fetchBreakdown).mockResolvedValue([helpdesk, withheldRow(2)]);
  vi.mocked(fetchDetail).mockResolvedValue(flooredDetail);
  vi.mocked(fetchAzureBreakdown).mockResolvedValue([]);
  vi.mocked(fetchAllDetailRows).mockResolvedValue({ rows: [], truncated: false, totalRows: 0 });
});

/** The cells of the breakdown table's row for the agents combined without figures. */
async function combinedRowCells(rowText: string): Promise<string[]> {
  const label = await screen.findByText(rowText, { selector: 'td' }, { timeout: 5000 });
  return within(label.closest('tr')!).getAllByRole('cell').map((cell) => cell.textContent ?? '');
}

describe('AgentCostsPage - the privacy floor for a reader without See PII', () => {
  it('says which billed lines every figure leaves out, and gives the combined agents no figures, never zero', async () => {
    renderWithProvider(<AgentCostsPage />, { access: withoutSeePii });

    expect(await screen.findByText(/leaves out the billed lines that Microsoft reports fewer than 5 people on/, {}, { timeout: 5000 }))
      .toBeInTheDocument();
    const billed = screen.getByText('Copilot Credits billed').parentElement!;
    expect(within(billed).getByText('1,234')).toBeInTheDocument();
    expect(screen.getByText('Days on which every billed line had fewer than 5 people aren’t shown.')).toBeInTheDocument();

    const cells = await combinedRowCells('2 more, each with fewer than 5 people on every billed line');
    expect(cells.slice(1)).toEqual(['Withheld', 'Withheld', 'Withheld', 'Withheld', 'Withheld']);
    expect(cells).not.toContain('0');

    expect(screen.getByText('Billed lines with fewer than 5 people, or with no number of people, aren’t listed, counted or exported.'))
      .toBeInTheDocument();
  });

  it('writes every figure of a selection only a handful of people used as withheld, not as zero or as no usage', async () => {
    vi.mocked(fetchSummary).mockResolvedValue(withheldSummary);
    vi.mocked(fetchTrend).mockResolvedValue([]);
    vi.mocked(fetchBreakdown).mockResolvedValue([withheldRow(1)]);
    vi.mocked(fetchDetail).mockResolvedValue({ ...flooredDetail, rows: [], totalRows: 0 });
    renderWithProvider(<AgentCostsPage />, { access: withoutSeePii });

    const billed = (await screen.findByText('Copilot Credits billed', {}, { timeout: 5000 })).parentElement!;
    expect(within(billed).getByText('Withheld')).toBeInTheDocument();
    for (const kpi of ['Credits not charged', 'Agents with spend', 'Environments', 'Busiest slice']) {
      expect(within(screen.getByText(kpi).parentElement!).getByText('Withheld')).toBeInTheDocument();
    }
    // The summary, the trend and the billed lines each say why, in place of "nothing happened".
    expect(screen.getAllByText(/^Withheld\. Microsoft reports fewer than 5 people on every billed line/)).toHaveLength(3);
    expect(screen.queryByText('No credit usage in this period.')).not.toBeInTheDocument();
    expect(screen.queryByText('No billed lines match the current filters.')).not.toBeInTheDocument();

    const cells = await combinedRowCells('One more, with fewer than 5 people on every billed line');
    expect(cells.slice(1)).toEqual(['Withheld', 'Withheld', 'Withheld', 'Withheld', 'Withheld']);
  });

  it('says the same in Spanish', async () => {
    renderWithProvider(<AgentCostsPage />, { access: withoutSeePii, language: 'es' });

    expect(await screen.findByText(/excluyen las líneas facturadas en las que Microsoft informa de menos de 5 personas/, {}, { timeout: 5000 }))
      .toBeInTheDocument();
    expect(screen.getByText('No se muestran los días en los que todas las líneas facturadas tenían menos de 5 personas.')).toBeInTheDocument();
    const cells = await combinedRowCells('2 más, cada uno con menos de 5 personas en cada línea facturada');
    expect(cells.slice(1)).toEqual(['Oculto', 'Oculto', 'Oculto', 'Oculto', 'Oculto']);
    expect(screen.getByText('Las líneas facturadas con menos de 5 personas, o sin número de personas, no se muestran, no se cuentan y no se exportan.'))
      .toBeInTheDocument();
  });

  it('writes a withheld selection as withheld in Spanish too', async () => {
    vi.mocked(fetchSummary).mockResolvedValue(withheldSummary);
    vi.mocked(fetchTrend).mockResolvedValue([]);
    vi.mocked(fetchBreakdown).mockResolvedValue([withheldRow(1)]);
    vi.mocked(fetchDetail).mockResolvedValue({ ...flooredDetail, rows: [], totalRows: 0 });
    renderWithProvider(<AgentCostsPage />, { access: withoutSeePii, language: 'es' });

    const billed = (await screen.findByText('Copilot Credits facturados', {}, { timeout: 5000 })).parentElement!;
    expect(within(billed).getByText('Oculto')).toBeInTheDocument();
    expect(screen.getAllByText(/^Oculto\. Microsoft informa de menos de 5 personas en cada línea facturada/)).toHaveLength(3);
    expect(screen.queryByText('No hay consumo de créditos en este periodo.')).not.toBeInTheDocument();
    const cells = await combinedRowCells('Uno más, con menos de 5 personas en cada línea facturada');
    expect(cells.slice(1)).toEqual(['Oculto', 'Oculto', 'Oculto', 'Oculto', 'Oculto']);
  });

  it('shows a reader with See PII no floor note: their figures cover every billed line', async () => {
    vi.mocked(fetchSummary).mockResolvedValue({ ...flooredSummary, minPeoplePerSlice: null, smallSlicesWithheld: false });
    vi.mocked(fetchBreakdown).mockResolvedValue([helpdesk]);
    vi.mocked(fetchDetail).mockResolvedValue({ ...flooredDetail, minPeoplePerSlice: null, smallSlicesWithheld: false });
    renderWithProvider(<AgentCostsPage />, { access: { administration: false, seePii: true } });

    expect((await screen.findAllByText('Contoso helpdesk agent', { selector: 'td' }, { timeout: 5000 })).length).toBeGreaterThan(0);
    expect(screen.queryByText(/fewer than 5 people/)).not.toBeInTheDocument();
    expect(screen.queryByText('Withheld')).not.toBeInTheDocument();
  });
});
