import { useEffect, useId, useMemo, useRef, useState } from 'react';
import {
  Body1,
  Button,
  Card,
  Link,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  Spinner,
  Text,
  Title3,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { Filter16Regular } from '@fluentui/react-icons';
import { fetchActivityAnalysisAvailability, fetchActivityAnalysisReport } from '../api/activityAnalysisApi';
import { formatList, formatNumber, plural, useT, type TranslationKey } from '../i18n';
import { usePortalAccess } from '../access';
import GlobalFilterBar from '../components/globalFilter/GlobalFilterBar';
import InfoTip from '../components/shared/InfoTip';
import PiiHiddenNote from '../components/shared/PiiHiddenNote';
import PrintButton from '../components/shared/PrintButton';
import ActivePeopleCharts from '../components/activityAnalysis/ActivePeopleCharts';
import AppliedFiltersSummary from '../components/activityAnalysis/AppliedFiltersSummary';
import FiltersPanel from '../components/activityAnalysis/FiltersPanel';
import MetricSlicer from '../components/activityAnalysis/MetricSlicer';
import MetricsByWeekChart from '../components/activityAnalysis/MetricsByWeekChart';
import PeriodControl, { type PeriodChoice } from '../components/activityAnalysis/PeriodControl';
import ResultsMatrix from '../components/activityAnalysis/ResultsMatrix';
import TopPeopleCard from '../components/activityAnalysis/TopPeopleCard';
import {
  NO_FILTERS,
  activeFilterCount,
  describeAppliedFilters,
  draftFrom,
  type AppliedFilters,
  type FilterDraft,
} from '../components/activityAnalysis/filters';
import {
  defaultSelection,
  groupMetrics,
  metricLabel,
  metricOrder,
  metricsByKey,
} from '../components/activityAnalysis/metrics';
import {
  DEFAULT_PRESET,
  formatWeekDate,
  presetPeriod,
  weekCount,
  type WeekPeriod,
} from '../components/activityAnalysis/period';
import { serializeUserFilter } from '../components/userFilter/userFilterModel';
import type {
  ActivityAnalysisAvailability,
  ActivityAnalysisQuery,
  ActivityAnalysisReport,
} from '../types/activityAnalysis';

/**
 * How long the metric selection has to stay still before the figures are asked for again. Ticking a
 * category's metrics one by one should be one request, not six.
 */
export const SELECTION_DEBOUNCE_MS = 400;

const useStyles = makeStyles({
  header: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  intro: {
    maxWidth: '860px',
  },
  message: {
    marginTop: '16px',
  },
  hint: {
    display: 'block',
    marginTop: '6px',
  },
  controls: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
    marginTop: '16px',
    padding: '14px 16px',
  },
  controlRow: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: '12px 16px',
    flexWrap: 'wrap',
  },
  field: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    minWidth: 0,
  },
  fieldLabel: {
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  spacer: {
    flexGrow: 1,
  },
  actions: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    paddingTop: '18px',
  },
  headline: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px 10px',
    flexWrap: 'wrap',
    paddingTop: '10px',
    borderTopWidth: '1px',
    borderTopStyle: 'solid',
    borderTopColor: tokens.colorNeutralStroke3,
  },
  figure: {
    fontSize: tokens.fontSizeBase500,
    lineHeight: tokens.lineHeightBase500,
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
  },
  separator: {
    color: tokens.colorNeutralForeground3,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  updating: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    color: tokens.colorNeutralForeground3,
  },
  // The body: the metric slicer beside the charts, as in the Power BI page. It becomes one column when
  // the page is too narrow for both - measured on the page, not the window, because the navigation rail
  // takes a share of the window when it is open.
  bodyContainer: {
    containerType: 'inline-size',
    marginTop: '16px',
  },
  body: {
    display: 'grid',
    gridTemplateColumns: 'minmax(260px, 300px) minmax(0, 1fr)',
    alignItems: 'start',
    gap: '16px',
    '@container (max-width: 820px)': {
      gridTemplateColumns: 'minmax(0, 1fr)',
    },
    // The slicer is a control, so it is not printed; the charts take the whole width of the sheet.
    '@media print': {
      gridTemplateColumns: 'minmax(0, 1fr)',
    },
  },
  // Stays in view while the charts beside it scroll, so a metric can be added without scrolling back.
  slicer: {
    position: 'sticky',
    top: '12px',
    maxHeight: 'calc(100vh - 24px)',
    overflowY: 'auto',
    '@container (max-width: 820px)': {
      position: 'static',
      maxHeight: 'none',
    },
  },
  main: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    minWidth: 0,
  },
  figures: {
    display: 'flex',
    flexDirection: 'column',
    gap: '16px',
    minWidth: 0,
    transitionProperty: 'opacity',
    transitionDuration: '150ms',
  },
  below: {
    marginTop: '16px',
  },
  // Figures for the previous selection, kept on screen while the new ones load - dimmed, so nobody
  // reads them as the answer to the question just asked.
  stale: {
    opacity: 0.55,
  },
  center: {
    padding: '32px',
    textAlign: 'center',
  },
  empty: {
    padding: '20px',
    color: tokens.colorNeutralForeground3,
  },
  source: {
    display: 'block',
    marginTop: '20px',
    color: tokens.colorNeutralForeground3,
  },
  printOnly: {
    display: 'none',
  },
});

