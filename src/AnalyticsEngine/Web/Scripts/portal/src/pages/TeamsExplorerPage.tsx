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
import AvailabilityBar from '../components/teamsExplorer/AvailabilityBar';
import OverviewPanel from '../components/teamsExplorer/OverviewPanel';
import AdoptionPanel from '../components/teamsExplorer/AdoptionPanel';
import MeetingsPanel from '../components/teamsExplorer/MeetingsPanel';
import CollaborationPanel from '../components/teamsExplorer/CollaborationPanel';
import ConversationsPanel from '../components/teamsExplorer/ConversationsPanel';
import PeoplePanel from '../components/teamsExplorer/PeoplePanel';
import {
  downloadTeamsExport,
  fetchTeamsAdoption,
  fetchTeamsAvailability,
  fetchTeamsCollaboration,
  fetchTeamsConversations,
  fetchTeamsMeetings,
  fetchTeamsOverview,
  fetchTeamsPeople,
} from '../api/teamsExplorerApi';
import type {
  TeamsAdoption,
  TeamsAvailability,
  TeamsCollaboration,
  TeamsConversations,
  TeamsExportSection,
  TeamsGrouping,
  TeamsMeetings,
  TeamsOverview,
  TeamsPeople,
} from '../types/teamsExplorer';
import { useT, type TranslationKey } from '../i18n';

/** The windows the API accepts. Anything else is snapped server-side, so these must agree with it. */
const WINDOWS: { days: number; labelKey: TranslationKey }[] = [
  { days: 7, labelKey: 'teamsExplorer.page.window.last7Days' },
  { days: 28, labelKey: 'teamsExplorer.page.window.last28Days' },
  { days: 90, labelKey: 'teamsExplorer.page.window.last90Days' },
  { days: 180, labelKey: 'teamsExplorer.page.window.last180Days' },
  { days: 365, labelKey: 'teamsExplorer.page.window.last365Days' },
];

type TabKey = 'overview' | 'adoption' | 'meetings' | 'collaboration' | 'conversations' | 'people';

