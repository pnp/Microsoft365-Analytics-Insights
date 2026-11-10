import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProvider } from '../test/renderWithProvider';
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
import type { AgentCostAvailability, AgentCostBreakdownRow, AgentCostSummary } from '../types/agentCosts';

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

const FAILED = 'These figures could not be loaded, so none are shown. Refresh to try again.';

const availability: AgentCostAvailability = {
  copilotStudioCreditsEnabled: true,
  azureCostsEnabled: true,
  hasCopilotStudioCreditData: true,
  hasPerUserCreditData: true,
  copilotStudioConnectionRequired: false,
  hasAzureCostData: true,
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

const summary: AgentCostSummary = {
  billedCredits: 1234,
  nonBilledCredits: 0,
  distinctAgents: 3,
  distinctEnvironments: 1,
  daysWithUsage: 10,
  peakDistinctUsersOnASlice: 5,
  unclassifiedHarnessCredits: 0,
  minPeoplePerSlice: null,
  smallSlicesWithheld: false,
  withheld: false,
  capacity: null,
  azureCost: [],
};

const agentRow = (billedCredits: number): AgentCostBreakdownRow => ({
  key: '00000000-0000-0000-0000-000000000001',
  label: 'Contoso helpdesk agent',
  billedCredits,
  nonBilledCredits: 0,
  activeDays: 3,
  peakDistinctUsers: 2,
  folded: 0,
  withheld: false,
});

const breakdownRow = () => screen.getByText('Contoso helpdesk agent', { selector: 'td' }).closest('tr')!;

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchAvailability).mockResolvedValue(availability);
  vi.mocked(fetchFilterOptions).mockResolvedValue({ agents: [], environments: [], harnesses: [], features: [] });
  vi.mocked(fetchSummary).mockResolvedValue(summary);
  vi.mocked(fetchTrend).mockResolvedValue([]);
  vi.mocked(fetchTopUsers).mockResolvedValue([]);
  vi.mocked(fetchBreakdown).mockResolvedValue([agentRow(617)]);
  vi.mocked(fetchDetail).mockResolvedValue({ rows: [], totalRows: 0, page: 1, pageSize: 50, minPeoplePerSlice: null, smallSlicesWithheld: false });
  vi.mocked(fetchAzureBreakdown).mockResolvedValue([]);
  vi.mocked(fetchAllDetailRows).mockResolvedValue({ rows: [], truncated: false, totalRows: 0 });
});

describe('AgentCostsPage - a failed read', () => {
  it('says the figures could not be loaded, never zero spend or imports that are switched off', async () => {
    const outage = new Error('Could not load the agent cost figures (HTTP 500).');
    for (const fetcher of [fetchAvailability, fetchFilterOptions, fetchSummary, fetchTrend, fetchTopUsers, fetchBreakdown, fetchDetail, fetchAzureBreakdown]) {
      vi.mocked(fetcher).mockRejectedValue(outage);
    }

    renderWithProvider(<AgentCostsPage />);
    await waitFor(() => expect(screen.queryByText('Loading agent costs...')).not.toBeInTheDocument());

    expect(screen.queryByText('Copilot Credits billed')).not.toBeInTheDocument();
    expect(screen.queryByText('The Copilot Studio credit import is switched off.')).not.toBeInTheDocument();
    expect(screen.queryByText('The Azure cost import is switched off.')).not.toBeInTheDocument();
    expect(screen.queryByText('No credit usage in this period.')).not.toBeInTheDocument();
    expect(screen.queryByText('No billed lines match the current filters.')).not.toBeInTheDocument();
    // Spend, trend, breakdown, billed lines, people and Azure: each card says it has nothing, and why.
    expect(screen.getAllByText(FAILED)).toHaveLength(6);
  });

  it('keeps the figures that did load when only one request fails', async () => {
    vi.mocked(fetchTrend).mockRejectedValue(new Error('Could not load the daily trend (HTTP 500).'));

    renderWithProvider(<AgentCostsPage />);

    const billed = (await screen.findByText('Copilot Credits billed')).parentElement!;
    expect(within(billed).getByText('1,234')).toBeInTheDocument();
    expect(screen.getByText('Could not load the daily trend (HTTP 500).')).toBeInTheDocument();
    expect(screen.getAllByText(FAILED)).toHaveLength(1);
    expect(screen.queryByText('No credit usage in this period.')).not.toBeInTheDocument();
  });

  it('does not leave the previous period\u2019s figures under a period whose figures failed to load', async () => {
    const user = userEvent.setup();
    renderWithProvider(<AgentCostsPage />);
    expect(within((await screen.findByText('Copilot Credits billed')).parentElement!).getByText('1,234')).toBeInTheDocument();
    await waitFor(() => expect(within(breakdownRow()).getByText('50%')).toBeInTheDocument());

    vi.mocked(fetchSummary).mockRejectedValue(new Error('Could not load the agent cost figures (HTTP 500).'));
    vi.mocked(fetchBreakdown).mockResolvedValue([agentRow(25)]);
    await user.selectOptions(screen.getByLabelText('Reporting period'), '7');

    await waitFor(() => expect(screen.queryByText('1,234')).not.toBeInTheDocument());
    expect(await screen.findByText(FAILED)).toBeInTheDocument();
    expect(screen.queryByText('Copilot Credits billed')).not.toBeInTheDocument();
    // The new period's breakdown has no total to be a share of - not a share of the old period's total.
    await waitFor(() => expect(within(breakdownRow()).getByText('25')).toBeInTheDocument());
    expect(within(breakdownRow()).getByText('\u2014')).toBeInTheDocument();
    expect(within(breakdownRow()).queryByText('2%')).not.toBeInTheDocument();
  });

  it('shows the new period\u2019s figures, and only them, when the period changes successfully', async () => {
    const user = userEvent.setup();
    renderWithProvider(<AgentCostsPage />);
    expect(within((await screen.findByText('Copilot Credits billed')).parentElement!).getByText('1,234')).toBeInTheDocument();

    vi.mocked(fetchSummary).mockResolvedValue({ ...summary, billedCredits: 100 });
    vi.mocked(fetchBreakdown).mockResolvedValue([agentRow(25)]);
    await user.selectOptions(screen.getByLabelText('Reporting period'), '7');

    expect(await screen.findByText('100')).toBeInTheDocument();
    expect(screen.queryByText('1,234')).not.toBeInTheDocument();
    await waitFor(() => expect(within(breakdownRow()).getByText('25%')).toBeInTheDocument());
    expect(screen.queryByText(FAILED)).not.toBeInTheDocument();
  });
});
