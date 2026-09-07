import { Suspense, useEffect, useMemo, useState } from 'react';
import {
  Title3,
  Body1,
  Text,
  Card,
  Input,
  Select,
  Tab,
  TabList,
  MessageBar,
  MessageBarBody,
  Button,
  makeStyles,
  tokens,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular } from '@fluentui/react-icons';
import { fetchReportAreas, fetchReportArea } from '../api/reportsApi';
import type { ReportAreaData, ReportAreaKey, ReportAreas } from '../types/reports';
import Spinner from '../components/Spinner';
import SqlPopover from '../components/SqlPopover';
import TimeSeriesChart from '../components/charts/TimeSeriesChart';
import CategoryBarChart from '../components/charts/CategoryBarChart';
import WordCloud from '../components/charts/WordCloud';
import { lazyWithReload } from '../lazyWithReload';

// The Licence activity tab is an always-present part of Reports (it doesn't depend on any import
// flag). Lazy-loaded so opening Reports for the charts doesn't pay for it until the tab is chosen.
const LicenceActivityPage = lazyWithReload(() => import('./LicenceActivityPage'));

/** The report areas in display order, with the enabled-flag they map to and their friendly copy. */
const AREA_DEFS: { flag: keyof ReportAreas; key: ReportAreaKey; label: string; blurb: string }[] = [
  { flag: 'copilot', key: 'copilot', label: 'Copilot', blurb: 'Microsoft 365 Copilot adoption and usage.' },
  { flag: 'copilot', key: 'copilot-agents', label: 'Copilot agents', blurb: 'Copilot agent popularity and usage.' },
  { flag: 'usage', key: 'usage', label: 'Microsoft 365 usage', blurb: 'Weekly active users across Microsoft 365 workloads.' },
  { flag: 'spoAudit', key: 'spo-audit', label: 'SharePoint & OneDrive', blurb: 'File activity from the audit log.' },
  { flag: 'webTraffic', key: 'web-traffic', label: 'Website traffic', blurb: 'Page views and visitors from the page tracker.' },
  { flag: 'calls', key: 'calls', label: 'Teams calls', blurb: 'Teams call volume and duration.' },
  { flag: 'emails', key: 'emails', label: 'Emails', blurb: 'Sent email volume.' },
];

const MONTH_OPTIONS = [
  { value: 1, label: 'Last month' },
  { value: 3, label: 'Last 3 months' },
  { value: 6, label: 'Last 6 months' },
];

/** Tab identity: one of the report areas, or the always-present Licence activity tab. */
type ReportTab = ReportAreaKey | 'licence-activity';
const LICENCE_TAB = 'licence-activity' as const;

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
  },
  subTabs: {
    marginTop: '16px',
  },
  cards: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    marginTop: '16px',
  },
  chartCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  chartHead: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  chartBody: {
    marginTop: '8px',
  },
});

/**
 * Reports tab: a lightweight, in-app version of the Power BI reports. Shows a sub-tab per report
 * area, but only for the areas whose import is enabled, and charts each area's usage over a
 * configurable window (default the last 3 months). Data comes from api/Reports.
 */