const TABS: { key: TabKey; labelKey: TranslationKey }[] = [
  { key: 'overview', labelKey: 'teamsExplorer.page.tab.overview' },
  { key: 'adoption', labelKey: 'teamsExplorer.page.tab.adoption' },
  { key: 'meetings', labelKey: 'teamsExplorer.page.tab.meetings' },
  { key: 'collaboration', labelKey: 'teamsExplorer.page.tab.collaboration' },
  { key: 'conversations', labelKey: 'teamsExplorer.page.tab.conversations' },
  { key: 'people', labelKey: 'teamsExplorer.page.tab.people' },
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
    maxWidth: '860px',
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
 * Teams Explorer: the in-app replacement for the archived `Teams.pbit` Power BI report.
 *
 * Each tab loads independently and only when it is first opened, so opening the page costs one
 * request rather than six. Changing the period invalidates every tab's cached payload, which is
 * why the loaded data is keyed by tab and cleared on a window change - showing a 28-day chart under
 * a "Last 365 days" selector for the second it takes to refetch is worse than showing a spinner.
 */
export default function TeamsExplorerPage() {
  const styles = useStyles();
  const t = useT();

  const [days, setDays] = useState(28);
  const [groupBy, setGroupBy] = useState<TeamsGrouping>('department');
  const [selectedTab, setSelectedTab] = useState<TabKey>('overview');
  const [reloadToken, setReloadToken] = useState(0);
  const [exporting, setExporting] = useState(false);

  const [availability, setAvailability] = useState<TeamsAvailability | null>(null);
  const [availabilityError, setAvailabilityError] = useState<string | null>(null);

  const [overview, setOverview] = useState<TeamsOverview | null>(null);
  const [adoption, setAdoption] = useState<TeamsAdoption | null>(null);
  const [meetings, setMeetings] = useState<TeamsMeetings | null>(null);
  const [collaboration, setCollaboration] = useState<TeamsCollaboration | null>(null);
  const [conversations, setConversations] = useState<TeamsConversations | null>(null);
  const [people, setPeople] = useState<TeamsPeople | null>(null);

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Clear every tab's data when the window changes, so a stale payload can never be rendered
  // under a period selector that no longer matches it.
  useEffect(() => {
    setOverview(null);
    setAdoption(null);
    setMeetings(null);
    setCollaboration(null);
    setConversations(null);
    setPeople(null);
  }, [days, reloadToken]);

  // The grouping only affects the adoption breakdown, so only that tab is invalidated by it.
  useEffect(() => {
    setAdoption(null);
  }, [groupBy]);

  useEffect(() => {
    const controller = new AbortController();
    setAvailabilityError(null);

    fetchTeamsAvailability(controller.signal)
      .then(setAvailability)
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setAvailabilityError(e instanceof Error ? e.message : t('teamsExplorer.page.error.loadDataSources'));
      });

    return () => controller.abort();
  }, [reloadToken]);

  // Loads whichever tab is showing, once. Keeping the "already loaded?" check inside the effect
  // rather than in the click handler means a period change reloads the visible tab automatically.
  const loadedRef = useRef<Record<string, boolean>>({});

  useEffect(() => {
    const key = `${selectedTab}:${days}:${groupBy}:${reloadToken}`;
    if (loadedRef.current[key]) return;

    const alreadyHave =
      (selectedTab === 'overview' && overview) ||
      (selectedTab === 'adoption' && adoption) ||
      (selectedTab === 'meetings' && meetings) ||
      (selectedTab === 'collaboration' && collaboration) ||
      (selectedTab === 'conversations' && conversations) ||
      (selectedTab === 'people' && people);
    if (alreadyHave) return;

    loadedRef.current[key] = true;
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    const request = (() => {
      switch (selectedTab) {
        case 'adoption':
          return fetchTeamsAdoption(days, groupBy, controller.signal).then(setAdoption);
        case 'meetings':
          return fetchTeamsMeetings(days, controller.signal).then(setMeetings);
        case 'collaboration':
          return fetchTeamsCollaboration(days, controller.signal).then(setCollaboration);
        case 'conversations':
          return fetchTeamsConversations(days, controller.signal).then(setConversations);
        case 'people':
          return fetchTeamsPeople(days, controller.signal).then(setPeople);
        default:
          return fetchTeamsOverview(days, controller.signal).then(setOverview);
      }
    })();

    request
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        // A failed load must be retryable, so drop the "loaded" marker for this key.
        delete loadedRef.current[key];
        setError(e instanceof Error ? e.message : t('teamsExplorer.page.error.loadSection'));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [selectedTab, days, groupBy, reloadToken, overview, adoption, meetings, collaboration, conversations, people]);

  const onTabSelect: SelectTabEventHandler = (_: any, d: any) => setSelectedTab(d.value as TabKey);

  const runExport = useCallback(
    async (section: TeamsExportSection) => {
      setExporting(true);
      try {
        await downloadTeamsExport(section, days, groupBy);
      toast.success(t('teamsExplorer.page.toast.exportDownloaded'));
      } catch (e: unknown) {
      toast.error(e instanceof Error ? e.message : t('teamsExplorer.page.toast.exportFailed'));
      } finally {
        setExporting(false);
      }
    },
    [days, groupBy, t],
  );

  const hasData =
    (selectedTab === 'overview' && overview) ||
    (selectedTab === 'adoption' && adoption) ||
    (selectedTab === 'meetings' && meetings) ||
    (selectedTab === 'collaboration' && collaboration) ||
    (selectedTab === 'conversations' && conversations) ||
    (selectedTab === 'people' && people);

  return (
    <div>
      <div className={styles.header}>
        <div>
          <Title3>{t('teamsExplorer.page.title')}</Title3>
          <Body1 block className={styles.intro}>
            {t('teamsExplorer.page.intro')}
          </Body1>
        </div>

        <div className={styles.controls}>
          <Select
            value={String(days)}
            onChange={(_: any, d: any) => setDays(Number(d.value))}
            aria-label={t('teamsExplorer.page.reportingPeriodAria')}
          >
            {WINDOWS.map((w) => (
              <option key={w.days} value={w.days}>
                {t(w.labelKey)}
              </option>
            ))}
          </Select>
          <Button
            appearance="subtle"
            icon={<ArrowClockwise16Regular />}
            onClick={() => setReloadToken((n) => n + 1)}
            disabled={loading}
          >
            {t('teamsExplorer.page.refresh')}
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
            {t('teamsExplorer.page.noImports')}
          </MessageBarBody>
        </MessageBar>
      )}

      <TabList
        className={styles.tabs}
        selectedValue={selectedTab}
        onTabSelect={onTabSelect}
        aria-label={t('teamsExplorer.page.sectionsAria')}
      >
        {TABS.map((tab) => (
          <Tab key={tab.key} value={tab.key}>
            {t(tab.labelKey)}
          </Tab>
        ))}
      </TabList>

      <div className={styles.body}>
        {error && (
          <MessageBar intent="error" style={{ marginTop: '12px' }}>
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}

        {loading && !hasData && <Spinner label={t('teamsExplorer.page.loading')} />}

        {selectedTab === 'overview' && overview && <OverviewPanel data={overview} />}

        {selectedTab === 'adoption' && adoption && (
          <AdoptionPanel
            data={adoption}
            groupBy={groupBy}
            onGroupByChange={setGroupBy}
            demographicsAvailable={availability?.userMetadataAvailable ?? false}
          />
        )}

        {selectedTab === 'meetings' && meetings && (
          <MeetingsPanel data={meetings} callsAvailable={availability?.callsAvailable ?? true} />
        )}

        {selectedTab === 'collaboration' && collaboration && (
          <CollaborationPanel
            data={collaboration}
            analyticsAvailable={availability?.teamsAnalyticsAvailable ?? true}
            authorisedTeams={availability?.authorisedTeams ?? 0}
            onExportTeams={() => runExport('teams')}
            onExportChannels={() => runExport('channels')}
            exporting={exporting}
          />
        )}

        {selectedTab === 'conversations' && conversations && <ConversationsPanel data={conversations} />}

        {selectedTab === 'people' && people && (
          <PeoplePanel
            data={people}
            usageReportsAvailable={availability?.usageReportsAvailable ?? true}
            onExportChampions={() => runExport('people')}
            onExportDormant={() => runExport('dormant')}
            exporting={exporting}
          />
        )}
      </div>

      <Text size={200} className={styles.muted} style={{ marginTop: '20px', display: 'block' }}>
        {t('teamsExplorer.page.archivedTemplateNote')}
      </Text>
    </div>
  );
}
