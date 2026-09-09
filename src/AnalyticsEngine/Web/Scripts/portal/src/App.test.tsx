import { describe, it, expect, beforeEach, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { renderWithProvider } from './test/renderWithProvider';
import App from './App';
import { routesForArea } from './navigation';
import { fetchAvailability } from './api/licenceActivityApi';
import { fetchReportAreas } from './api/reportsApi';

vi.mock('./api/licenceActivityApi', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api/licenceActivityApi')>(),
  fetchAvailability: vi.fn(),
}));
vi.mock('./api/reportsApi', () => ({ fetchReportAreas: vi.fn(), fetchReportArea: vi.fn() }));

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchAvailability).mockResolvedValue({
    available: false, minimumDays: 7, maximumDays: 180, messages: [],
  });
  vi.mocked(fetchReportAreas).mockResolvedValue({
    copilot: false, usage: false, spoAudit: false, webTraffic: false, calls: false, emails: false,
  });
});

describe('Licence activity navigation', () => {
  it('places Licence activity directly below Copilot Adoption in Insights', () => {
    const paths = routesForArea('insights').map((r) => r.path);
    expect(paths[paths.indexOf('/insights/copilot-adoption') + 1]).toBe('/insights/licence-activity');
    expect(routesForArea('admin').some((r) => r.path === '/insights/licence-activity')).toBe(false);
  });

  it('opens the standalone route directly without loading Reports or requiring its imports', async () => {
    renderWithProvider(<MemoryRouter initialEntries={['/insights/licence-activity']}><App /></MemoryRouter>);

    expect(await screen.findByText(/Licence activity reporting is not available on this deployment/)).toBeVisible();
    expect(screen.getByText('Preview')).toBeVisible();
    expect(screen.queryByLabelText('Reporting period')).not.toBeInTheDocument();
    expect(fetchAvailability).toHaveBeenCalled();
    expect(fetchReportAreas).not.toHaveBeenCalled();
    expect(within(screen.getByLabelText('Insights navigation')).getByText('Licence activity')).toBeVisible();
  });

  it('navigates from Reports to Licence activity using the sidebar', async () => {
    const user = userEvent.setup();
    renderWithProvider(<MemoryRouter initialEntries={['/insights/reports']}><App /></MemoryRouter>);

    expect(await screen.findByText(/No built-in report charts are available yet/)).toBeVisible();
    expect(fetchAvailability).not.toHaveBeenCalled();
    await user.click(within(screen.getByLabelText('Insights navigation')).getByText('Licence activity'));
    expect(await screen.findByText(/Licence activity reporting is not available on this deployment/)).toBeVisible();
    expect(screen.queryByText(/No built-in report charts are available yet/)).not.toBeInTheDocument();
  });
});
