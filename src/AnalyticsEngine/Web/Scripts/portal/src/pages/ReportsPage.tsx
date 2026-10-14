import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import {
  Title3,
  Body1,
  Link,
  Text,
  Card,
  Input,
  Select,
  MessageBar,
  MessageBarBody,
  Button,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular } from '@fluentui/react-icons';
import { fetchReportArea } from '../api/reportsApi';
import type { ReportAreaData, ReportAreaKey, ReportChart, ReportMatrix, ReportSeries, ReportSeriesWarning } from '../types/reports';
import Spinner from '../components/Spinner';
import SqlPopover from '../components/SqlPopover';
import TimeSeriesChart from '../components/charts/TimeSeriesChart';
import CategoryBarChart from '../components/charts/CategoryBarChart';
import MatrixChart from '../components/charts/MatrixChart';
import WordCloud from '../components/charts/WordCloud';
import GlobalFilterBar from '../components/globalFilter/GlobalFilterBar';
import PromptCategoryReport from '../components/PromptCategoryReport';
import ReportAreasProvider, { useReportAreas } from '../components/ReportAreasProvider';
import { REPORT_PAGES, PROMPT_CHART_KEYS, reportImportLabelKey, reportMonths, reportPageEnabled, reportPath, type ReportPageDefinition } from '../reportNavigation';
import { EN_CATALOG, formatDateParts, formatNumber, plural, useT, useTNode, type TFunction, type TranslationKey } from '../i18n';

const MONTH_OPTIONS = [
  { value: 1, labelKey: 'reports.period.lastMonth' },
  { value: 3, labelKey: 'reports.period.last3Months' },
  { value: 6, labelKey: 'reports.period.last6Months' },
] satisfies { value: number; labelKey: TranslationKey }[];

