import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Title3,
  Body1,
  Text,
  Select,
  Button,
  Tab,
  TabList,
  MessageBar,
  MessageBarBody,
  makeStyles,
  tokens,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular } from '@fluentui/react-icons';
import Spinner from '../components/Spinner';
import toast from '../components/toast';
import AvailabilityBar from '../components/webActivity/AvailabilityBar';
import OverviewPanel from '../components/webActivity/OverviewPanel';
import VisitsPanel from '../components/webActivity/VisitsPanel';
import PagesPanel from '../components/webActivity/PagesPanel';
import JourneysPanel from '../components/webActivity/JourneysPanel';
import GeographyPanel from '../components/webActivity/GeographyPanel';
import SearchPanel from '../components/webActivity/SearchPanel';
import TechnologyPanel from '../components/webActivity/TechnologyPanel';
import {
  downloadWebActivityExport,
  fetchWebActivityAvailability,
  fetchWebActivityGeography,
  fetchWebActivityJourneys,
  fetchWebActivityOverview,
  fetchWebActivityPages,
  fetchWebActivitySearch,
  fetchWebActivityTechnology,
  fetchWebActivityVisits,
} from '../api/webActivityApi';
import type {
  WebActivityAvailability,
  WebActivityExportSection,
  WebActivityGeography,
  WebActivityJourneys,
  WebActivityOverview,
  WebActivityPages,
  WebActivitySearch,
  WebActivityTechnology,
  WebActivityVisits,
} from '../types/webActivity';

/** The windows the API accepts. Anything else is snapped server-side, so these must agree with it. */
const WINDOWS = [
  { days: 7, label: 'Last 7 days' },
  { days: 28, label: 'Last 28 days' },
  { days: 90, label: 'Last 90 days' },
  { days: 180, label: 'Last 180 days' },
  { days: 365, label: 'Last 365 days' },
];

type TabKey = 'overview' | 'visits' | 'pages' | 'journeys' | 'geography' | 'search' | 'technology';

const TABS: { key: TabKey; label: string }[] = [
  { key: 'overview', label: 'Overview' },
  { key: 'visits', label: 'Visits' },
  { key: 'pages', label: 'Page views' },
  { key: 'journeys', label: 'Journeys' },
  { key: 'geography', label: 'Geographics' },
  { key: 'search', label: 'Web searches' },
  { key: 'technology', label: 'Technology' },
];

const useStyles = makeStyles({
  header: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  controls: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
  },
  intro: {
    marginTop: '8px',
    maxWidth: '880px',
  },
  tabs: {
    marginTop: '16px',
  },
  body: {
    marginTop: '8px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
});

/**
 * SharePoint web activity: the in-app replacement for the Power BI web-traffic report.
 *
 * Each tab loads independently and only when it is first opened, so opening the page costs one
 * request rather than seven. Changing the period invalidates every tab's cached payload, which is
 * why the loaded data is keyed by tab and cleared on a window change - showing a 28-day chart under
 * a "Last 365 days" selector for the second it takes to refetch is worse than showing a spinner.
 */
