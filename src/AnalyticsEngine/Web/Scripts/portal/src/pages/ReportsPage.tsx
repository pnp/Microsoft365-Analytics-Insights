import { useEffect, useMemo, useState } from 'react';
import {
  Title3,
  Body1,
  Link,
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
import type { ReportAreaData, ReportAreaKey, ReportAreas, ReportChart } from '../types/reports';
import Spinner from '../components/Spinner';
import SqlPopover from '../components/SqlPopover';
import TimeSeriesChart from '../components/charts/TimeSeriesChart';
import CategoryBarChart from '../components/charts/CategoryBarChart';
import MatrixChart from '../components/charts/MatrixChart';
import WordCloud from '../components/charts/WordCloud';
import { formatDateParts, formatNumber, useT, useTNode, type TranslationKey } from '../i18n';

/** The report areas in display order, with the enabled-flag they map to and their friendly copy. */
const AREA_DEFS: { flag: keyof ReportAreas; key: ReportAreaKey; labelKey: TranslationKey; blurbKey: TranslationKey }[] = [
  { flag: 'copilot', key: 'copilot', labelKey: 'reports.area.copilot.label', blurbKey: 'reports.area.copilot.blurb' },
  { flag: 'copilot', key: 'copilot-agents', labelKey: 'reports.area.copilotAgents.label', blurbKey: 'reports.area.copilotAgents.blurb' },
  { flag: 'usage', key: 'usage', labelKey: 'reports.area.usage.label', blurbKey: 'reports.area.usage.blurb' },
  {
    flag: 'officeApps',
    key: 'office-apps',
    labelKey: 'reports.area.officeApps.label',
    blurbKey: 'reports.area.officeApps.blurb',
  },
  { flag: 'spoAudit', key: 'spo-audit', labelKey: 'reports.area.spoAudit.label', blurbKey: 'reports.area.spoAudit.blurb' },
  { flag: 'webTraffic', key: 'web-traffic', labelKey: 'reports.area.webTraffic.label', blurbKey: 'reports.area.webTraffic.blurb' },
  { flag: 'calls', key: 'calls', labelKey: 'reports.area.calls.label', blurbKey: 'reports.area.calls.blurb' },
  { flag: 'emails', key: 'emails', labelKey: 'reports.area.emails.label', blurbKey: 'reports.area.emails.blurb' },
];

const MONTH_OPTIONS = [
  { value: 1, labelKey: 'reports.period.lastMonth' },
  { value: 3, labelKey: 'reports.period.last3Months' },
  { value: 6, labelKey: 'reports.period.last6Months' },
] satisfies { value: number; labelKey: TranslationKey }[];

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
  const t = useT();
  const tNode = useTNode();

  const [areas, setAreas] = useState<ReportAreas | null>(null);
  const [areasError, setAreasError] = useState<string | null>(null);
  const [areasLoading, setAreasLoading] = useState(true);

  const [months, setMonths] = useState(3);
  const [selectedTab, setSelectedTab] = useState<ReportAreaKey | null>(null);
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
        if (!cancelled) setAreasError(e instanceof Error ? e.message : t('reports.error.loadAreas'));
      })
      .finally(() => {
        if (!cancelled) setAreasLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [t]);

  const enabledAreas = useMemo(
    () => (areas ? AREA_DEFS.filter((d) => areas[d.flag]) : []),
    [areas],
  );

  useEffect(() => {
    if (areas === null && !areasError) return; // still loading - don't pick a default yet
    setSelectedTab((current) => {
      if (current && enabledAreas.some((a) => a.key === current)) return current;
      return enabledAreas[0]?.key ?? null;
    });
  }, [areas, areasError, enabledAreas]);

  const onTabSelect: SelectTabEventHandler = (_e: unknown, data: { value: unknown }) => {
    const area = enabledAreas.find((a) => a.key === data.value);
    if (area) setSelectedTab(area.key);
  };

  return (
    <div>
      <div className={styles.header}>
        <div>
          <Title3>{t('reports.title')}</Title3>
          <Body1 block className={styles.intro}>
            {tNode('reports.intro.licenceActivity', {
              link: <Link href="#/insights/licence-activity">{t('reports.intro.licenceActivityLink')}</Link>,
            })}
          </Body1>
        </div>
        {enabledAreas.length > 0 && (
          <div className={styles.controls}>
            <Text size={200} className={styles.muted}>
              {t('reports.period.label')}
            </Text>
            <Select
              value={String(months)}
              onChange={(_e: unknown, data: { value: string }) => setMonths(Number(data.value))}
              aria-label={t('reports.period.ariaLabel')}
            >
              {MONTH_OPTIONS.map((o) => (
                <option key={o.value} value={o.value}>
                  {t(o.labelKey)}
                </option>
              ))}
            </Select>
          </div>
        )}
      </div>

      {enabledAreas.length > 0 && (
        <div className={styles.subTabs}>
          <TabList selectedValue={selectedTab ?? ''} onTabSelect={onTabSelect}>
            {enabledAreas.map((a) => (
              <Tab key={a.key} value={a.key}>
                {t(a.labelKey)}
              </Tab>
            ))}
          </TabList>
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
            {t('reports.empty.noImports')}
          </MessageBarBody>
        </MessageBar>
      )}

      {areasLoading ? (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={80} label={t('reports.loading.reports')} />
        </div>
      ) : areasError ? null : selectedTab && enabledAreas.some((a) => a.key === selectedTab) ? (
        <>
          {selectedTab === 'copilot-agents' && (
            <div className={styles.controls} style={{ marginTop: '16px', flexWrap: 'wrap' }}>
              <Text size={200} className={styles.muted}>
                {t('reports.topAgents.label')}
              </Text>
              <Select
                value={String(topAgents)}
                onChange={(_e: unknown, data: { value: string }) => setTopAgents(Number(data.value))}
                aria-label={t('reports.topAgents.ariaLabel')}
              >
                {[5, 8, 10, 15, 20].map((count) => (
                  <option key={count} value={count}>
                    {count}
                  </option>
                ))}
              </Select>
              <Input
                value={agentNameDraft}
                onChange={(_e: unknown, data: { value: string }) => setAgentNameDraft(data.value)}
                onKeyDown={(event: { key: string }) => {
                  if (event.key === 'Enter') setAgentNameFilter(agentNameDraft.trim());
                }}
                placeholder={t('reports.topAgents.filterPlaceholder')}
                aria-label={t('reports.topAgents.filterAriaLabel')}
              />
              <Button size="small" onClick={() => setAgentNameFilter(agentNameDraft.trim())}>
                {t('common.action.apply')}
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
                  {t('common.action.clear')}
                </Button>
              )}
            </div>
          )}

          <ReportAreaView
            key={selectedTab}
            area={selectedTab}
            months={months}
            blurb={t(enabledAreas.find((a) => a.key === selectedTab)!.blurbKey)}
            topAgents={topAgents}
            agentName={agentNameFilter}
          />
        </>
      ) : null}
    </div>
  );
}