export default function ReportsPage() {
  const styles = useStyles();

  const [areas, setAreas] = useState<ReportAreas | null>(null);
  const [areasError, setAreasError] = useState<string | null>(null);
  const [areasLoading, setAreasLoading] = useState(true);

  const [months, setMonths] = useState(3);
  const [selectedTab, setSelectedTab] = useState<ReportTab | null>(null);
  const [topAgents, setTopAgents] = useState(8);
  const [agentNameDraft, setAgentNameDraft] = useState('');
  const [agentNameFilter, setAgentNameFilter] = useState('');

  useEffect(() => {
    let cancelled = false;
    setAreasLoading(true);
    setAreasError(null);
    fetchReportAreas()
      .then((a) => {
        if (!cancelled) setAreas(a);
      })
      .catch((e) => {
        if (!cancelled) setAreasError(e instanceof Error ? e.message : 'Failed to load report areas.');
      })
      .finally(() => {
        if (!cancelled) setAreasLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const enabledAreas = useMemo(
    () => (areas ? AREA_DEFS.filter((d) => areas[d.flag]) : []),
    [areas],
  );

  // The Licence activity tab is always present and independent of the report-area flags. Choose a
  // default once areas resolve (or fail to): keep an explicit Licence choice, else land on the first
  // enabled report area, falling back to Licence activity when no report imports are enabled.
  useEffect(() => {
    if (areas === null && !areasError) return; // still loading - don't pick a default yet
    setSelectedTab((current) => {
      if (current === LICENCE_TAB) return current;
      if (current && enabledAreas.some((a) => a.key === current)) return current;
      return enabledAreas.length > 0 ? enabledAreas[0].key : LICENCE_TAB;
    });
  }, [areas, areasError, enabledAreas]);

  const onLicence = selectedTab === LICENCE_TAB;
  const onTabSelect: SelectTabEventHandler = (_e, data) => setSelectedTab(data.value as ReportTab);

  return (
    <div>
      <div className={styles.header}>
        <div>
          <Title3>Reports</Title3>
          <Body1 block className={styles.intro}>
            A quick, built-in view of how your Microsoft 365 usage is trending. The report charts appear only when
            their data is being imported; the Licence activity tab is always shown and explains what it needs if a
            prerequisite import is off.
          </Body1>
        </div>
        {!onLicence && (
          <div className={styles.controls}>
            <Text size={200} className={styles.muted}>
              Period
            </Text>
            <Select
              value={String(months)}
              onChange={(_e, data) => setMonths(Number(data.value))}
              aria-label="Reporting period"
            >
              {MONTH_OPTIONS.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
          </div>
        )}
      </div>

      {/* The tab strip is ALWAYS rendered - the Licence activity tab must exist even while the
          report areas are loading, errored, or all disabled. Report-area tabs are added as they load. */}
      <div className={styles.subTabs}>
        <TabList selectedValue={selectedTab ?? ''} onTabSelect={onTabSelect}>
          {enabledAreas.map((a) => (
            <Tab key={a.key} value={a.key}>
              {a.label}
            </Tab>
          ))}
          <Tab value={LICENCE_TAB}>Licence activity</Tab>
        </TabList>
      </div>

      {/* The report-areas load failure is rendered HERE, outside the tab content below, so it stays
          visible even when the Licence activity tab (which does not depend on the report-area flags)
          is the selected/default tab. Inside the content switch the Licence branch wins and the error
          would never show. */}
      {areasError && (
        <MessageBar intent="error" style={{ marginTop: '16px' }}>
          <MessageBarBody>
            {areasError} This affects only the built-in report charts; the Licence activity tab is unaffected.
          </MessageBarBody>
        </MessageBar>
      )}

      {!areasLoading && !areasError && enabledAreas.length === 0 && (
        <MessageBar intent="info" style={{ marginTop: '16px' }}>
          <MessageBarBody>
            No built-in report charts are available yet because no data imports are enabled. Enable one or more
            imports (Copilot, usage reports, SharePoint activity, website traffic, Teams calls or emails) in the
            installer to see them. The Licence activity tab stays visible; open it to see licence and workload
            activity, and it explains its own prerequisites if the licence import is off.
          </MessageBarBody>
        </MessageBar>
      )}

      {onLicence ? (
        <Suspense
          fallback={
            <div style={{ textAlign: 'center', padding: '32px' }}>
              <Spinner size={80} label="Loading licence activity..." />
            </div>
          }
        >
          <LicenceActivityPage />
        </Suspense>
      ) : areasLoading ? (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={80} label="Loading reports..." />
        </div>
      ) : areasError ? null : selectedTab && enabledAreas.some((a) => a.key === selectedTab) ? (
        <>
          {selectedTab === 'copilot-agents' && (
            <div className={styles.controls} style={{ marginTop: '16px', flexWrap: 'wrap' }}>
              <Text size={200} className={styles.muted}>
                Top agents
              </Text>
              <Select
                value={String(topAgents)}
                onChange={(_e, data) => setTopAgents(Number(data.value))}
                aria-label="Number of top Copilot agents"
              >
                {[5, 8, 10, 15, 20].map((count) => (
                  <option key={count} value={count}>
                    {count}
                  </option>
                ))}
              </Select>
              <Input
                value={agentNameDraft}
                onChange={(_e, data) => setAgentNameDraft(data.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') setAgentNameFilter(agentNameDraft.trim());
                }}
                placeholder="Filter by agent name"
                aria-label="Filter Copilot agents by name"
              />
              <Button size="small" onClick={() => setAgentNameFilter(agentNameDraft.trim())}>
                Apply
              </Button>
              {agentNameFilter && (
                <Button
                  appearance="subtle"
                  size="small"
                  onClick={() => {
                    setAgentNameDraft('');
                    setAgentNameFilter('');
                  }}
                >
                  Clear
                </Button>
              )}
            </div>
          )}

          <ReportAreaView
            key={selectedTab}
            area={selectedTab as ReportAreaKey}
            months={months}
            blurb={enabledAreas.find((a) => a.key === selectedTab)?.blurb ?? ''}
            topAgents={topAgents}
            agentName={agentNameFilter}
          />
        </>
      ) : null}
    </div>
  );
}

/** Fetches and renders the charts for a single report area over the chosen window. */
function ReportAreaView({
  area,
  months,
  blurb,
  topAgents,
  agentName,
}: {
  area: ReportAreaKey;
  months: number;
  blurb: string;
  topAgents: number;
  agentName: string;
}) {
  const styles = useStyles();

  const [data, setData] = useState<ReportAreaData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    fetchReportArea(
      area,
      months,
      area === 'copilot-agents' ? { topAgents, agentName } : undefined,
    )
      .then((d) => {
        if (!cancelled) setData(d);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load the report.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [area, months, topAgents, agentName, reloadKey]);

  if (loading) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={64} label="Loading charts..." />
      </div>
    );
  }

  if (error) {
    return (
      <MessageBar intent="error" style={{ marginTop: '16px' }}>
        <MessageBarBody>{error}</MessageBarBody>
      </MessageBar>
    );
  }

  if (!data) return null;

  const fromLabel = new Date(data.fromWeek).toLocaleDateString(undefined, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });

  return (
    <div className={styles.cards}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' }}>
        <Text size={200} className={styles.muted}>
          {blurb} Weeks from {fromLabel}
          {area === 'usage'
            ? '. Usage reports arrive a few days late, so the latest weeks appear once their report does.'
            : ' to now.'}
        </Text>
        <Button
          appearance="subtle"
          size="small"
          icon={<ArrowClockwise16Regular />}
          onClick={() => setReloadKey((k) => k + 1)}
        >
          Refresh
        </Button>
      </div>

      {area === 'copilot' && data.cognitiveConfigured === false && (
        <MessageBar intent="info">
          <MessageBarBody>
            Prompt insights (common prompt phrases, weekly prompt sentiment and prompt language) are not shown because
            Azure AI Language is not configured. Those three charts are built from cognitive enrichment of Copilot
            prompt history, so without it they would always be empty. Add a Cognitive Services endpoint and key in the
            installer, then re-run the Copilot interaction history import, to enable them.
          </MessageBarBody>
        </MessageBar>
      )}

      {data.charts.map((chart) => (
        <Card key={chart.key} className={styles.chartCard}>
          <div className={styles.chartHead}>
            <div>
              <Text weight="semibold" size={400}>
                {chart.title}
              </Text>
              <Text size={200} block className={styles.muted}>
                {chart.description}
              </Text>
            </div>
            <SqlPopover sql={chart.sql} title="SQL behind this chart" />
          </div>

          <div className={styles.chartBody}>
            {chart.error ? (
              <MessageBar intent="warning">
                <MessageBarBody>Couldn't load this chart: {chart.error}</MessageBarBody>
              </MessageBar>
            ) : (
              <>
                {chart.warning && (
                  <MessageBar intent="warning">
                    <MessageBarBody>{chart.warning}</MessageBarBody>
                  </MessageBar>
                )}
                {chart.type === 'timeseries' && chart.series ? (
                  <TimeSeriesChart series={chart.series} valueLabel={chart.valueLabel} />
                ) : chart.type === 'bar' && chart.categories ? (
                  <CategoryBarChart categories={chart.categories} valueLabel={chart.valueLabel} />
                ) : chart.type === 'wordcloud' && chart.categories ? (
                  <WordCloud categories={chart.categories} valueLabel={chart.valueLabel} />
                ) : (
                  <Text className={styles.muted}>No data for this period.</Text>
                )}
              </>
            )}
          </div>
        </Card>
      ))}
    </div>
  );
}
