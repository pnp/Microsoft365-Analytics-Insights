import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, fireEvent, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import ReportsPage from './ReportsPage';
import { fetchReportAreas, fetchReportArea } from '../api/reportsApi';
import { fetchAvailability } from '../api/licenceActivityApi';
import type { ReportAreaData, ReportAreas } from '../types/reports';
import type { LicenceActivityAvailability } from '../types/licenceActivity';

vi.mock('../api/reportsApi', () => ({ fetchReportAreas: vi.fn(), fetchReportArea: vi.fn() }));
vi.mock('../api/licenceActivityApi', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../api/licenceActivityApi')>();
  return {
    ...actual,
    fetchAvailability: vi.fn(),
    fetchOverview: vi.fn(),
    fetchUsers: vi.fn(),
    downloadExport: vi.fn(),
  };
});

const mockAreas = vi.mocked(fetchReportAreas);
const mockArea = vi.mocked(fetchReportArea);
const mockAvailability = vi.mocked(fetchAvailability);

const NO_AREAS: ReportAreas = {
  copilot: false,
  usage: false,
  spoAudit: false,
  webTraffic: false,
  calls: false,
  emails: false,
};

const areaData: ReportAreaData = {
  area: 'copilot',
  months: 3,
  fromWeek: '2026-01-05T00:00:00Z',
  charts: [],
  cognitiveConfigured: true,
};

function availability(over: Partial<LicenceActivityAvailability> = {}): LicenceActivityAvailability {
  return { available: false, canViewUsers: false, minimumDays: 7, maximumDays: 180, messages: [], ...over };
}

beforeEach(() => {
  vi.clearAllMocks();
  mockArea.mockResolvedValue(areaData);
  // Licence activity resolves "not available" so the lazy panel settles quickly without an overview call.
  mockAvailability.mockResolvedValue(availability());
});

describe('ReportsPage - Licence activity integration', () => {
  it('always shows the Licence activity tab, even when no report imports are enabled', async () => {
    mockAreas.mockResolvedValue(NO_AREAS);
    renderWithProvider(<ReportsPage />);

    // The tab exists despite every report-area flag being false...
    expect(await screen.findByRole('tab', { name: 'Licence activity' })).toBeInTheDocument();
    // ...it becomes the default, so its content loads...
    expect(await screen.findByText(/not available on this deployment/i)).toBeInTheDocument();
    // ...and the Reports month/period selector is hidden (it would look like it ignores the dates).
    expect(screen.queryByLabelText('Reporting period')).not.toBeInTheDocument();
  });

  it('hides the Reports period selector when the Licence activity tab is chosen', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, copilot: true });
    renderWithProvider(<ReportsPage />);

    // Default lands on the report area, so the period selector is shown.
    expect(await screen.findByRole('tab', { name: 'Copilot' })).toBeInTheDocument();
    expect(screen.getByLabelText('Reporting period')).toBeInTheDocument();

    // Switching to Licence activity hides it and loads the licence panel.
    fireEvent.click(screen.getByRole('tab', { name: 'Licence activity' }));
    expect(await screen.findByText(/not available on this deployment/i)).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByLabelText('Reporting period')).not.toBeInTheDocument());
  });

  it('keeps a report-areas load failure visible even while the Licence activity tab is showing', async () => {
    // The areas fetch fails; with no enabled areas the default lands on the always-present Licence tab.
    mockAreas.mockRejectedValue(new Error('Boom: report areas failed to load (500).'));
    renderWithProvider(<ReportsPage />);

    // The error is rendered OUTSIDE the tab content, so it shows even though the Licence tab wins the
    // content switch...
    expect(await screen.findByText(/report areas failed to load/i)).toBeInTheDocument();
    // ...and the Licence activity tab still works (its panel resolves)...
    expect(await screen.findByText(/not available on this deployment/i)).toBeInTheDocument();
    // ...with the error still visible alongside it, on the selected Licence tab.
    expect(screen.getByText(/report areas failed to load/i)).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Licence activity' })).toHaveAttribute('aria-selected', 'true');
  });
});