export default function WebActivityPage() {
  const styles = useStyles();

  const [days, setDays] = useState(28);
  const [selectedTab, setSelectedTab] = useState<TabKey>('overview');
  const [reloadToken, setReloadToken] = useState(0);
  const [exporting, setExporting] = useState(false);

  const [availability, setAvailability] = useState<WebActivityAvailability | null>(null);
  const [availabilityError, setAvailabilityError] = useState<string | null>(null);

  const [overview, setOverview] = useState<WebActivityOverview | null>(null);
  const [visits, setVisits] = useState<WebActivityVisits | null>(null);
  const [pages, setPages] = useState<WebActivityPages | null>(null);
  const [journeys, setJourneys] = useState<WebActivityJourneys | null>(null);
  const [geography, setGeography] = useState<WebActivityGeography | null>(null);
  const [search, setSearch] = useState<WebActivitySearch | null>(null);
  const [technology, setTechnology] = useState<WebActivityTechnology | null>(null);

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Clear every tab's data when the window changes, so a stale payload can never be rendered
  // under a period selector that no longer matches it.
  useEffect(() => {
    setOverview(null);
    setVisits(null);
    setPages(null);
    setJourneys(null);
    setGeography(null);
    setSearch(null);
    setTechnology(null);
  }, [days, reloadToken]);

  useEffect(() => {
    const controller = new AbortController();
    setAvailabilityError(null);

    fetchWebActivityAvailability(controller.signal)
      .then(setAvailability)
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setAvailabilityError(
          e instanceof Error ? e.message : 'Failed to load the web traffic data sources.',
        );
      });

    return () => controller.abort();
  }, [reloadToken]);

  // Loads whichever tab is showing, once. Keeping the "already loaded?" check inside the effect
  // rather than in the click handler means a period change reloads the visible tab automatically.
  const loadedRef = useRef<Record<string, boolean>>({});

  useEffect(() => {
    const key = `${selectedTab}:${days}:${reloadToken}`;
    if (loadedRef.current[key]) return;

    const alreadyHave =
      (selectedTab === 'overview' && overview) ||
      (selectedTab === 'visits' && visits) ||
      (selectedTab === 'pages' && pages) ||
      (selectedTab === 'journeys' && journeys) ||
      (selectedTab === 'geography' && geography) ||
      (selectedTab === 'search' && search) ||
      (selectedTab === 'technology' && technology);
    if (alreadyHave) return;

    loadedRef.current[key] = true;
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    const request = (() => {
      switch (selectedTab) {
        case 'visits':
          return fetchWebActivityVisits(days, controller.signal).then(setVisits);
        case 'pages':
          return fetchWebActivityPages(days, controller.signal).then(setPages);
        case 'journeys':
          return fetchWebActivityJourneys(days, controller.signal).then(setJourneys);
        case 'geography':
          return fetchWebActivityGeography(days, controller.signal).then(setGeography);
        case 'search':
          return fetchWebActivitySearch(days, controller.signal).then(setSearch);
        case 'technology':
          return fetchWebActivityTechnology(days, controller.signal).then(setTechnology);
        default:
          return fetchWebActivityOverview(days, controller.signal).then(setOverview);
      }
    })();

    request
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        // A failed load must be retryable, so drop the "loaded" marker for this key.
        delete loadedRef.current[key];
        setError(e instanceof Error ? e.message : 'Failed to load this section.');
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [selectedTab, days, reloadToken, overview, visits, pages, journeys, geography, search, technology]);

  const onTabSelect: SelectTabEventHandler = (_: any, d: any) => setSelectedTab(d.value as TabKey);

  const runExport = useCallback(
    async (section: WebActivityExportSection) => {
      setExporting(true);
      try {
        await downloadWebActivityExport(section, days);
        toast.success('Export downloaded');
      } catch (e: unknown) {
        toast.error(e instanceof Error ? e.message : 'The export failed.');
      } finally {
        setExporting(false);
      }
    },
    [days],
  );

  const hasData =
    (selectedTab === 'overview' && overview) ||
    (selectedTab === 'visits' && visits) ||
    (selectedTab === 'pages' && pages) ||
    (selectedTab === 'journeys' && journeys) ||
    (selectedTab === 'geography' && geography) ||
    (selectedTab === 'search' && search) ||
    (selectedTab === 'technology' && technology);

  return (
    <div>
      <div className={styles.header}>
        <div>
          <Title3>SharePoint web activity</Title3>
          <Body1 block className={styles.intro}>
            What people actually do on the intranet: who visits and how often, which pages earn their
            keep, where visitors arrive and give up, what they search for when navigation fails, and
            how fast the whole thing feels. Every figure carries its definition and the SQL behind it.
          </Body1>
        </div>

        <div className={styles.controls}>
          <Select
            value={String(days)}
            onChange={(_: any, d: any) => setDays(Number(d.value))}
            aria-label="Reporting period"
          >
            {WINDOWS.map((w) => (
              <option key={w.days} value={w.days}>
                {w.label}
              </option>
            ))}
          </Select>
          <Button
            appearance="subtle"
            icon={<ArrowClockwise16Regular />}
            onClick={() => setReloadToken((n) => n + 1)}
            disabled={loading}
          >
            Refresh
          </Button>
        </div>
      </div>

      {availabilityError && (
        <MessageBar intent="error" style={{ marginTop: '12px' }}>
          <MessageBarBody>{availabilityError}</MessageBarBody>
        </MessageBar>
      )}

      {availability && <AvailabilityBar availability={availability} />}

      {availability && !availability.available && (
        <MessageBar intent="warning" style={{ marginTop: '12px' }}>
          <MessageBarBody>
            No SharePoint page views have been collected, so this page has nothing to report. The
            details above say exactly what to enable or deploy.
          </MessageBarBody>
        </MessageBar>
      )}

      <TabList
        className={styles.tabs}
        selectedValue={selectedTab}
        onTabSelect={onTabSelect}
        aria-label="Web activity sections"
      >
        {TABS.map((tab) => (
          <Tab key={tab.key} value={tab.key}>
            {tab.label}
          </Tab>
        ))}
      </TabList>

      <div className={styles.body}>
        {error && (
          <MessageBar intent="error" style={{ marginTop: '12px' }}>
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}

        {loading && !hasData && <Spinner label="Loading web activity..." />}

        {selectedTab === 'overview' && overview && <OverviewPanel data={overview} />}

        {selectedTab === 'visits' && visits && <VisitsPanel data={visits} />}

        {selectedTab === 'pages' && pages && (
          <PagesPanel
            data={pages}
            onExportPages={() => runExport('pages')}
            onExportQuiet={() => runExport('quiet-pages')}
            onExportSlow={() => runExport('slow-pages')}
            exporting={exporting}
          />
        )}

        {selectedTab === 'journeys' && journeys && (
          <JourneysPanel
            data={journeys}
            clickTrackingAvailable={availability?.clickTrackingAvailable ?? true}
            onExportEntry={() => runExport('entry-pages')}
            onExportExit={() => runExport('exit-pages')}
            onExportTransitions={() => runExport('transitions')}
            exporting={exporting}
          />
        )}

        {selectedTab === 'geography' && geography && <GeographyPanel data={geography} />}

        {selectedTab === 'search' && search && (
          <SearchPanel
            data={search}
            searchAvailable={availability?.searchAvailable ?? true}
            onExportTerms={() => runExport('search-terms')}
            exporting={exporting}
          />
        )}

        {selectedTab === 'technology' && technology && (
          <TechnologyPanel
            data={technology}
            onExportDetail={() => runExport('technology')}
            exporting={exporting}
          />
        )}
      </div>

      <Text size={200} className={styles.muted} style={{ marginTop: '20px', display: 'block' }}>
        This page replaces the Power BI web-traffic report. It reads the page-hit tables directly
        rather than the legacy reporting views, so it reflects what the SharePoint tracker actually
        collected. Hour-of-day and day-of-week figures are UTC.
      </Text>
    </div>
  );
}
