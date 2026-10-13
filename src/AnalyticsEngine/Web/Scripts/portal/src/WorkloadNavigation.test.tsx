import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HashRouter } from 'react-router-dom';
import App from './App';
import { renderWithProvider } from './test/renderWithProvider';
import { fetchReportArea, fetchReportAreas } from './api/reportsApi';
import { loadCatalog, translateStatic } from './i18n';
import type { ReportAreas } from './types/reports';

vi.mock('./api/reportsApi', () => ({ fetchReportAreas: vi.fn(), fetchReportArea: vi.fn() }));
vi.mock('./api/licenceActivityApi', () => ({
  fetchAvailability: vi.fn().mockResolvedValue({ available: false, minimumDays: 7, maximumDays: 180, messages: [] }),
}));

const areas: ReportAreas = {
  copilot: true, copilotInteractionHistory: false, usage: true, officeApps: true,
  spoAudit: true, webTraffic: true, calls: true, emails: true,
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchReportAreas).mockResolvedValue(areas);
  vi.mocked(fetchReportArea).mockImplementation(async (area, months) => ({
    area, months, fromWeek: '2026-01-05T00:00:00Z', charts: [], cognitiveConfigured: false,
  }));
});

function renderAt(path: string, language: 'en' | 'es' = 'en') {
  window.history.replaceState(null, '', `#${path}`);
  return renderWithProvider(<HashRouter><App /></HashRouter>, { language });
}

describe('workload sidebar and report routes', () => {
  it.each(['en', 'es'] as const)('opens the active workload on a shared report link in %s', async language => {
    await loadCatalog(language);
    renderAt('/insights/reports/office-apps?months=6', language);

    const nav = await screen.findByLabelText(translateStatic(language, 'app.nav.ariaLabel', { area: translateStatic(language, 'app.area.insights') }));
    expect(within(nav).getByRole('button', { name: translateStatic(language, 'app.workload.apps') })).toHaveAttribute('aria-expanded', 'true');
    expect(within(nav).getByRole('button', { name: translateStatic(language, 'app.workload.copilot') })).toHaveAttribute('aria-expanded', 'false');
    await waitFor(() => expect(fetchReportArea).toHaveBeenCalledWith('office-apps', 6, undefined));
    expect(fetchReportAreas).toHaveBeenCalledTimes(1);
    expect(screen.getByLabelText(translateStatic(language, 'reports.period.ariaLabel'))).toHaveValue('6');
  });

  it('expands groups without requesting charts and never prefetches sibling workloads', async () => {
    const user = userEvent.setup();
    renderAt('/insights/reports');
    await screen.findAllByText('Import enabled');
    const nav = screen.getByLabelText('Insights navigation');
    await user.click(within(nav).getByRole('button', { name: 'Copilot' }));
    await user.click(within(nav).getByRole('button', { name: 'Apps & licences' }));
    expect(fetchReportArea).not.toHaveBeenCalled();
    expect(within(nav).getByText('Usage')).toBeVisible();
    expect(within(nav).getByText('Office apps')).toBeVisible();
  });

  it('preserves the period between reports and restores both page and active group with browser Back', async () => {
    const user = userEvent.setup();
    renderAt('/insights/reports/copilot?months=6');
    await waitFor(() => expect(fetchReportArea).toHaveBeenCalledWith('copilot', 6, undefined));
    const nav = screen.getByLabelText('Insights navigation');
    await user.click(within(nav).getByRole('button', { name: 'Apps & licences' }));
    await user.click(within(nav).getByText('Office apps'));
    await waitFor(() => expect(window.location.hash).toBe('#/insights/reports/office-apps?months=6'));
    await waitFor(() => expect(fetchReportArea).toHaveBeenCalledWith('office-apps', 6, undefined));

    await act(async () => {
      const popped = new Promise<void>(resolve => window.addEventListener('popstate', () => resolve(), { once: true }));
      window.history.back();
      await popped;
    });
    await waitFor(() => expect(window.location.hash).toBe('#/insights/reports/copilot?months=6'));
    expect(within(nav).getByRole('button', { name: 'Copilot' })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByLabelText('Reporting period')).toHaveValue('6');
  });

  it('replaces period changes in history and keeps the new period when returning through the catalog', async () => {
    const user = userEvent.setup();
    renderAt('/insights/reports/copilot?months=3');
    await waitFor(() => expect(fetchReportArea).toHaveBeenCalledWith('copilot', 3, undefined));
    const replace = vi.spyOn(window.history, 'replaceState');
    fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '1' } });
    await waitFor(() => expect(window.location.hash).toBe('#/insights/reports/copilot?months=1'));
    expect(replace).toHaveBeenCalled();
    replace.mockRestore();
    await user.click(within(screen.getByLabelText('Insights navigation')).getByText('Reports'));
    expect(await screen.findAllByText('Import enabled')).not.toHaveLength(0);
    expect(screen.getAllByRole('link', { name: /^Open .* report$/ }).every(link => link.getAttribute('href')?.endsWith('?months=1'))).toBe(true);
  });

  it('hides disabled report leaves but retains the active direct link with its prerequisite explanation', async () => {
    vi.mocked(fetchReportAreas).mockResolvedValue({ ...areas, officeApps: false });
    renderAt('/insights/reports/office-apps?months=3');
    expect(await screen.findByText(/This report needs the Microsoft 365 usage data import enabled/)).toBeVisible();
    const nav = screen.getByLabelText('Insights navigation');
    expect(within(nav).getByText('Office apps')).toBeVisible();
    expect(fetchReportArea).not.toHaveBeenCalled();
  });

  it('supports keyboard expansion and shows metadata failures with a retry', async () => {
    const user = userEvent.setup();
    vi.mocked(fetchReportAreas).mockRejectedValueOnce(new Error('Synthetic availability failure.'));
    renderAt('/insights/reports');
    expect(await screen.findByText('Synthetic availability failure.')).toBeVisible();
    const group = within(screen.getByLabelText('Insights navigation')).getByRole('button', { name: 'Apps & licences' });
    group.focus();
    await user.keyboard('{Enter}');
    expect(group).toHaveAttribute('aria-expanded', 'true');
    vi.mocked(fetchReportAreas).mockResolvedValue(areas);
    await user.click(screen.getByRole('button', { name: 'Refresh' }));
    await screen.findAllByText('Import enabled');
    expect(fetchReportArea).not.toHaveBeenCalled();
  });
});
