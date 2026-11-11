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
import type { AgentCostAvailability, AgentCostSummary } from '../types/agentCosts';

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
  distinctAgents: 1,
  distinctEnvironments: 1,
  daysWithUsage: 2,
  peakDistinctUsersOnASlice: 5,
  unclassifiedHarnessCredits: 0,
  minPeoplePerSlice: null,
  smallSlicesWithheld: false,
  withheld: false,
  capacity: null,
  azureCost: [],
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchAvailability).mockResolvedValue(availability);
  vi.mocked(fetchFilterOptions).mockResolvedValue({ agents: [], environments: [], harnesses: [], features: [] });
  vi.mocked(fetchSummary).mockResolvedValue(summary);
  vi.mocked(fetchTrend).mockResolvedValue([
    { date: '2026-06-01T00:00:00', billedCredits: 1000, nonBilledCredits: 0 },
    { date: '2026-06-02T00:00:00', billedCredits: 234, nonBilledCredits: 0 },
  ]);
  vi.mocked(fetchTopUsers).mockResolvedValue([]);
  vi.mocked(fetchBreakdown).mockResolvedValue([]);
  vi.mocked(fetchDetail).mockResolvedValue({
    rows: [{
      usageDate: '2026-06-01T00:00:00',
      environmentId: null,
      environmentName: 'Contoso default',
      agentId: '00000000-0000-0000-0000-000000000001',
      agentName: 'Contoso helpdesk agent',
      harness: null,
      featureName: 'Contoso feature',
      billedCredits: 1234,
      nonBilledCredits: 0,
      distinctUsers: 5,
    }],
    totalRows: 1,
    page: 1,
    pageSize: 50,
    minPeoplePerSlice: null,
    smallSlicesWithheld: false,
  });
  vi.mocked(fetchAzureBreakdown).mockResolvedValue([]);
  vi.mocked(fetchAllDetailRows).mockResolvedValue({ rows: [], truncated: false, totalRows: 0 });
});

describe('AgentCostsPage - the billed lines ledger', () => {
  it('sorts from the keyboard and announces the sort order on each header', async () => {
    const user = userEvent.setup();
    renderWithProvider(<AgentCostsPage />);
    const ledger = (await screen.findByText('Contoso helpdesk agent', { selector: 'td' })).closest('table')!;

    const credits = within(ledger).getByRole('columnheader', { name: /Credits/ });
    expect(credits).toHaveAttribute('aria-sort', 'descending');
    expect(within(ledger).getByRole('columnheader', { name: /Day/ })).toHaveAttribute('aria-sort', 'none');

    within(ledger).getByRole('button', { name: /Day/ }).focus();
    await user.keyboard('{Enter}');

    await waitFor(() => expect(fetchDetail).toHaveBeenLastCalledWith(expect.objectContaining({ sort: 'date', direction: 'desc' }), expect.anything()));
    const sortedTable = (await screen.findByText('Contoso helpdesk agent', { selector: 'td' })).closest('table')!;
    expect(within(sortedTable).getByRole('columnheader', { name: /Day/ })).toHaveAttribute('aria-sort', 'descending');

    within(sortedTable).getByRole('button', { name: /Day/ }).focus();
    await user.keyboard(' ');
    await waitFor(() => expect(fetchDetail).toHaveBeenLastCalledWith(expect.objectContaining({ sort: 'date', direction: 'asc' }), expect.anything()));
  }, 60_000);

  it('gives each day of the trend a label a screen reader can read', async () => {
    renderWithProvider(<AgentCostsPage />);

    const strip = await screen.findByRole('list', { name: 'Credits per day' });
    const days = within(strip).getAllByRole('listitem');
    expect(days).toHaveLength(2);
    expect(days[0]).toHaveAccessibleName(/1,000 credits$/);
  });
});