type ReportResult = {
  requestId: string;
  query: ActivityAnalysisQuery;
  data: ActivityAnalysisReport | null;
  error: string | null;
};

function sameKeys(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((key, i) => key === b[i]);
}

const UNAVAILABLE_KEYS: ReadonlyMap<string, { title: TranslationKey; body: TranslationKey }> = new Map([
  [
    'notInstalled',
    { title: 'activityAnalysis.availability.notInstalled.title', body: 'activityAnalysis.availability.notInstalled.body' },
  ],
  ['noData', { title: 'activityAnalysis.availability.noData.title', body: 'activityAnalysis.availability.noData.body' }],
]);

/** Any other reason - one this build does not know, or none at all. */
const UNKNOWN_REASON_KEYS: { title: TranslationKey; body: TranslationKey } = {
  title: 'activityAnalysis.availability.unknown.title',
  body: 'activityAnalysis.availability.unknown.body',
};

/** Why the page cannot be shown: the profiling tables are missing, or hold no weeks yet. */
function Unavailable({ reason }: { reason: string | null }) {
  const styles = useStyles();
  const t = useT();
  const { administration } = usePortalAccess();
  const keys = (reason ? UNAVAILABLE_KEYS.get(reason) : undefined) ?? UNKNOWN_REASON_KEYS;

  return (
    <MessageBar intent="info" layout="multiline" className={styles.message}>
      <MessageBarBody>
        <MessageBarTitle>{t(keys.title)}</MessageBarTitle>
        {t(keys.body)}
        <Text size={200} className={styles.hint}>
          {administration ? (
            <>
              {t('activityAnalysis.availability.adminHint')} <Link href="#/admin/profiling">{t('activityAnalysis.availability.openProfiling')}</Link>
            </>
          ) : (
            t('activityAnalysis.availability.readerHint')
          )}
        </Text>
      </MessageBarBody>
    </MessageBar>
  );
}

/**
 * Activity analysis: the Power BI "Activity Analysis" and "Filter Settings" views, in the portal.
 *
 * Reads the weekly activity profile the profiling runbooks compile (`profiling.ActivitiesWeeklyColumns`)
 * through `api/ActivityAnalysis`. The reader picks metrics, a period and filters; the server returns
 * aggregates, and - for a reader with See PII only - the people behind them on request.
 */
