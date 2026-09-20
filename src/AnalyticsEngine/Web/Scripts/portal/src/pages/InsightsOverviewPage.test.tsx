import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import InsightsOverviewPage from './InsightsOverviewPage';
import type { SystemStatus } from '../types/systemStatus';
import type { DataOverviewSection, HealthSummary } from '../types/health';

const mockStatus = vi.fn();
const mockHealthSummary = vi.fn();
const mockHealthData = vi.fn();

vi.mock('../api/systemStatusApi', () => ({
  fetchSystemStatus: () => mockStatus(),
}));

vi.mock('../api/healthApi', () => ({
  fetchHealthSummary: () => mockHealthSummary(),
  fetchHealthData: () => mockHealthData(),
}));

const status = (over: Partial<SystemStatus> = {}): SystemStatus => ({
  buildLabel: 'TEST_BUILD',
  hasValidConfig: true,
  dataCounts: [
    { key: 'users', name: 'Users', count: 1000, hint: 'People discovered by any import' },
    { key: 'auditEvents', name: 'Audit events', count: 401295, hint: 'Activity from the unified audit log' },
    { key: 'webHits', name: 'Web page hits', count: 0, hint: 'Page views from the SharePoint tracker' },
  ],
  enabledImports: ['Activity/audit', 'Web traffic'],
  importSettingsKnown: true,
  webhookEndpointUrl: null,
  callsImportEnabled: false,
  callWebhookState: 'Disabled',
  callWebhookExpiry: null,
  callWebhookStatusDetail: null,
  webAppConfigSQL: null,
  webAppConfigRedis: null,
  webAppConfigCognitive: null,
  cognitiveServiceEnabled: false,
  webAppConfigServiceBus: null,
  ...over,
});

const summary = (over: Partial<HealthSummary> = {}): HealthSummary => ({
  buildLabel: 'TEST_BUILD',
  loadedAtUtc: '2026-09-16T09:00:00Z',
  appInsightsConfigured: true,
  overallStatus: 'Healthy',
  overallReasons: [],
  sections: [
    { key: 'data', label: 'Data overview', status: 'Healthy', reasons: [] },
    { key: 'liveness', label: 'Import liveness', status: 'Degraded', reasons: ['No cycle in 30 hours'] },
  ],
  ...over,
});

const dataSection = (over: Partial<DataOverviewSection> = {}): DataOverviewSection => ({
  status: 'Healthy',
  reasons: [],
  loadedAtUtc: '2026-09-16T09:00:00Z',
  countsAreApproximate: true,
  hitCount: 0,
  activityCount: 401295,
  teamsCount: 0,
  sentEmailCount: 0,
  callRecordCount: 0,
  copilotChatCount: 0,
  userCount: 1000,
  teamsBeingTrackedCount: 0,
  databaseSizeMb: 1024,
  auditEventsLast24h: 12345,
  auditEventsLast7d: 99999,
  hitsLast24h: 0,
  hitsLast7d: 0,
  newestHitUtc: new Date(Date.now() - 5 * 60_000).toISOString(),
  newestAuditEventUtc: new Date(Date.now() - 5 * 60_000).toISOString(),
  countsError: null,
  recentVolumeError: null,
  dataError: null,
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  mockStatus.mockResolvedValue(status());
  mockHealthSummary.mockResolvedValue(summary());
  mockHealthData.mockResolvedValue(dataSection());
});

