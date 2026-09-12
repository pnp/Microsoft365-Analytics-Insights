import { describe, it, expect, beforeEach, vi } from 'vitest';
import { useEffect } from 'react';
import { screen, within, fireEvent, waitFor, configure, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { HashRouter, useLocation } from 'react-router-dom';
import { renderWithProvider } from './test/renderWithProvider';
import App from './App';
import { routesForArea } from './navigation';
import { fetchAvailability } from './api/licenceActivityApi';
import { fetchReportAreas } from './api/reportsApi';

// Every test here mounts the whole app shell (Fluent header, TabList and NavDrawer) and then waits
// for a lazily-imported page chunk. In jsdom that is slow, and under a full parallel run it has
// been measured over 6s, so the async matchers get well beyond LicenceActivityPage.test.tsx's
// headroom - without it this file fails intermittently on timing alone.
configure({ asyncUtilTimeout: 15000 });
vi.setConfig({ testTimeout: 30000 });

vi.mock('./api/licenceActivityApi', async (importOriginal) => ({
  ...await importOriginal<typeof import('./api/licenceActivityApi')>(),
  fetchAvailability: vi.fn(),
}));
vi.mock('./api/reportsApi', () => ({ fetchReportAreas: vi.fn(), fetchReportArea: vi.fn() }));

/** Records each distinct location react-router lands on, including duplicate pushes. */
function LocationLog({ log }: { log: string[] }) {
  const location = useLocation();
  useEffect(() => {
    log.push(location.pathname);
  }, [location, log]);
  return null;
}

/**
 * Renders the app at `path` under HashRouter - the router main.tsx uses in production.
 *
 * The shell decides whether to navigate by reading the live URL, so the router has to be a real
 * one over jsdom's history: under MemoryRouter there is no URL to read, and because jsdom's
 * history is shared by every test in the file, a stale hash left by an earlier test would be read
 * as the current route. Replacing (not pushing) the entry also means a Back press in one test
 * cannot walk into another test's history.
 */
const renderAt = (path: string) => {
  window.history.replaceState(null, '', `#${path}`);
  const log: string[] = [];
  renderWithProvider(
    <HashRouter>
      <LocationLog log={log} />
      <App />
    </HashRouter>,
  );
  return log;
};

/** The browser Back button - the invariant the user actually feels. */
const pressBack = () => window.history.back();

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
    renderAt('/insights/licence-activity');

    expect(await screen.findByText(/Licence activity reporting is not available on this deployment/)).toBeVisible();
    expect(screen.getByText('Preview')).toBeVisible();
    expect(screen.queryByLabelText('Reporting period')).not.toBeInTheDocument();
    expect(fetchAvailability).toHaveBeenCalled();
    expect(fetchReportAreas).not.toHaveBeenCalled();
    expect(within(screen.getByLabelText('Insights navigation')).getByText('Licence activity')).toBeVisible();
  });

  it('navigates from Reports to Licence activity using the sidebar', async () => {
    const user = userEvent.setup();
    renderAt('/insights/reports');

    expect(await screen.findByText(/No built-in report charts are available yet/)).toBeVisible();
    expect(fetchAvailability).not.toHaveBeenCalled();
    await user.click(within(screen.getByLabelText('Insights navigation')).getByText('Licence activity'));
    expect(await screen.findByText(/Licence activity reporting is not available on this deployment/)).toBeVisible();
    expect(screen.queryByText(/No built-in report charts are available yet/)).not.toBeInTheDocument();
  });
});

/**
 * Every push adds a browser history entry, so re-selecting what is already selected leaves a
 * duplicate the user has to Back through - the first press looks like a dead Back button.
 * Verified against the deployed site: clicking one nav item three times needed three Backs to
 * return to the previous page.
 *
 * These use HashRouter (what main.tsx renders) rather than MemoryRouter, because the shell decides
 * whether to navigate by reading the live URL - jsdom's history is what makes that reachable, and
 * MemoryRouter would leave the interesting logic untested.
 */