/**
 * Whether a chart has anything to draw.
 *
 * A `timeseries` week with a null value means "unknown" rather than zero, so a series made entirely
 * of nulls is not data.
 */
function chartHasData(chart: ReportChart): boolean {
  if (chart.series?.some((s) => s.points.some((p) => p.value !== null))) return true;
  if ((chart.categories?.length ?? 0) > 0) return true;
  if ((chart.matrix?.cells.length ?? 0) > 0) return true;
  return false;
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
  const t = useT();
  const tNode = useTNode();

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
        if (!cancelled) setError(e instanceof Error ? e.message : t('reports.error.loadReport'));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [area, months, topAgents, agentName, reloadKey, t]);

  if (loading) {
    return (
      <div style={{ textAlign: 'center', padding: '32px' }}>
        <Spinner size={64} label={t('reports.loading.charts')} />
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

  const fromLabel = formatDateParts(new Date(data.fromWeek), {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });

  return (
    <div className={styles.cards}>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' }}>
        <Text size={200} className={styles.muted}>
          {area === 'usage' || area === 'office-apps'
            ? t('reports.areaHeader.usageLag', { blurb, from: fromLabel })
            : t('reports.areaHeader.toNow', { blurb, from: fromLabel })}
        </Text>
        <Button
          appearance="subtle"
          size="small"
          icon={<ArrowClockwise16Regular />}
          onClick={() => setReloadKey((k) => k + 1)}
        >
          {t('common.action.refresh')}
        </Button>
      </div>

      {area === 'calls' && (
        <MessageBar intent="info">
          <MessageBarBody>
            {tNode('reports.callsInfo.teamsExplorer', {
              link: <Link href="#/insights/teams">{t('reports.callsInfo.teamsExplorerLink')}</Link>,
            })}
          </MessageBarBody>
        </MessageBar>
      )}

      {/*
        The server clamps the window for areas whose queries cannot finish over the longer one. Say
        so rather than silently charting a different period from the one selected - an unexplained
        mismatch between the control and the data is worse than the shorter window.
      */}
      {data.months < months && (
        <MessageBar intent="info">
          <MessageBarBody>
            {t('reports.clampedWindow', { current: formatNumber(data.months), selected: formatNumber(months) })}
          </MessageBarBody>
        </MessageBar>
      )}

      {area === 'copilot' && data.cognitiveConfigured === false && (
        <MessageBar intent="info">
          <MessageBarBody>
            {t('reports.promptInsights.notConfigured')}
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
            <SqlPopover sql={chart.sql} title={t('reports.chart.sqlTitle')} />
          </div>

          <div className={styles.chartBody}>
            {chart.error ? (
              <MessageBar intent="warning">
                <MessageBarBody>{t('reports.chart.loadError', { error: chart.error })}</MessageBarBody>
              </MessageBar>
            ) : (
              <>
                {chart.warning && (
                  <MessageBar intent="warning">
                    <MessageBarBody>{chart.warning}</MessageBarBody>
                  </MessageBar>
                )}
                {/*
                  A chart that explains why it is empty must not ALSO print the generic
                  "No data for this period." beneath that explanation - the two together read as a
                  contradiction, and the generic line is the less true of the pair. A warning
                  alongside real data (e.g. one series of several failed) still renders both.
                */}
                {(!chart.warning || chartHasData(chart)) && (
                  <>
                    {chart.type === 'timeseries' && chart.series ? (
                      <TimeSeriesChart series={chart.series} valueLabel={chart.valueLabel} />
                    ) : chart.type === 'bar' && chart.categories ? (
                      <CategoryBarChart
                        categories={chart.categories}
                        valueLabel={chart.valueLabel}
                        showShare={chart.showShare}
                        valueSuffix={chart.valueSuffix}
                      />
                    ) : chart.type === 'matrix' && chart.matrix ? (
                      <MatrixChart matrix={chart.matrix} valueLabel={chart.valueLabel} />
                    ) : chart.type === 'wordcloud' && chart.categories ? (
                      <WordCloud categories={chart.categories} valueLabel={chart.valueLabel} />
                    ) : (
                      <Text className={styles.muted}>{t('reports.chart.noData')}</Text>
                    )}
                  </>
                )}
              </>
            )}
          </div>
        </Card>
      ))}
    </div>
  );
}
