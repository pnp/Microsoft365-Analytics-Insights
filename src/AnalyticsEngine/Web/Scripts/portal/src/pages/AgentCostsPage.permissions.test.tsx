import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';

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
import type { AgentCostAvailability, AgentCostSummary, AgentCostUserRow } from '../types/agentCosts';

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

const summary: AgentCostSummary = {
  billedCredits: 1234,
  nonBilledCredits: 0,
  distinctAgents: 3,
  distinctEnvironments: 1,
  daysWithUsage: 10,
  peakDistinctUsersOnASlice: 5,
  unclassifiedHarnessCredits: 0,
  capacity: null,
  azureCost: [],
};

const maker: AgentCostUserRow = {
  userId: 1,
  entraObjectId: '00000000-0000-0000-0000-000000000001',
  userPrincipalName: 'maker@contoso.com',
  billedCredits: 12,
  activeDays: 3,
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchAvailability).mockResolvedValue(availability);
  vi.mocked(fetchFilterOptions).mockResolvedValue({ agents: [], environments: [], harnesses: [], features: [] });
  vi.mocked(fetchSummary).mockResolvedValue(summary);
  vi.mocked(fetchTrend).mockResolvedValue([]);
  vi.mocked(fetchTopUsers).mockResolvedValue([maker]);
  vi.mocked(fetchBreakdown).mockResolvedValue([]);
  vi.mocked(fetchDetail).mockResolvedValue({ rows: [], totalRows: 0, page: 1, pageSize: 50 });
  vi.mocked(fetchAzureBreakdown).mockResolvedValue([]);
  vi.mocked(fetchAllDetailRows).mockResolvedValue({ rows: [], truncated: false, totalRows: 0 });
});

describe('AgentCostsPage - See PII', () => {
  it('reports prepaid consumption and Azure spend separately without See PII', async () => {
    vi.mocked(fetchSummary).mockResolvedValue({
      ...summary,
      capacity: {
        snapshotUtc: '2026-01-10T00:00:00Z',
        consumptionAsOf: '2026-01-09T00:00:00Z',
        entitled: 25000,
        consumed: 1500,
        consumptionType: 'MonthToDate',
        allocated: 5000,
        available: 23500,
        payAsYouGoConsumed: 200,
        status: 'WithinCapacity',
      },
      azureCost: [{ currency: 'USD', cost: 2, quantity: 200, includesEstimates: true }],
    });
    renderWithProvider(<AgentCostsPage />, { access: { administration: true, seePii: false } });

    const prepaid = (await screen.findByText('Prepaid Copilot Credits used')).parentElement!;
    expect(within(prepaid).getByText('1,500')).toBeInTheDocument();
    expect(within(prepaid).getByText('1,500 of 25,000 used (Month to date)')).toBeInTheDocument();
    const spend = screen.getByText('Spend in the selected period').parentElement!.parentElement!;
    const azure = within(spend).getByText('Azure spend').parentElement!;
    expect(within(azure).getByText('2.00 USD')).toBeInTheDocument();
    expect(within(azure).getByText('200 metered units billed')).toBeInTheDocument();
    const payAsYouGo = screen.getByText('Pay-as-you-go credits').parentElement!;
    expect(within(payAsYouGo).getByText('200')).toBeInTheDocument();
    expect(screen.getByText('Chargeable usage, paid from prepaid credits or pay-as-you-go; not an Azure charge total')).toBeInTheDocument();
    expect(fetchTopUsers).not.toHaveBeenCalled();
  });

  it('shows confirmed zero prepaid consumption without inventing absent pay-as-you-go usage', async () => {
    vi.mocked(fetchSummary).mockResolvedValue({
      ...summary,
      capacity: {
        snapshotUtc: '2026-01-10T00:00:00Z',
        consumptionAsOf: null,
        entitled: 25000,
        consumed: 0,
        consumptionType: 'MonthToDate',
        allocated: null,
        available: 25000,
        payAsYouGoConsumed: null,
        status: null,
      },
    });
    renderWithProvider(<AgentCostsPage />, { access: { administration: true, seePii: false } });

    const prepaid = (await screen.findByText('Prepaid Copilot Credits used')).parentElement!;
    expect(within(prepaid).getByText('0')).toBeInTheDocument();
    expect(screen.queryByText('Pay-as-you-go credits')).not.toBeInTheDocument();
    const spend = screen.getByText('Spend in the selected period').parentElement!.parentElement!;
    expect(within(spend).queryByText('Azure spend')).not.toBeInTheDocument();
  });

  it('does not show missing prepaid consumption as zero', async () => {
    vi.mocked(fetchSummary).mockResolvedValue({
      ...summary,
      capacity: {
        snapshotUtc: '2026-01-10T00:00:00Z',
        consumptionAsOf: null,
        entitled: 25000,
        consumed: null,
        consumptionType: null,
        allocated: null,
        available: null,
        payAsYouGoConsumed: null,
        status: null,
      },
    });
    renderWithProvider(<AgentCostsPage />, { access: { administration: true, seePii: false } });

    const prepaid = (await screen.findByText('Prepaid Copilot Credits used')).parentElement!;
    expect(within(prepaid).queryByText('0')).not.toBeInTheDocument();
    expect(within(prepaid).getByText(/of 25,000 used/)).toHaveTextContent('— of 25,000 used');
  });

  it('shows a reader without See PII every total, and in place of the per-person table says why', async () => {
    renderWithProvider(<AgentCostsPage />, { access: { administration: true, seePii: false } });

    expect(await screen.findByText('Spend in the selected period')).toBeInTheDocument();
    expect(screen.getByText('Copilot Credits billed')).toBeInTheDocument();
    expect(screen.getByText('Individual details are hidden')).toBeInTheDocument();
    expect(screen.queryByText('Who is spending the credits')).not.toBeInTheDocument();
    expect(screen.queryByText('maker@contoso.com')).not.toBeInTheDocument();
    // Never requested: the server refuses the per-user rows to this reader.
    expect(fetchTopUsers).not.toHaveBeenCalled();
  });

  it('still shows the per-person table to a reader with See PII', async () => {
    renderWithProvider(<AgentCostsPage />, { access: { administration: false, seePii: true } });

    expect(await screen.findByText('maker@contoso.com')).toBeInTheDocument();
    expect(screen.getByText('Who is spending the credits')).toBeInTheDocument();
    expect(screen.queryByText('Individual details are hidden')).not.toBeInTheDocument();
    await waitFor(() => expect(fetchTopUsers).toHaveBeenCalled());
  });
});