describe('InsightsOverviewPage', () => {
  it('shows a tile per returned figure, with its formatted value and hint', async () => {
    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText('Users')).toBeInTheDocument();
    expect(screen.getByText('1,000')).toBeInTheDocument();
    expect(screen.getByText('Audit events')).toBeInTheDocument();
    expect(screen.getByText('401,295')).toBeInTheDocument();
    expect(screen.getByText('Activity from the unified audit log')).toBeInTheDocument();
    // The build label moved out of the page title into a badge beside it.
    expect(screen.getByRole('heading', { name: 'Overview' })).toBeInTheDocument();
    expect(screen.getByText('TEST_BUILD')).toBeInTheDocument();
  });

  it('lists the imports that are switched on', async () => {
    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText('Activity/audit')).toBeInTheDocument();
    expect(screen.getByText('Web traffic')).toBeInTheDocument();
  });

  it('shows the health roll-up and its per-section statuses once they arrive', async () => {
    renderWithProvider(<InsightsOverviewPage />);

    // "Healthy" appears twice - the overall badge and the Data overview section chip.
    expect(await screen.findByRole('heading', { name: 'System health' })).toBeInTheDocument();
    expect(screen.getAllByText('Healthy').length).toBeGreaterThanOrEqual(2);
    expect(screen.getByText('Import liveness')).toBeInTheDocument();
    expect(screen.getByText('Degraded')).toBeInTheDocument();
  });

  it('shows freshness and 24h volume for the workloads that are switched on', async () => {
    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText('Newest audit event')).toBeInTheDocument();
    expect(screen.getByText('Audit events in the last 24h')).toBeInTheDocument();
    expect(screen.getByText('12,345')).toBeInTheDocument();
    // webHits is in the counts, so its freshness row is shown too.
    expect(screen.getByText('Newest web page hit')).toBeInTheDocument();
  });

  it('omits freshness for a workload whose import is off', async () => {
    mockStatus.mockResolvedValue(
      status({ dataCounts: [{ key: 'users', name: 'Users', count: 10, hint: null }] }),
    );

    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText('Users')).toBeInTheDocument();
    expect(screen.queryByText('Newest audit event')).not.toBeInTheDocument();
    expect(screen.queryByText('Newest web page hit')).not.toBeInTheDocument();
  });

  it('keeps the data figures when the health roll-up fails', async () => {
    mockHealthSummary.mockRejectedValue(new Error('App Insights unavailable'));

    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText(/App Insights unavailable/)).toBeInTheDocument();
    expect(screen.getByText('401,295')).toBeInTheDocument();
  });

  it('keeps the page usable when the heavy freshness scan fails', async () => {
    mockHealthData.mockRejectedValue(new Error('timeout'));

    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText('Audit events')).toBeInTheDocument();
    expect(screen.queryByText(/timeout/)).not.toBeInTheDocument();
  });

  it('warns when every figure is still zero', async () => {
    mockStatus.mockResolvedValue(
      status({
        dataCounts: [
          { key: 'users', name: 'Users', count: 0, hint: null },
          { key: 'auditEvents', name: 'Audit events', count: 0, hint: null },
        ],
      }),
    );

    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText(/Every figure is still zero/)).toBeInTheDocument();
  });

  it('explains an empty overview rather than showing a bare grid', async () => {
    mockStatus.mockResolvedValue(status({ dataCounts: [], enabledImports: [] }));

    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText(/No imports are switched on/)).toBeInTheDocument();
  });

  it('says so when the import settings could not be read', async () => {
    mockStatus.mockResolvedValue(status({ importSettingsKnown: false }));

    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText(/every figure is shown/)).toBeInTheDocument();
  });

  it('surfaces a failed system status as an error', async () => {
    mockStatus.mockRejectedValue(new Error("Couldn't load system status (500)."));

    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText("Couldn't load system status (500).")).toBeInTheDocument();
  });

  it('only points at the pages this deployment can populate', async () => {
    renderWithProvider(<InsightsOverviewPage />);

    // Always relevant.
    expect(await screen.findByText('Reports')).toBeInTheDocument();
    expect(screen.getByText('Service health')).toBeInTheDocument();
    // Gated: no Copilot, DLP, agent-cost or Teams figures in the default fixture.
    expect(screen.queryByText('Copilot Adoption')).not.toBeInTheDocument();
    expect(screen.queryByText('DLP impact')).not.toBeInTheDocument();
    expect(screen.queryByText('Agent costs')).not.toBeInTheDocument();
    expect(screen.queryByText('Teams permissions')).not.toBeInTheDocument();
  });

  it('points at the gated pages once their figures are present', async () => {
    mockStatus.mockResolvedValue(
      status({
        dataCounts: [
          { key: 'copilotInteractions', name: 'Copilot interactions', count: 5, hint: null },
          { key: 'dlpMatches', name: 'DLP rule matches', count: 2, hint: null },
          { key: 'teams', name: 'Teams discovered', count: 13, hint: null },
          { key: 'azureCostDays', name: 'Azure cost days', count: 30, hint: null },
        ],
      }),
    );

    renderWithProvider(<InsightsOverviewPage />);

    expect(await screen.findByText('Copilot Adoption')).toBeInTheDocument();
    expect(screen.getByText('DLP impact')).toBeInTheDocument();
    expect(screen.getByText('Teams permissions')).toBeInTheDocument();
    expect(screen.getByText('Agent costs')).toBeInTheDocument();
  });
});