export default function ActivityAnalysisPage() {
  const styles = useStyles();
  const t = useT();
  const [availability, setAvailability] = useState<ActivityAnalysisAvailability | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    setError(null);
    fetchActivityAnalysisAvailability(controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) setAvailability(result);
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : String(e));
      });
    return () => controller.abort();
  }, [attempt]);

  return (
    <div>
      <div className={styles.header}>
        <Title3>{t('activityAnalysis.page.title')}</Title3>
        <Body1 block className={styles.intro}>
          {t('activityAnalysis.page.intro')}
        </Body1>
      </div>

      <GlobalFilterBar />

      {!availability && !error && (
        <div className={styles.center}>
          <Spinner size="large" label={t('activityAnalysis.page.checkingAvailability')} />
        </div>
      )}

      {error && (
        <MessageBar intent="error" className={styles.message}>
          <MessageBarBody>{error}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={() => setAttempt((a) => a + 1)}>
              {t('activityAnalysis.page.retry')}
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {availability && !availability.available && <Unavailable reason={availability.reason} />}
      {availability?.available && <ActivityAnalysisView availability={availability} />}
    </div>
  );
}

function ActivityAnalysisView({ availability }: { availability: ActivityAnalysisAvailability }) {
  const styles = useStyles();
  const t = useT();
  const { seePii } = usePortalAccess();
  const periodLabelId = useId();
  const filtersPanelId = useId();

  const groups = useMemo(() => groupMetrics(availability), [availability]);
  const order = useMemo(() => metricOrder(groups), [groups]);
  const metrics = useMemo(() => metricsByKey(availability.metrics), [availability]);
  const unavailableCount = availability.metrics.filter((m) => !m.available).length;

  // --- Metrics: selected on screen at once, requested once the selection settles ---------------------
  const [selected, setSelected] = useState<string[]>(() => defaultSelection(groups));
  const [requested, setRequested] = useState<string[]>(selected);
  useEffect(() => {
    if (sameKeys(selected, requested)) return;
    const timer = window.setTimeout(() => setRequested(selected), SELECTION_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [selected, requested]);

  // --- Period ------------------------------------------------------------------------------------------
  const [choice, setChoice] = useState<PeriodChoice>(DEFAULT_PRESET);
  const [customPeriod, setCustomPeriod] = useState<WeekPeriod | null>(null);
  const period = useMemo<WeekPeriod | null>(() => {
    if (choice === 'custom' && customPeriod) return customPeriod;
    // The server names its own default period; the other presets are worked out from the same weeks.
    if ((choice === DEFAULT_PRESET || choice === 'custom') && availability.defaultFrom && availability.defaultTo) {
      return { from: availability.defaultFrom, to: availability.defaultTo };
    }
    return presetPeriod(choice === 'custom' ? DEFAULT_PRESET : choice, availability);
  }, [choice, customPeriod, availability]);

  // --- Filters: a draft in the panel, applied on request ------------------------------------------------
  const [applied, setApplied] = useState<AppliedFilters>(NO_FILTERS);
  const [draft, setDraft] = useState<FilterDraft>(() => draftFrom(NO_FILTERS, metrics));
  const [filtersOpen, setFiltersOpen] = useState(false);
  const filtersButtonRef = useRef<HTMLButtonElement>(null);
  // Every filter needs See PII - any condition can be differenced to single one person out, so the API
  // refuses them all to anyone else. Without it there is nothing to edit, and nothing is ever sent.
  const userFilterParam = seePii ? serializeUserFilter(applied.userFilter) : null;
  const effectiveFilters = useMemo<AppliedFilters>(() => (seePii ? applied : NO_FILTERS), [applied, seePii]);
  const filterCount = activeFilterCount(effectiveFilters);

  // --- The report ----------------------------------------------------------------------------------------
  const query = useMemo<ActivityAnalysisQuery | null>(() => {
    if (!period || requested.length === 0) return null;
    return {
      from: period.from,
      to: period.to,
      metrics: requested,
      userFilter: userFilterParam,
      licences: effectiveFilters.licences,
      ranges: effectiveFilters.ranges,
    };
  }, [period, requested, userFilterParam, effectiveFilters.licences, effectiveFilters.ranges]);
  const queryKey = query ? JSON.stringify(query) : null;

  const [attempt, setAttempt] = useState(0);
  const requestId = queryKey ? `${queryKey}#${attempt}` : null;
  const [result, setResult] = useState<ReportResult | null>(null);
  // The last report that loaded, for the licence list and range bounds while a new one loads or fails.
  const [lastGood, setLastGood] = useState<ActivityAnalysisReport | null>(null);

  useEffect(() => {
    if (!query || !requestId) return;
    const controller = new AbortController();
    fetchActivityAnalysisReport(query, controller.signal)
      .then((data) => {
        if (controller.signal.aborted) return;
        setResult({ requestId, query, data, error: null });
        setLastGood(data);
      })
      .catch((e: unknown) => {
        if (controller.signal.aborted) return;
        setResult({ requestId, query, data: null, error: e instanceof Error ? e.message : String(e) });
      });
    return () => controller.abort();
    // `query` is identified by `requestId`, which also changes on "Try again".
  }, [requestId]);

  const loading = requestId !== null && result?.requestId !== requestId;
  const report = query ? (result?.data ?? null) : null;
  const reportQuery = result?.data ? result.query : null;
  const reportError = query && !loading && result?.error ? result.error : null;

  const metricLabels = (report?.metrics ?? requested).map((key) => metricLabel(t, key, metrics.get(key)?.label));
  const periodSummary = period
    ? t(plural(weekCount(period), 'activityAnalysis.period.summary.one', 'activityAnalysis.period.summary.other'), {
        from: formatWeekDate(period.from),
        to: formatWeekDate(period.to),
        weeks: formatNumber(weekCount(period)),
      })
    : '';
  const filterWords = describeAppliedFilters(t, effectiveFilters, metrics, lastGood?.licences ?? [], {
    names: lastGood?.userFilter?.dimensionNames,
  });

  // Available, but with no week to show - a server that says so without a reason.
  if (!period) return <Unavailable reason={availability.reason} />;

  if (groups.length === 0) {
    return (
      <MessageBar intent="info" className={styles.message}>
        <MessageBarBody>{t('activityAnalysis.availability.noMetrics')}</MessageBarBody>
      </MessageBar>
    );
  }

  return (
    <>
      <Card className={styles.controls}>
        <div className={styles.controlRow} data-print="hide">
          <div className={styles.field}>
            <Text id={periodLabelId} size={200} className={styles.fieldLabel}>
              {t('activityAnalysis.period.label')}
            </Text>
            <PeriodControl
              availability={availability}
              choice={choice}
              period={period}
              labelId={periodLabelId}
              onPreset={(preset) => setChoice(preset)}
              onCustom={(custom) => {
                setCustomPeriod(custom);
                setChoice('custom');
              }}
            />
          </div>
          <div className={styles.spacer} />
          <div className={styles.actions}>
            <Button
              ref={filtersButtonRef}
              icon={<Filter16Regular />}
              appearance={filtersOpen ? 'primary' : 'secondary'}
              aria-expanded={filtersOpen}
              aria-controls={filtersOpen ? filtersPanelId : undefined}
              onClick={() => setFiltersOpen((open) => !open)}
            >
              {filterCount > 0
                ? t('activityAnalysis.filters.buttonWithCount', { count: formatNumber(filterCount) })
                : t('activityAnalysis.filters.button')}
            </Button>
            <PrintButton tooltip={t('activityAnalysis.page.printTooltip')} />
          </div>
        </div>

        <div className={styles.printOnly} data-print="only">
          <Text size={200} block>
            {t('activityAnalysis.print.scope', { period: periodSummary, metrics: formatList(metricLabels) })}
          </Text>
          <Text size={200} block>
            {filterWords.length > 0
              ? t('activityAnalysis.print.filters', { filters: formatList(filterWords) })
              : t('activityAnalysis.print.noFilters')}
          </Text>
        </div>

        {(report || loading) && (
          <div className={styles.headline}>
            {report && (
              <>
                <Text className={styles.figure}>
                  {t(plural(report.matchingPeople, 'activityAnalysis.headline.matching.one', 'activityAnalysis.headline.matching.other'), {
                    count: formatNumber(report.matchingPeople),
                  })}
                </Text>
                {!report.suppressed && (
                  <>
                    <span className={styles.separator} aria-hidden="true">
                      ·
                    </span>
                    <Text className={styles.figure}>
                      {t('activityAnalysis.headline.active', { count: formatNumber(report.activePeople) })}
                    </Text>
                  </>
                )}
                {report.matchingPeople !== report.populationPeople && (
                  <Text size={300} className={styles.muted}>
                    {t('activityAnalysis.headline.ofPopulation', { count: formatNumber(report.populationPeople) })}
                  </Text>
                )}
                <InfoTip
                  title={t('activityAnalysis.headline.info.title')}
                  content={{
                    what: t('activityAnalysis.headline.info.what'),
                    source: t('activityAnalysis.headline.info.source'),
                  }}
                />
              </>
            )}
            {loading && (
              <span className={styles.updating} role="status">
                <Spinner size="extra-tiny" />
                <Text size={200}>{report ? t('activityAnalysis.page.updating') : t('activityAnalysis.page.loading')}</Text>
              </span>
            )}
          </div>
        )}

        <AppliedFiltersSummary
          applied={effectiveFilters}
          metrics={metrics}
          licences={lastGood?.licences ?? []}
          echoNames={lastGood?.userFilter?.dimensionNames}
        />
      </Card>

      {filtersOpen && (
        <FiltersPanel
          id={filtersPanelId}
          draft={draft}
          onDraftChange={setDraft}
          applied={effectiveFilters}
          onApply={(next) => {
            setApplied(next);
            // Applying is the end of the edit: the panel makes way for the figures it changed, and the
            // focus goes back to the button that opened it.
            setFiltersOpen(false);
            filtersButtonRef.current?.focus();
          }}
          groups={groups}
          order={order}
          metrics={metrics}
          licences={lastGood?.licences ?? []}
          rangeMaxima={lastGood?.rangeMaxima ?? []}
          echoNames={lastGood?.userFilter?.dimensionNames}
          matchingPeople={lastGood ? lastGood.matchingPeople : null}
        />
      )}

      <div className={styles.bodyContainer}>
        <div className={styles.body}>
          <aside className={styles.slicer}>
            <MetricSlicer
              groups={groups}
              order={order}
              selected={selected}
              onChange={setSelected}
              unavailableCount={unavailableCount}
            />
          </aside>

          <div className={styles.main}>
            {selected.length === 0 && (
              <Card className={styles.empty}>
                <Text>{t('activityAnalysis.page.noMetricSelected')}</Text>
              </Card>
            )}

            {reportError && (
              <MessageBar intent="error">
                <MessageBarBody>{reportError}</MessageBarBody>
                <MessageBarActions>
                  <Button size="small" onClick={() => setAttempt((a) => a + 1)}>
                    {t('activityAnalysis.page.retry')}
                  </Button>
                </MessageBarActions>
              </MessageBar>
            )}

            {!report && loading && (
              <div className={styles.center}>
                <Spinner size="large" label={t('activityAnalysis.page.loading')} />
              </div>
            )}

            {report?.suppressed && (
              <MessageBar intent="warning" layout="multiline">
                <MessageBarBody>
                  <MessageBarTitle>{t('activityAnalysis.suppressed.title')}</MessageBarTitle>
                  {t('activityAnalysis.suppressed.body')}
                </MessageBarBody>
              </MessageBar>
            )}

            {report && !report.suppressed && reportQuery && (
              <div className={mergeClasses(styles.figures, loading ? styles.stale : undefined)} aria-busy={loading}>
                <ActivePeopleCharts byCompany={report.byCompany} byDepartment={report.byDepartment} />
                <MetricsByWeekChart report={report} metrics={metrics} />
              </div>
            )}
          </div>
        </div>
      </div>

      {report && !report.suppressed && reportQuery && (
        <div className={mergeClasses(styles.figures, styles.below, loading ? styles.stale : undefined)} aria-busy={loading}>
          <ResultsMatrix report={report} query={reportQuery} metrics={metrics} />
          {seePii ? (
            <TopPeopleCard query={reportQuery} metrics={metrics} matchingPeople={report.matchingPeople} />
          ) : (
            <PiiHiddenNote />
          )}
        </div>
      )}

      <Text size={200} className={styles.source}>
        {t('activityAnalysis.page.source')}
      </Text>
    </>
  );
}