export const OFFICE_PLATFORM_LABEL_KEYS: Record<string, TranslationKey> = {
  Mobile: 'reports.platform.mobile',
  Web: 'reports.platform.web',
};

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
  heading: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '4px',
    minWidth: 0,
  },
  intro: {
    marginTop: '8px',
  },
  catalog: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 280px), 1fr))',
    gap: '16px',
    marginTop: '16px',
  },
  catalogCard: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '12px',
  },
  catalogTitle: {
    margin: 0,
  },
  catalogDescription: {
    flexGrow: 1,
    color: tokens.colorNeutralForeground3,
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

export default function ReportsPage({ page }: { page?: ReportPageDefinition }) {
  return <ReportAreasProvider><ReportsContent key={page?.path ?? 'catalog'} page={page} /></ReportAreasProvider>;
}

function ReportsContent({ page }: { page?: ReportPageDefinition }) {
  const styles = useStyles();
  const t = useT();
  const tNode = useTNode();

  const { areas, error: areasError, loading: areasLoading, retry } = useReportAreas();
  const [params, setParams] = useSearchParams();
  const months = reportMonths(`?${params.toString()}`);
  const topValue = params.get('top') ?? '8';
  const topAgents = ['5', '8', '10', '15', '20'].includes(topValue) ? Number(topValue) : 8;
  const agentNameFilter = params.get('agentName') ?? '';
  const [agentNameDraft, setAgentNameDraft] = useState(agentNameFilter);
  useEffect(() => setAgentNameDraft(agentNameFilter), [agentNameFilter]);

  const updateParam = (key: string, value: string) => {
    setParams(previous => {
      const next = new URLSearchParams(previous);
      if (value) next.set(key, value);
      else next.delete(key);
      return next;
    }, { replace: true });
  };
  const enabled = page && areas ? reportPageEnabled(page, areas) : false;
  const invalidPeriod = params.has('months') && !['1', '3', '6'].includes(params.get('months') ?? '');
  const invalidTop = page?.area === 'copilot-agents' && !['5', '8', '10', '15', '20'].includes(topValue);

  return (
    <div>
      <div className={styles.header}>
        <div className={styles.heading}>
          {page && <Link href={`#${reportPath('/insights/reports', months)}`}>{t('reports.backToCatalog')}</Link>}
          <Title3 as="h1" className={styles.catalogTitle}>{t(page?.titleKey ?? 'reports.title')}</Title3>
          <Body1 block className={styles.intro}>
            {page ? t(page.blurbKey) : tNode('reports.catalog.intro', {
              link: <Link href="#/insights/licence-activity">{t('reports.intro.licenceActivityLink')}</Link>,
            })}
          </Body1>
        </div>
        {page && enabled && !areasLoading && !areasError && (
          <div className={styles.controls}>
            <Text size={200} className={styles.muted}>
              {t('reports.period.label')}
            </Text>
            <Select
              value={String(months)}
              onChange={(_e: unknown, data: { value: string }) => updateParam('months', data.value)}
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

      <GlobalFilterBar />

      {areasError && (
        <MessageBar intent="error" style={{ marginTop: '16px' }}>
          <MessageBarBody>{areasError}</MessageBarBody>
          <Button onClick={retry}>{t('common.action.refresh')}</Button>
        </MessageBar>
      )}

      {!areasLoading && !areasError && (invalidPeriod || invalidTop) && (
        <MessageBar intent="warning"><MessageBarBody>{t('reports.invalidParameters')}</MessageBarBody></MessageBar>
      )}

      {!page && !areasLoading && !areasError && areas && !REPORT_PAGES.some(report => reportPageEnabled(report, areas)) && (
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
      ) : areasError ? null : !page && areas ? (
        <div className={styles.catalog}>
          {REPORT_PAGES.map(report => (
            <Card key={report.path} className={styles.catalogCard}>
              <Text as="h2" size={400} weight="semibold" className={styles.catalogTitle}>{t(report.titleKey)}</Text>
              <Text className={styles.catalogDescription}>{t(report.area === 'office-apps' ? 'reports.catalog.officeAppsBlurb' : report.blurbKey)}</Text>
              <Text size={200}>{t(report.prompts && areas.copilot && !areas.copilotInteractionHistory
                ? 'reports.catalog.historyDisabled'
                : reportPageEnabled(report, areas) ? 'reports.catalog.enabled' : 'reports.catalog.disabled')}</Text>
              <Link href={`#${reportPath(report.path, months)}`} aria-label={t('reports.catalog.openNamed', { report: t(report.titleKey) })}>{t('reports.catalog.open')}</Link>
            </Card>
          ))}
        </div>
      ) : page && !enabled ? (
        <MessageBar intent="info" style={{ marginTop: '16px' }}>
          <MessageBarBody>{page.prompts
            ? t('reports.unavailable.prompts')
            : t('reports.unavailable.import', { import: t(reportImportLabelKey(page)) })}</MessageBarBody>
        </MessageBar>
      ) : page && areas ? (
        <>
          {page.area === 'copilot-agents' && (
            <div className={styles.controls} style={{ marginTop: '16px', flexWrap: 'wrap' }}>
              <Text size={200} className={styles.muted}>
                {t('reports.topAgents.label')}
              </Text>
              <Select
                value={String(topAgents)}
                onChange={(_e: unknown, data: { value: string }) => updateParam('top', data.value)}
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
                  if (event.key === 'Enter') updateParam('agentName', agentNameDraft.trim());
                }}
                placeholder={t('reports.topAgents.filterPlaceholder')}
                aria-label={t('reports.topAgents.filterAriaLabel')}
              />
              <Button size="small" onClick={() => updateParam('agentName', agentNameDraft.trim())}>
                {t('common.action.apply')}
              </Button>
              {agentNameFilter && (
                <Button
                  appearance="subtle"
                  size="small"
                  onClick={() => {
                    setAgentNameDraft('');
                    updateParam('agentName', '');
                  }}
                >
                  {t('common.action.clear')}
                </Button>
              )}
            </div>
          )}

          {(!page.prompts || areas.copilot) && (
            <ReportAreaView
              area={page.area}
              months={months}
              topAgents={topAgents}
              agentName={agentNameFilter}
              prompts={page.prompts ?? false}
            />
          )}
          {page.prompts && !areas.copilot && (
            <MessageBar intent="info"><MessageBarBody>{t('reports.promptInsights.auditUnavailable')}</MessageBarBody></MessageBar>
          )}
          {page.prompts && (areas.copilotInteractionHistory ? (
            <div className={styles.cards}><PromptCategoryReport months={months} /></div>
          ) : (
            <MessageBar intent="info"><MessageBarBody>{t('reports.unavailable.prompts')}</MessageBarBody></MessageBar>
          ))}
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

function chartText(
  t: TFunction,
  key: string,
  field: 'title' | 'description' | 'valueLabel' | 'warning' | 'rowLabel' | 'columnLabel',
  fallback: string,
): string {
  if (!key) return fallback;
  const catalogKey = chartTranslationKey(key, field, fallback);
  const translated = t(catalogKey);
  return translated === catalogKey ? fallback : translated;
}

function chartTranslationKey(
  key: string,
  field: 'title' | 'description' | 'valueLabel' | 'warning' | 'rowLabel' | 'columnLabel',
  fallback: string,
): TranslationKey {
  if (
    key === 'office-apps-department-adoption' &&
    field === 'description' &&
    fallback === EN_CATALOG['reports.chart.office-apps-department-adoption.description.groupFiltered']
  ) {
    return 'reports.chart.office-apps-department-adoption.description.groupFiltered';
  }

  const copilotAttachConfirmFailedParts = EN_CATALOG['reports.chart.office-apps-copilot-attach.warning.confirmFailed'].split('{error}');
  if (
    key === 'office-apps-copilot-attach' &&
    field === 'warning' &&
    fallback.startsWith(copilotAttachConfirmFailedParts[0])
  ) {
    return 'reports.chart.office-apps-copilot-attach.warning.confirmFailed';
  }

  if (
    key === 'office-apps-copilot-attach' &&
    field === 'warning' &&
    fallback === EN_CATALOG['reports.chart.office-apps-copilot-attach.warning.noReportRows']
  ) {
    return 'reports.chart.office-apps-copilot-attach.warning.noReportRows';
  }

  return `reports.chart.${key}.${field}` as TranslationKey;
}

export function reportChartWarningText(t: TFunction, chart: ReportChart): string | null {
  if (!chart.warning) return null;

  if (chart.key === 'usage-active-users' && chart.seriesWarnings?.length) {
    const details = chart.seriesWarnings.map((warning) => seriesWarningText(t, warning)).join('; ');
    const catalogKey = 'reports.chart.usage-active-users.warning' as TranslationKey;
    const translated = t(catalogKey, { details });
    return translated === catalogKey ? chart.warning : translated;
  }

  const usageWarningParts = EN_CATALOG['reports.chart.usage-active-users.warning'].split('{details}');
  if (chart.key === 'usage-active-users' && chart.warning.startsWith(usageWarningParts[0]) && chart.warning.endsWith(usageWarningParts[1])) {
    const details = chart.warning.slice(usageWarningParts[0].length, -usageWarningParts[1].length);
    const catalogKey = 'reports.chart.usage-active-users.warning' as TranslationKey;
    const translated = t(catalogKey, { details });
    return translated === catalogKey ? chart.warning : translated;
  }

  if (chart.key === 'office-apps-copilot-attach') {
    const [prefix, suffix] = EN_CATALOG['reports.chart.office-apps-copilot-attach.warning.confirmFailed'].split('{error}');
    if (chart.warning.startsWith(prefix) && chart.warning.endsWith(suffix)) {
      const error = chart.warning.slice(prefix.length, -suffix.length);
      const catalogKey = 'reports.chart.office-apps-copilot-attach.warning.confirmFailed' as TranslationKey;
      const translated = t(catalogKey, { error });
      return translated === catalogKey ? chart.warning : translated;
    }
  }

  const catalogKey = chartTranslationKey(chart.key, 'warning', chart.warning);
  if (chart.warning !== EN_CATALOG[catalogKey]) return chart.warning;
  const translated = t(catalogKey);
  return translated === catalogKey ? chart.warning : translated;
}

function seriesWarningText(t: TFunction, warning: ReportSeriesWarning): string {
  const values = {
    series: warning.series,
    error: warning.error ?? '',
    week: warning.week ? formatDateParts(new Date(warning.week), { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' }) : '',
  };

  switch (warning.reason) {
    case 'loadFailed':
      return t('reports.chart.warning.series.loadFailed', values);
    case 'notAttempted':
      return t('reports.chart.warning.series.notAttempted', values);
    case 'noSettledData':
      return t('reports.chart.warning.series.noSettledData', values);
    case 'noSettledDataForWeek':
      return t('reports.chart.warning.series.noSettledDataForWeek', values);
    default:
      return warning.error ? `${warning.series}: ${warning.error}` : warning.series;
  }
}

function chartErrorText(t: TFunction, chart: ReportChart): string {
  if (chart.errorKey === 'noCompletedUsageWeeks') {
    return t('reports.chart.error.noCompletedUsageWeeks');
  }

  if (chart.errorKey === 'noWorkloadSeriesLoaded' && chart.seriesWarnings?.length) {
    const details = chart.seriesWarnings.map((warning) => seriesWarningText(t, warning)).join('; ');
    return t('reports.chart.error.noWorkloadSeriesLoaded', { details });
  }

  if (chart.errorKey === 'noCompletedUsageWeeksWithData') {
    return t('reports.chart.error.noCompletedUsageWeeksWithData');
  }

  return chart.error ?? '';
}

const APP_BREADTH_LABEL = /^(\d+) apps?$/;

export function reportCategories(t: TFunction, chart: ReportChart) {
  if (chart.key === 'office-apps-platform-mix' && chart.categories) {
    return chart.categories.map((category) => ({
      ...category,
      label: reportPlatformLabel(t, category.label),
    }));
  }

  if (chart.key !== 'office-apps-breadth' || !chart.categories) return chart.categories;

  return chart.categories.map((category) => {
    const match = APP_BREADTH_LABEL.exec(category.label);
    if (!match) return category;

    const count = Number(match[1]);
    return {
      ...category,
      label: t(plural(count, 'reports.category.appBreadth.one', 'reports.category.appBreadth.other'), {
        count: formatNumber(count),
      }),
    };
  });
}

function reportPlatformLabel(t: TFunction, label: string): string {
  const key = OFFICE_PLATFORM_LABEL_KEYS[label];
  return key ? t(key) : label;
}

export function reportSeries(t: TFunction, chart: ReportChart): ReportSeries[] | null {
  if (chart.key !== 'office-apps-platform-trend' || !chart.series) return chart.series;

  return chart.series.map((series) => ({
    ...series,
    name: reportPlatformLabel(t, series.name),
  }));
}

export function reportMatrix(t: TFunction, chart: ReportChart): ReportMatrix | null {
  if (!chart.matrix) return null;

  const matrix = {
    ...chart.matrix,
    rowLabel: chartText(t, chart.key, 'rowLabel', chart.matrix.rowLabel),
    columnLabel: chartText(t, chart.key, 'columnLabel', chart.matrix.columnLabel),
  };

  if (chart.key !== 'office-apps-platform-matrix') return matrix;

  return {
    ...matrix,
    columns: matrix.columns.map((column) => reportPlatformLabel(t, column)),
    cells: matrix.cells.map((cell) => ({
      ...cell,
      column: reportPlatformLabel(t, cell.column),
    })),
  };
}

/** Fetches and renders the charts for a single report area over the chosen window. */
function ReportAreaView({
  area,
  months,
  topAgents,
  agentName,
  prompts,
}: {
  area: ReportAreaKey;
  months: number;
  topAgents: number;
  agentName: string;
  prompts: boolean;
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
            ? t('reports.areaHeader.weeksUsageLag', { from: fromLabel })
            : t('reports.areaHeader.weeksToNow', { from: fromLabel })}
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

      {area === 'copilot' && prompts && data.cognitiveConfigured === false && (
        <MessageBar intent="info">
          <MessageBarBody>
            {t('reports.promptInsights.notConfigured')}
          </MessageBarBody>
        </MessageBar>
      )}

      {data.charts.filter(chart => area !== 'copilot' || PROMPT_CHART_KEYS.has(chart.key) === prompts).map((chart) => {
        const title = chartText(t, chart.key, 'title', chart.title);
        const description = chartText(t, chart.key, 'description', chart.description);
        const valueLabel = chartText(t, chart.key, 'valueLabel', chart.valueLabel);
        const warning = reportChartWarningText(t, chart);
        const error = chartErrorText(t, chart);
        const categories = reportCategories(t, chart);
        const series = reportSeries(t, chart);
        const matrix = reportMatrix(t, chart);

        return (
        <Card key={chart.key} className={styles.chartCard}>
          <div className={styles.chartHead}>
            <div>
              <Text weight="semibold" size={400}>
                {title}
              </Text>
              <Text size={200} block className={styles.muted}>
                {description}
              </Text>
            </div>
            <SqlPopover sql={chart.sql} title={t('reports.chart.sqlTitle')} />
          </div>

          <div className={styles.chartBody}>
            {chart.error ? (
              <MessageBar intent="warning">
                <MessageBarBody>{t('reports.chart.loadError', { error })}</MessageBarBody>
              </MessageBar>
            ) : (
              <>
                {chart.warning && (
                  <MessageBar intent="warning">
                    <MessageBarBody>{warning}</MessageBarBody>
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
                    {chart.type === 'timeseries' && series ? (
                      <TimeSeriesChart series={series} valueLabel={valueLabel} />
                    ) : chart.type === 'bar' && categories ? (
                      <CategoryBarChart
                        categories={categories}
                        valueLabel={valueLabel}
                        showShare={chart.showShare}
                        valueSuffix={chart.valueSuffix}
                      />
                    ) : chart.type === 'matrix' && matrix ? (
                      <MatrixChart matrix={matrix} valueLabel={valueLabel} />
                    ) : chart.type === 'wordcloud' && categories ? (
                      <WordCloud categories={categories} valueLabel={valueLabel} />
                    ) : (
                      <Text className={styles.muted}>{t('reports.chart.noData')}</Text>
                    )}
                  </>
                )}
              </>
            )}
          </div>
        </Card>
        );
      })}
    </div>
  );
}
