import { describe, it, expect, vi, beforeEach } from 'vitest';
import { screen, fireEvent, waitFor, within } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import WebActivityPage from './WebActivityPage';
import type {
  WebActivityAvailability,
  WebActivityOverview,
  WebActivityPages,
  WebActivitySearch,
  WebActivityWindow,
} from '../types/webActivity';

const mockAvailability = vi.fn();
const mockOverview = vi.fn();
const mockVisits = vi.fn();
const mockPages = vi.fn();
const mockJourneys = vi.fn();
const mockGeography = vi.fn();
const mockSearch = vi.fn();
const mockTechnology = vi.fn();
const mockExport = vi.fn();

vi.mock('../api/webActivityApi', () => ({
  fetchWebActivityAvailability: (...args: unknown[]) => mockAvailability(...args),
  fetchWebActivityOverview: (...args: unknown[]) => mockOverview(...args),
  fetchWebActivityVisits: (...args: unknown[]) => mockVisits(...args),
  fetchWebActivityPages: (...args: unknown[]) => mockPages(...args),
  fetchWebActivityJourneys: (...args: unknown[]) => mockJourneys(...args),
  fetchWebActivityGeography: (...args: unknown[]) => mockGeography(...args),
  fetchWebActivitySearch: (...args: unknown[]) => mockSearch(...args),
  fetchWebActivityTechnology: (...args: unknown[]) => mockTechnology(...args),
  downloadWebActivityExport: (...args: unknown[]) => mockExport(...args),
}));

const window28: WebActivityWindow = {
  days: 28,
  fromUtc: '2026-02-21T00:00:00Z',
  toUtc: '2026-03-20T00:00:00Z',
  workingDays: 20,
  top: 15,
  minimumViews: 5,
};

const availability = (over: Partial<WebActivityAvailability> = {}): WebActivityAvailability => ({
  webTrafficAvailable: true,
  userMetadataAvailable: true,
  appInsightsConfigured: true,
  hasAnyHits: true,
  lastHitUtc: '2026-03-20T09:00:00Z',
  searchAvailable: true,
  clickTrackingAvailable: true,
  available: true,
  reasons: [],
  ...over,
});

const overview = (over: Partial<WebActivityOverview> = {}): WebActivityOverview => ({
  window: window28,
  queries: [{ key: 'overview-kpis', sql: 'SELECT 1', error: null, elapsedMs: 12 }],
  kpis: {
    pageViews: 48000,
    uniquePageViews: 41000,
    visits: 12000,
    visitors: 1500,
    knownUsers: 2000,
    reachPct: 75,
    uniquePages: 900,
    sites: 14,
    pagesPerVisit: 4,
    bouncePct: 32,
    averageSecondsOnPage: 55,
    averageLoadSeconds: 1.1,
    newVisitors: 120,
    returningVisitors: 1380,
    mobileVisitPct: 11,
  },
  trend: [],
  visitorSegments: [{ key: 'Daily', label: 'Daily', count: 400, sharePct: 27 }],
  visitDepth: [{ key: '1 page', label: '1 page', count: 3840, sharePct: 32 }],
  heatmap: [{ day: 0, hour: 9, pageViews: 500, visits: 120 }],
  topSites: [{ name: 'Contoso intranet', count: 20000, sharePct: 42 }],
  judgements: [
    {
      key: 'bounce',
      tone: 'good',
      headline: 'Visits go several pages deep',
      detail: 'Nothing to do here.',
    },
  ],
  ...over,
});

const pages = (over: Partial<WebActivityPages> = {}): WebActivityPages => ({
  window: window28,
  queries: [{ key: 'pages-quiet', sql: 'SELECT 2', error: null, elapsedMs: 8 }],
  kpis: {
    pageViews: 48000,
    uniquePageViews: 41000,
    uniqueSharePct: 85,
    pagesPerVisit: 4,
    averageSecondsOnPage: 55,
    averageLoadSeconds: 1.1,
    uniquePages: 900,
    quietPages: 210,
    topDecilePagePct: 68,
  },
  topPages: [],
  slowestPages: [],
  quietPages: [
    {
      title: 'Καλημέρα κόσμε',
      url: 'https://contoso.sharepoint.com/sites/demo-03/SitePages/Καλημέρα-κόσμε/Home.aspx',
      site: 'Contoso Engineering',
      pageViews: 1,
      uniquePageViews: 1,
      visitors: 1,
      averageSecondsOnPage: 12,
      averageLoadSeconds: 0.9,
      entries: 1,
      exits: 1,
      bounces: 1,
      bouncePct: 100,
    },
  ],
  bySite: [],
  periodOverTime: [],
  ...over,
});

