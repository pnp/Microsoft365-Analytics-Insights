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

/** The windows the API accepts. Anything else is snapped server-side, so these must agree with it. */
const WINDOWS = [
  { days: 7, label: 'Last 7 days' },
  { days: 28, label: 'Last 28 days' },
  { days: 90, label: 'Last 90 days' },
  { days: 180, label: 'Last 180 days' },
  { days: 365, label: 'Last 365 days' },
];

type TabKey = 'overview' | 'adoption' | 'meetings' | 'collaboration' | 'conversations' | 'people';

const TABS: { key: TabKey; label: string }[] = [
  { key: 'overview', label: 'Overview' },
  { key: 'adoption', label: 'Adoption & reach' },
  { key: 'meetings', label: 'Meetings & calls' },
  { key: 'collaboration', label: 'Teams & channels' },
  { key: 'conversations', label: 'Conversation insights' },
  { key: 'people', label: 'People' },
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
        setAvailabilityError(e instanceof Error ? e.message : 'Failed to load the Teams data sources.');
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
        setError(e instanceof Error ? e.message : 'Failed to load this section.');
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });

    return () => controller.abort();
  }, [selectedTab, days, groupBy, reloadToken, overview, adoption, meetings, collaboration, conversations, people]);

  const onTabSelect: SelectTabEventHandler = (_, d) => setSelectedTab(d.value as TabKey);

  const runExport = useCallback(
    async (section: TeamsExportSection) => {
      setExporting(true);
      try {
        await downloadTeamsExport(section, days, groupBy);
        toast.success('Export downloaded');
      } catch (e: unknown) {
        toast.error(e instanceof Error ? e.message : 'The export failed.');
      } finally {
        setExporting(false);
      }
    },
    [days, groupBy],
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
          <Title3>Teams Explorer</Title3>
          <Body1 block className={styles.intro}>
            How Microsoft Teams is actually being used: who has adopted it, how habitually, what the
            meeting load looks like, which teams and channels are alive, and where the governance
            gaps are. Every figure carries its definition and the SQL behind it.
          </Body1>
        </div>

        <div className={styles.controls}>
          <Select
            value={String(days)}
            onChange={(_, d) => setDays(Number(d.value))}
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
            None of the Teams imports are switched on, so this page has nothing to report. The
            details above say exactly what to enable.
          </MessageBarBody>
        </MessageBar>
      )}

      <TabList
        className={styles.tabs}
        selectedValue={selectedTab}
        onTabSelect={onTabSelect}
        aria-label="Teams Explorer sections"
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

        {loading && !hasData && <Spinner label="Loading Teams data..." />}

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
        This page replaces the archived Teams Power BI template. It reads the base tables directly
        rather than the legacy reporting views, so it reflects what the importer actually collected.
      </Text>
    </div>
  );
}
