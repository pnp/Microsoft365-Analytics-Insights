import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { fetchHealthComponents } from '../../api/healthApi';
import { renderWithProvider } from '../../test/renderWithProvider';
import { loadCatalog } from '../../i18n';
import ComponentsPanel from './ComponentsPanel';
import type { ComponentsSection } from '../../types/health';

vi.mock('../../api/healthApi', () => ({ fetchHealthComponents: vi.fn() }));

const section: ComponentsSection = {
  loadedAtUtc: '2026-01-01T00:00:00Z',
  status: 'Degraded',
  reasons: [],
  appInsightsConfigured: false,
  componentHealthError: null,
  componentHealth: [{
    component: 'PowerPlatformConnection',
    status: 'Degraded',
    detail: 'Server fallback',
    reasonKey: 'agentCostConnection.disconnected',
    errorCode: null,
    httpStatus: null,
    daysToExpiry: null,
    lastSeenUtc: null,
  }],
};

beforeEach(() => {
  vi.mocked(fetchHealthComponents).mockResolvedValue(section);
});

describe('Power Platform connection in Component health', () => {
  it('shows the connection issue even when the telemetry query fails', async () => {
    vi.mocked(fetchHealthComponents).mockResolvedValue({ ...section, componentHealthError: 'Synthetic telemetry failure' });
    renderWithProvider(<ComponentsPanel active />);
    expect(await screen.findByText('Power Platform billing connection')).toBeInTheDocument();
    expect(screen.getByText(/Agent credit tracking is enabled but no Power Platform/)).toBeInTheDocument();
    expect(screen.getByText(/Synthetic telemetry failure/)).toBeInTheDocument();
  });

  it('renders the connection label and guidance in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(<ComponentsPanel active />, { language: 'es' });
    expect(await screen.findByText('Conexión de facturación de Power Platform')).toBeInTheDocument();
    expect(screen.getByText(/El seguimiento de créditos de agentes está habilitado/)).toBeInTheDocument();
    expect(screen.queryByText('Server fallback')).not.toBeInTheDocument();
  });

  it('does not add a connection row when the server omits the disabled check', async () => {
    vi.mocked(fetchHealthComponents).mockResolvedValue({ ...section, status: 'Healthy', componentHealth: [] });
    renderWithProvider(<ComponentsPanel active />);
    await screen.findByText(/No component health/);
    expect(screen.queryByText('Power Platform billing connection')).not.toBeInTheDocument();
  });
});
