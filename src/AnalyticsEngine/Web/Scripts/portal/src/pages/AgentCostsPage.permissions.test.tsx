import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';

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
