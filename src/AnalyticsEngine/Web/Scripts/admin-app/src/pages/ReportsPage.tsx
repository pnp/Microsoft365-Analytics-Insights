import { useEffect, useMemo, useState } from 'react';
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
  const [selectedArea, setSelectedArea] = useState<ReportAreaKey | null>(null);
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

  // Default the selection to the first enabled area once we know which areas exist.
  useEffect(() => {
    if (enabledAreas.length === 0) {
      setSelectedArea(null);
      return;
    }
    setSelectedArea((current) =>
      current && enabledAreas.some((a) => a.key === current) ? current : enabledAreas[0].key,
    );
  }, [enabledAreas]);

  const onTabSelect: SelectTabEventHandler = (_e, data) => setSelectedArea(data.value as ReportAreaKey);

  return (
    <div>
      <div className={styles.header}>
        <div>
          <Title3>Reports</Title3>
          <Body1 block className={styles.intro}>
            A quick, built-in view of how your Microsoft 365 usage is trending. Each section below appears only when its
            data is being imported.
          </Body1>
        </div>
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
      </div>

      {areasLoading && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={80} label="Loading reports..." />
        </div>
      )}

      {areasError && (
        <MessageBar intent="error" style={{ marginTop: '16px' }}>
          <MessageBarBody>{areasError}</MessageBarBody>
        </MessageBar>
      )}

      {!areasLoading && !areasError && enabledAreas.length === 0 && (
        <MessageBar intent="info" style={{ marginTop: '16px' }}>
          <MessageBarBody>
            No reports are available yet because no data imports are enabled. Enable one or more imports (Copilot, usage
            reports, SharePoint activity, website traffic, Teams calls or emails) in the installer to see reports here.
          </MessageBarBody>
        </MessageBar>
      )}

      {!areasLoading && enabledAreas.length > 0 && selectedArea && (
        <>
          {enabledAreas.length > 1 && (
            <div className={styles.subTabs}>
              <TabList selectedValue={selectedArea} onTabSelect={onTabSelect}>
                {enabledAreas.map((a) => (
                  <Tab key={a.key} value={a.key}>
                    {a.label}
                  </Tab>
                ))}
              </TabList>
            </div>
          )}

          {selectedArea === 'copilot-agents' && (
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
              <Button
                size="small"
                onClick={() => setAgentNameFilter(agentNameDraft.trim())}
              >
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
            key={selectedArea}
            area={selectedArea}
            months={months}
            blurb={enabledAreas.find((a) => a.key === selectedArea)?.blurb ?? ''}
            topAgents={topAgents}
            agentName={agentNameFilter}
          />
        </>
      )}
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
            ) : chart.type === 'timeseries' && chart.series ? (
              <TimeSeriesChart series={chart.series} valueLabel={chart.valueLabel} />
            ) : chart.type === 'bar' && chart.categories ? (
              <CategoryBarChart categories={chart.categories} valueLabel={chart.valueLabel} />
            ) : (
              <Text className={styles.muted}>No data for this period.</Text>
            )}
          </div>
        </Card>
      ))}
    </div>
  );
}