const search = (over: Partial<WebActivitySearch> = {}): WebActivitySearch => ({
  window: window28,
  queries: [{ key: 'search-kpis', sql: 'SELECT 3', error: null, elapsedMs: 9 }],
  kpis: {
    searches: 900,
    terms: 310,
    searchers: 420,
    sessionsWithSearch: 700,
    searchReliancePct: 5.8,
    searchesPerSearchingVisit: 1.3,
    strugglingVisits: 40,
    deadEndSearches: 210,
    deadEndPct: 23.3,
  },
  topTerms: [{ term: 'expenses', searches: 120, searchers: 90, deadEnds: 5, deadEndPct: 4.2 }],
  deadEndTerms: [],
  trend: [],
  byDay: [],
  byPeriodOfDay: [],
  bySite: [],
  ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  mockAvailability.mockResolvedValue(availability());
  mockOverview.mockResolvedValue(overview());
  mockPages.mockResolvedValue(pages());
  mockSearch.mockResolvedValue(search());
  mockExport.mockResolvedValue(undefined);
});

describe('WebActivityPage', () => {
  it('loads only the visible tab, then loads a tab the first time it is opened', async () => {
    renderWithProvider(<WebActivityPage />);

    await waitFor(() => expect(mockOverview).toHaveBeenCalledTimes(1));

    // Opening the page must cost ONE request, not seven. Every tab is a multi-query scan of the
    // largest table in the product.
    expect(mockVisits).not.toHaveBeenCalled();
    expect(mockPages).not.toHaveBeenCalled();
    expect(mockSearch).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('tab', { name: 'Page views' }));
    await waitFor(() => expect(mockPages).toHaveBeenCalledTimes(1));

    // Going back and forth must not refetch what is already in hand.
    fireEvent.click(screen.getByRole('tab', { name: 'Overview' }));
    fireEvent.click(screen.getByRole('tab', { name: 'Page views' }));
    await waitFor(() => expect(mockPages).toHaveBeenCalledTimes(1));
  });

  it('refetches the visible tab when the period changes', async () => {
    renderWithProvider(<WebActivityPage />);
    await waitFor(() => expect(mockOverview).toHaveBeenCalledTimes(1));
    expect(mockOverview).toHaveBeenLastCalledWith(28, expect.anything());

    fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '90' } });

    await waitFor(() => expect(mockOverview).toHaveBeenCalledTimes(2));
    expect(mockOverview).toHaveBeenLastCalledWith(90, expect.anything());
  });

  it('shows the headline figures and the page\u2019s own verdict on them', async () => {
    renderWithProvider(<WebActivityPage />);

    await screen.findByText('Visits go several pages deep');
    expect(screen.getByText('12,000')).toBeInTheDocument();
    expect(screen.getByText('32%')).toBeInTheDocument();
  });

  it('renders a non-Latin page title rather than dropping it', async () => {
    renderWithProvider(<WebActivityPage />);
    await waitFor(() => expect(mockOverview).toHaveBeenCalled());

    fireEvent.click(screen.getByRole('tab', { name: 'Page views' }));

    // SharePoint page names are genuinely non-Latin in plenty of tenants. A table that rendered
    // question marks here would be indistinguishable from a database encoding bug.
    await screen.findByText('Καλημέρα κόσμε');
  });

  it('explains a failed section instead of quietly showing nothing', async () => {
    mockOverview.mockResolvedValue(
      overview({
        queries: [
          { key: 'overview-heatmap', sql: 'SELECT 1', error: 'Execution Timeout Expired.', elapsedMs: 25000 },
        ],
      }),
    );

    renderWithProvider(<WebActivityPage />);

    // "No data for this period" and "the query timed out" must never look the same on a page that
    // exists to support a content decision.
    await screen.findByText(/could not be loaded: Execution Timeout Expired/);
  });

  it('surfaces a whole-tab failure and keeps the page usable', async () => {
    mockOverview.mockRejectedValue(new Error('Couldn\u2019t load the web activity overview (500).'));

    renderWithProvider(<WebActivityPage />);

    await screen.findByText(/Couldn\u2019t load the web activity overview/);
    expect(screen.getByRole('tab', { name: 'Visits' })).toBeInTheDocument();
  });

  it('tells an admin what to deploy when nothing has ever been collected', async () => {
    mockAvailability.mockResolvedValue(
      availability({
        hasAnyHits: false,
        lastHitUtc: null,
        available: false,
        reasons: ['Add the AI Tracker app to the site collections you want reported on.'],
      }),
    );

    renderWithProvider(<WebActivityPage />);

    await screen.findByText(/No SharePoint page views have been collected/);
    fireEvent.click(screen.getByRole('button', { name: /Show what is missing/ }));
    await screen.findByText(/Add the AI Tracker app/);
  });

  it('exports the quiet-page list for the period on screen', async () => {
    renderWithProvider(<WebActivityPage />);
    await waitFor(() => expect(mockOverview).toHaveBeenCalled());

    fireEvent.change(screen.getByLabelText('Reporting period'), { target: { value: '90' } });
    fireEvent.click(screen.getByRole('tab', { name: 'Page views' }));
    await waitFor(() => expect(mockPages).toHaveBeenCalled());

    const section = screen.getByText('Pages nobody reads').closest('div')?.parentElement;
    const exportButton = within(section as HTMLElement).getByRole('button', { name: 'Export' });
    fireEvent.click(exportButton);

    // The export must carry the period the reader is looking at, not the default.
    await waitFor(() => expect(mockExport).toHaveBeenCalledWith('quiet-pages', 90));
  });
});