describe('History entries', () => {
  it('does not push a history entry when the selected nav item is clicked again', async () => {
    const user = userEvent.setup();
    const log = renderAt('/insights/reports');
    expect(await screen.findByText(/No built-in report charts are available yet/)).toBeVisible();
    const initial = log.length;

    const nav = screen.getByLabelText('Insights navigation');
    await user.click(within(nav).getByText('Reports'));
    await user.click(within(nav).getByText('Reports'));

    expect(log.length).toBe(initial);
    expect(await screen.findByText(/No built-in report charts are available yet/)).toBeVisible();
  });

  it('still navigates when a different nav item is clicked', async () => {
    const user = userEvent.setup();
    const log = renderAt('/insights/reports');
    expect(await screen.findByText(/No built-in report charts are available yet/)).toBeVisible();

    await user.click(within(screen.getByLabelText('Insights navigation')).getByText('Licence activity'));

    expect(await screen.findByText(/Licence activity reporting is not available on this deployment/)).toBeVisible();
    expect(log[log.length - 1]).toBe('/insights/licence-activity');
  });

  it('takes one Back press to undo a nav item that was double-clicked', async () => {
    const log = renderAt('/insights/overview');
    await screen.findByLabelText('Insights navigation');
    const nav = screen.getByLabelText('Insights navigation');

    // Both clicks must land before the first navigation commits. fireEvent flushes between calls,
    // which would hide the race, so dispatch both inside one act. A duplicate push is invisible to
    // LocationLog because React coalesces the two updates - pressing Back is what exposes it.
    const reports = within(nav).getByText('Reports');
    await act(async () => {
      reports.click();
      reports.click();
    });
    await waitFor(() => expect(log[log.length - 1]).toBe('/insights/reports'));

    pressBack();

    await waitFor(() => expect(log[log.length - 1]).toBe('/insights/overview'));
  });

  it('still navigates, and keeps the page behind it, when an interrupted navigation is retried', async () => {
    const log = renderAt('/insights/overview');
    await screen.findByLabelText('Insights navigation');
    const reports = within(screen.getByLabelText('Insights navigation')).getByText('Reports');

    // Stage the real interleaving: the retry has to happen after Back has changed the URL but
    // before React commits it. Keeping one act open and awaiting popstate does that - jsdom queues
    // history.back() to a later task, so clicking straight after it would still see the pre-Back
    // URL and prove nothing.
    await act(async () => {
      reports.click();
      const popped = new Promise<void>((resolve) =>
        window.addEventListener('popstate', () => resolve(), { once: true }),
      );
      pressBack();
      await popped;
      reports.click();
    });

    // The retry must navigate...
    await waitFor(() => expect(log[log.length - 1]).toBe('/insights/reports'));
    // ...by pushing, not replacing: the page it was launched from must still be behind it.
    pressBack();
    await waitFor(() => expect(log[log.length - 1]).toBe('/insights/overview'));
  });

  it('honours a click on another item while a navigation is still in flight', async () => {
    const log = renderAt('/insights/overview');
    await screen.findByLabelText('Insights navigation');
    const nav = screen.getByLabelText('Insights navigation');

    // The second click targets the page we are visually still on, so comparing against the
    // committed location would discard it and leave the user on Reports.
    await act(async () => {
      within(nav).getByText('Reports').click();
      within(nav).getByText('Overview').click();
    });

    await waitFor(() => expect(log[log.length - 1]).toBe('/insights/overview'));
  });

  it('does not bounce you to the area home page when the area tab you are already in is clicked', async () => {
    // Starting one page deep means a bounce is visible in the history stack rather than only in
    // timing: with the bounce, Back returns to Licence activity; without it, Back reaches Reports.
    const user = userEvent.setup();
    const log = renderAt('/insights/reports');
    expect(await screen.findByText(/No built-in report charts are available yet/)).toBeVisible();
    await user.click(within(screen.getByLabelText('Insights navigation')).getByText('Licence activity'));
    expect(await screen.findByText(/Licence activity reporting is not available on this deployment/)).toBeVisible();

    // Fluent's TabList only responds to fireEvent here, as in the other page tests.
    fireEvent.click(screen.getByRole('tab', { name: 'Insights' }));
    pressBack();

    await waitFor(() => expect(log[log.length - 1]).toBe('/insights/reports'));
  });

  it('switches area and lands on its home page when the other area tab is clicked', async () => {
    const log = renderAt('/insights/licence-activity');
    expect(await screen.findByText(/Licence activity reporting is not available on this deployment/)).toBeVisible();

    fireEvent.click(screen.getByRole('tab', { name: 'Administration' }));

    await waitFor(() => expect(log[log.length - 1]).toBe('/admin/health'));
    expect(await screen.findByLabelText('Administration navigation')).toBeVisible();
  });
});
