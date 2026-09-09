import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, fireEvent, waitFor } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import ReportsPage from './ReportsPage';
import { fetchReportAreas, fetchReportArea } from '../api/reportsApi';
import { fetchAvailability } from '../api/licenceActivityApi';
import type { ReportAreaData, ReportAreas } from '../types/reports';

vi.mock('../api/reportsApi', () => ({ fetchReportAreas: vi.fn(), fetchReportArea: vi.fn() }));
vi.mock('../api/licenceActivityApi', () => ({ fetchAvailability: vi.fn() }));

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

beforeEach(() => {
  vi.clearAllMocks();
  mockArea.mockResolvedValue(areaData);
});

describe('ReportsPage', () => {
  it('keeps the report loading state without mounting Licence activity while areas are pending', () => {
    mockAreas.mockReturnValue(new Promise(() => {}));
    renderWithProvider(<ReportsPage />);

    expect(screen.getByText('Loading reports...')).toBeVisible();
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.queryByText(/No built-in report charts are available yet/)).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Licence activity' })).toHaveAttribute('href', '#/insights/licence-activity');
    expect(mockAvailability).not.toHaveBeenCalled();
    expect(mockArea).not.toHaveBeenCalled();
  });

  it('shows the empty report state and a link to standalone Licence activity when imports are disabled', async () => {
    mockAreas.mockResolvedValue(NO_AREAS);
    renderWithProvider(<ReportsPage />);

    expect(await screen.findByText(/No built-in report charts are available yet/)).toBeInTheDocument();
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Reporting period')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Licence activity' })).toHaveAttribute('href', '#/insights/licence-activity');
    expect(mockAvailability).not.toHaveBeenCalled();
    expect(mockArea).not.toHaveBeenCalled();
  });

  it('keeps report tabs and their period control without embedding Licence activity', async () => {
    mockAreas.mockResolvedValue({ ...NO_AREAS, copilot: true });
    renderWithProvider(<ReportsPage />);

    expect(await screen.findByRole('tab', { name: 'Copilot' })).toBeInTheDocument();
    expect(screen.getByLabelText('Reporting period')).toBeInTheDocument();
    expect(screen.queryByRole('tab', { name: 'Licence activity' })).not.toBeInTheDocument();
    await waitFor(() => expect(mockArea).toHaveBeenCalledWith('copilot', 3, undefined));

    fireEvent.click(screen.getByRole('tab', { name: 'Copilot agents' }));
    await waitFor(() => expect(mockArea).toHaveBeenCalledWith('copilot-agents', 3, { topAgents: 8, agentName: '' }));
    fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '6' } });
    await waitFor(() => expect(mockArea).toHaveBeenCalledWith('copilot-agents', 6, { topAgents: 8, agentName: '' }));
    expect(mockAvailability).not.toHaveBeenCalled();
  });

  it('surfaces report-areas errors without falling back to a Licence activity panel', async () => {
    mockAreas.mockRejectedValue(new Error('Boom: report areas failed to load (500).'));
    renderWithProvider(<ReportsPage />);

    expect(await screen.findByText(/report areas failed to load/i)).toBeInTheDocument();
    expect(screen.queryByRole('tablist')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Reporting period')).not.toBeInTheDocument();
    expect(screen.queryByText(/No built-in report charts are available yet/)).not.toBeInTheDocument();
    expect(mockAvailability).not.toHaveBeenCalled();
    expect(mockArea).not.toHaveBeenCalled();
  });
});
