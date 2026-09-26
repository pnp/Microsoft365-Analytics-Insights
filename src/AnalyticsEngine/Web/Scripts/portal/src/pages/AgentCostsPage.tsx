import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Body1,
  Button,
  Card,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  Tab,
  TabList,
  Text,
  Title3,
  makeStyles,
  tokens,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, ArrowDownload16Regular } from '@fluentui/react-icons';

import { useT, useTNode, type TFunction, type TranslationKey } from '../i18n';
import {
  fetchAllDetailRows,
  fetchAvailability,
  fetchAzureBreakdown,
  fetchBreakdown,
  fetchDetail,
  fetchFilterOptions,
  fetchSummary,
  fetchTopUsers,
  fetchTrend,
} from '../api/agentCostsApi';
import type {
  AgentCostAvailability,
  AgentCostBreakdownRow,
  AgentCostDailyPoint,
  AgentCostDetailPage,
  AgentCostFilterOptions,
  AgentCostFilters,
  AgentCostSummary,
  AgentCostUserRow,
  AzureCostBreakdownRow,
  AzureDimension,
  CopilotCapacitySnapshot,
  CreditDimension,
} from '../types/agentCosts';
import Spinner from '../components/Spinner';
import CategoryBarChart from '../components/charts/CategoryBarChart';
import {
  AZURE_DIMENSIONS,
  CREDIT_DIMENSIONS,
  DASH,
  NOT_REPORTED,
  detailRowsToCsv,
  formatCount,
  formatCredits,
  formatDateTime,
  formatDay,
  formatMoney,
  formatQuantity,
  capacityConsumptionTypeLabel,
  capacityStatusLabel,
  harnessLabel,
  saveCsv,
  windowOfDays,
} from '../components/agentCosts/agentCostShared';

const WINDOW_OPTIONS: { value: number; labelKey: TranslationKey }[] = [
  { value: 7, labelKey: 'agentCosts.window.last7Days' },
  { value: 30, labelKey: 'agentCosts.window.last30Days' },
  { value: 60, labelKey: 'agentCosts.window.last60Days' },
  { value: 90, labelKey: 'agentCosts.window.last90Days' },
  { value: 180, labelKey: 'agentCosts.window.last180Days' },
];

const PAGE_SIZE = 50;

// Fixed messages, so an effect can recognise the error IT set and clear it once its own request succeeds.
// Without that, a panel that failed and then recovered (for example paging after a timeout) keeps showing a
// stale banner over fresh, correct data.
const BREAKDOWN_ERROR = 'agentCosts.error.breakdown';
const DETAIL_ERROR = 'agentCosts.error.detail';
const AZURE_ERROR = 'agentCosts.error.azure';

export function agentCostDimensionLabel(
  label: string | null | undefined,
  key: string | null | undefined,
  t: TFunction,
): string {
  return label || key || t('agentCosts.state.notReported');
}

type DetailSort = 'credits' | 'date' | 'agent' | 'feature' | 'users';

const useStyles = makeStyles({
  header: { display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' },
  controls: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  intro: { marginTop: '8px' },
  messages: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '12px' },
  cards: { display: 'flex', flexDirection: 'column', gap: '16px', marginTop: '16px' },
  kpiGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: '12px' },
  kpi: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
  },
  kpiValue: { fontSize: tokens.fontSizeHero700, fontWeight: tokens.fontWeightSemibold, lineHeight: '1.15' },
  kpiLabel: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  kpiHint: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 },
  filterBar: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: '10px' },
  filterField: { display: 'flex', flexDirection: 'column', gap: '4px' },
  filterLabel: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  cardHead: { display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  body: { marginTop: '12px' },
  wrap: { overflowX: 'auto' },
  table: { width: '100%', borderCollapse: 'collapse', fontSize: tokens.fontSizeBase200 },
  th: {
    textAlign: 'left',
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke2,
    color: tokens.colorNeutralForeground3,
    fontWeight: tokens.fontWeightSemibold,
    whiteSpace: 'nowrap',
  },
  thNumeric: { textAlign: 'right' },
  thSortable: { cursor: 'pointer', userSelect: 'none' },
  td: {
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
    fontSize: tokens.fontSizeBase200,
    verticalAlign: 'top',
  },
  tdNumeric: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  tdMuted: { color: tokens.colorNeutralForeground3 },
  pager: { display: 'flex', alignItems: 'center', gap: '10px', marginTop: '10px', flexWrap: 'wrap' },
  trendRow: { display: 'flex', alignItems: 'flex-end', gap: '2px', height: '120px', marginTop: '8px' },
  trendBar: { flex: '1 1 auto', minWidth: '2px', borderRadius: '2px 2px 0 0', backgroundColor: tokens.colorBrandBackground },
  empty: { color: tokens.colorNeutralForeground3, padding: '24px 0', textAlign: 'center' },
});


export function importFailureWarning(t: TFunction, kind: 'copilotStudio' | 'azure'): string {
  return t(kind === 'copilotStudio'
    ? 'agentCosts.warning.copilotStudioImportFailing'
    : 'agentCosts.warning.azureCostImportFailing');
}

/**
 * A failing import's warning bar. The label is the portal's own wording, so it is translated. The detail is
 * the importer's last error: diagnostic text, shown exactly as the importer wrote it, as `availabilityMessages`
 * already does with `{error}`. It must stay, because once some figures exist this bar is the only place the page
 * says WHY the import is failing - `availabilityMessages` quotes the error only while there is no data at all.
 */
export function ImportFailureBar({ kind, error }: { kind: 'copilotStudio' | 'azure'; error: string }) {
  const t = useT();
  return (
    <MessageBar intent="warning">
      <MessageBarBody>
        <strong>{importFailureWarning(t, kind)}</strong> {error}
      </MessageBarBody>
    </MessageBar>
  );
}

/**
 * "X of Y used (Month to date)" under the capacity tile. The consumption type is Power Platform's stable
 * identifier ("MonthToDate"), so it is labelled rather than shown raw.
 */
export function CapacityUsedHint({ capacity }: { capacity: CopilotCapacitySnapshot }) {
  const t = useT();
  return (
    <>
      {t('agentCosts.capacity.usedOfEntitled', {
        consumed: formatCredits(capacity.consumed),
        entitled: formatCredits(capacity.entitled),
      })}
      {capacity.consumptionType ? ` (${capacityConsumptionTypeLabel(capacity.consumptionType, t)})` : ''}
    </>
  );
}

function availabilityMessages(availability: AgentCostAvailability, t: TFunction): string[] {
  const messages: string[] = [];
  if (!availability.copilotStudioCreditsEnabled && !availability.azureCostsEnabled) {
    messages.push(t('agentCosts.availability.message.noImports'));
  }

  if (availability.copilotStudioCreditsEnabled && !availability.hasCopilotStudioCreditData) {
    if (availability.copilotStudioCreditsLastError) {
      messages.push(t('agentCosts.availability.message.copilotImportFailing', { error: availability.copilotStudioCreditsLastError }));
    } else if (availability.copilotStudioCreditsHasRunCleanly) {
      messages.push(t('agentCosts.availability.message.copilotNoUsage'));
    } else {
      messages.push(t('agentCosts.availability.message.copilotNotStoredYet'));
    }
  }

  if (availability.azureCostsEnabled && !availability.hasAzureCostData) {
    if (availability.azureCostsLastError) {
      messages.push(t('agentCosts.availability.message.azureImportFailing', { error: availability.azureCostsLastError }));
    } else if (availability.azureCostsHaveRunCleanly) {
      messages.push(t('agentCosts.availability.message.azureNoSpend'));
    } else {
      messages.push(t('agentCosts.availability.message.azureNotStoredYet'));
    }
  }

  if (availability.copilotStudioCreditsEnabled
    && !availability.copilotStudioCreditsLastError
    && availability.perUserCreditsLastError) {
    messages.push(t('agentCosts.availability.message.perUserNotUpdating', { error: availability.perUserCreditsLastError }));
  }

  if (availability.copilotStudioCreditsEnabled
    && !availability.copilotStudioCreditsLastError
    && availability.capacityLastError) {
    messages.push(t('agentCosts.availability.message.capacityNotUpdating', { error: availability.capacityLastError }));
  }

  messages.push(t('agentCosts.availability.message.creditEndpointMismatch'));
  messages.push(t('agentCosts.availability.message.azureNoPeople'));
  messages.push(t('agentCosts.availability.message.azureEstimates'));
  return messages;
}

/** Reads a filter value from a Select/Input, mapping the "any" sentinel back to undefined. */
function orUndefined(value: string): string | undefined {
  return value === '' ? undefined : value;
}

/**
 * The display text for a pivot value. Only the harness dimension stores identifiers rather than
 * human text, so only it needs translating; everything else is already what Microsoft reported.
 */
function labelFor(dimension: CreditDimension, value: string | null | undefined, t: TFunction): string {
  if (dimension === 'harness') return harnessLabel(value, t);
  return value || t('agentCosts.state.notReported');
}

export default function AgentCostsPage() {
  const styles = useStyles();
  const t = useT();
  const tNode = useTNode();

  const [days, setDays] = useState(30);
  const [availability, setAvailability] = useState<AgentCostAvailability | null>(null);
  const [options, setOptions] = useState<AgentCostFilterOptions | null>(null);
  const [summary, setSummary] = useState<AgentCostSummary | null>(null);
  const [trend, setTrend] = useState<AgentCostDailyPoint[]>([]);
  const [breakdown, setBreakdown] = useState<AgentCostBreakdownRow[]>([]);
  const [detail, setDetail] = useState<AgentCostDetailPage | null>(null);
  const [azure, setAzure] = useState<AzureCostBreakdownRow[]>([]);
  const [topUsers, setTopUsers] = useState<AgentCostUserRow[]>([]);

  const [dimension, setDimension] = useState<CreditDimension>('agent');
  const [azureDimension, setAzureDimension] = useState<AzureDimension>('meter');

  const [agentId, setAgentId] = useState('');
  const [environmentId, setEnvironmentId] = useState('');
  const [harness, setHarness] = useState('');
  const [feature, setFeature] = useState('');
  const [search, setSearch] = useState('');

  const [page, setPage] = useState(1);
  const [sort, setSort] = useState<DetailSort>('credits');
  const [direction, setDirection] = useState<'asc' | 'desc'>('desc');

  const [loading, setLoading] = useState(true);
  const [detailLoading, setDetailLoading] = useState(false);
  const [exporting, setExporting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [reloadToken, setReloadToken] = useState(0);

  // Debounced so typing in the agent-name box does not fire a query per keystroke.
  const [debouncedSearch, setDebouncedSearch] = useState('');
  useEffect(() => {
    const handle = window.setTimeout(() => setDebouncedSearch(search), 350);
    return () => window.clearTimeout(handle);
  }, [search]);

  const filters: AgentCostFilters = useMemo(() => {
    const { from, to } = windowOfDays(days);
    return {
      from,
      to,
      agentId: orUndefined(agentId),
      environmentId: orUndefined(environmentId),
      harness: orUndefined(harness),
      feature: orUndefined(feature),
      search: debouncedSearch.trim() || undefined,
    };
  }, [days, agentId, environmentId, harness, feature, debouncedSearch]);

  // The effects below key off this string rather than the `filters` object. A `useMemo` object is a new
  // reference whenever any input changes, and putting it in a dependency array alongside its own derived
  // values would re-run the effect twice for one user action.
  const filterSignature = JSON.stringify(filters);

  const lastSignature = useRef(filterSignature);
  useEffect(() => {
    if (lastSignature.current !== filterSignature) {
      lastSignature.current = filterSignature;
      // Any filter change invalidates the current page - staying on page 7 of a result set that now has
      // two pages would show an empty table.
      setPage(1);
    }
  }, [filterSignature]);

  // Loading is deliberately split into four effects rather than one. Paging or re-sorting the detail grid
  // must not re-run the summary, the trend, the pivot, the filter options and the Azure breakdown as well -
  // that turned a single "next page" click into seven queries against tables that grow by thousands of rows
  // a day.
  useEffect(() => {
    const controller = new AbortController();
    let cancelled = false;

    (async () => {
      setLoading(true);
      setError(null);
      try {
        const [availabilityResult, optionsResult, summaryResult, trendResult, usersResult] = await Promise.all([
          fetchAvailability(controller.signal),
          fetchFilterOptions(filters, controller.signal),
          fetchSummary(filters, controller.signal),
          fetchTrend(filters, controller.signal),
          fetchTopUsers(filters, 20, controller.signal),
        ]);

        if (cancelled) return;
        setAvailability(availabilityResult);
        setOptions(optionsResult);
        setSummary(summaryResult);
        setTrend(trendResult);
        setTopUsers(usersResult);
      } catch (ex) {
        if (cancelled || controller.signal.aborted) return;
        setError(ex instanceof Error ? ex.message : t('agentCosts.error.summary'));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterSignature, reloadToken]);

  useEffect(() => {
    const controller = new AbortController();
    let cancelled = false;

    (async () => {
      try {
        const result = await fetchBreakdown(filters, dimension, 20, controller.signal);
        if (!cancelled) {
          setBreakdown(result);
          setError((e) => (e === t(BREAKDOWN_ERROR) ? null : e));
        }
      } catch (ex) {
        if (cancelled || controller.signal.aborted) return;
        setError(t(BREAKDOWN_ERROR));
      }
    })();

    return () => {
      cancelled = true;
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterSignature, dimension, reloadToken]);

  useEffect(() => {
    const controller = new AbortController();
    let cancelled = false;

    (async () => {
      setDetailLoading(true);
      try {
        const result = await fetchDetail(
          { ...filters, page, pageSize: PAGE_SIZE, sort, direction },
          controller.signal,
        );
        if (!cancelled) {
          setDetail(result);
          setError((e) => (e === t(DETAIL_ERROR) ? null : e));
        }
      } catch (ex) {
        if (cancelled || controller.signal.aborted) return;
        setError(t(DETAIL_ERROR));
      } finally {
        if (!cancelled) setDetailLoading(false);
      }
    })();

    return () => {
      cancelled = true;
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterSignature, page, sort, direction, reloadToken]);

  useEffect(() => {
    const controller = new AbortController();
    let cancelled = false;

    (async () => {
      try {
        const result = await fetchAzureBreakdown(filters, azureDimension, 20, controller.signal);
        if (!cancelled) {
          setAzure(result);
          setError((e) => (e === t(AZURE_ERROR) ? null : e));
        }
      } catch (ex) {
        if (cancelled || controller.signal.aborted) return;
        setError(t(AZURE_ERROR));
      }
    })();

    return () => {
      cancelled = true;
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filterSignature, azureDimension, reloadToken]);

  const toggleSort = useCallback(
    (column: DetailSort) => {
      if (sort === column) {
        setDirection((d) => (d === 'asc' ? 'desc' : 'asc'));
      } else {
        setSort(column);
        setDirection('desc');
      }
      setPage(1);
    },
    [sort],
  );

  const categories = useMemo(
    () => breakdown.map((r) => ({ label: labelFor(dimension, r.label ?? r.key, t), value: r.billedCredits })),
    [breakdown, dimension, t],
  );

  // The share denominator is the TRUE window total from the summary, not the sum of the rows on screen.
  // The pivot is capped at the top 20, so dividing by the visible sum would make every row's share add up
  // to 100% while omitting everything below the cut - overstating each one.
  const shareTotal = summary?.billedCredits ?? 0;
  const breakdownShown = useMemo(() => breakdown.reduce((sum, r) => sum + r.billedCredits, 0), [breakdown]);
  const hasHiddenRows = shareTotal > 0 && breakdownShown < shareTotal - 0.0000005;

  const trendMax = useMemo(() => Math.max(...trend.map((p) => p.billedCredits), 1), [trend]);

  /**
   * Only the Azure dimensions that actually have data. Cost Management allows two group-by clauses per
   * query, so whichever dimensions are not grouped are never populated and a pivot on them could only ever
   * say "Not reported". Falls back to the full list before availability has loaded, so the control is never
   * empty on first paint.
   */
  const availableAzureDimensions = useMemo(() => {
    const withData = availability?.azureDimensionsWithData;
    if (!withData || withData.length === 0) return AZURE_DIMENSIONS;
    return AZURE_DIMENSIONS.filter((d) => withData.includes(d.key));
  }, [availability]);

  /**
   * The same guard for the credit pivots. Microsoft fills the per-agent metadata sparsely, so offering a
   * pivot it never populates reads as "this tenant did none of that" rather than "this was never reported".
   */
  const availableCreditDimensions = useMemo(() => {
    const withData = availability?.creditDimensionsWithData;
    if (!withData || withData.length === 0) return CREDIT_DIMENSIONS;
    return CREDIT_DIMENSIONS.filter((d) => withData.includes(d.key));
  }, [availability]);

  // Keep the selection valid: if the chosen dimension is not one of the populated ones, move to the first
  // that is, rather than showing an empty table for a pivot the data cannot answer.
  useEffect(() => {
    if (availableAzureDimensions.length === 0) return;
    if (!availableAzureDimensions.some((d) => d.key === azureDimension)) {
      setAzureDimension(availableAzureDimensions[0].key);
    }
  }, [availableAzureDimensions, azureDimension]);

  // The per-user total is the sum of the rows SHOWN, and the caption says so. Unlike the pivot there is no
  // uncapped total to divide by: the per-user endpoint is a separate report, so the summary's per-agent
  // total is not its denominator and using it would understate every share.
  const userTotal = useMemo(() => topUsers.reduce((sum, u) => sum + u.billedCredits, 0), [topUsers]);

  const totalPages = detail ? Math.max(1, Math.ceil(detail.totalRows / detail.pageSize)) : 1;
  const activeDimension = CREDIT_DIMENSIONS.find((d) => d.key === dimension);

  const exportPage = useCallback(() => {
    if (!detail) return;
    saveCsv(
      detailRowsToCsv(detail.rows, t),
      t('agentCosts.export.pageFilename', { from: filters.from, to: filters.to, page: detail.page }),
    );
  }, [detail, filters.from, filters.to, t]);

  const exportAll = useCallback(async () => {
    setExporting(true);
    setNotice(null);
    try {
      const result = await fetchAllDetailRows(filters, sort, direction);
      saveCsv(
        detailRowsToCsv(result.rows, t),
        t('agentCosts.export.filteredFilename', { from: filters.from, to: filters.to }),
      );
      setNotice(
        result.truncated
          ? t('agentCosts.notice.exportedTruncated', {
              rows: formatCount(result.rows.length),
              totalRows: formatCount(result.totalRows),
            })
          : t('agentCosts.notice.exportedRows', { rows: formatCount(result.rows.length) }),
      );
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : t('agentCosts.error.export'));
    } finally {
      setExporting(false);
    }
  }, [filters, sort, direction, t]);

  return (
    <>
      <div className={styles.header}>
        <div>
          <Title3 block>{t('agentCosts.title')}</Title3>
          <Body1 block className={styles.intro}>
            {t('agentCosts.intro')}
          </Body1>
        </div>
        <div className={styles.controls}>
          <Select value={String(days)} onChange={(_: unknown, d: { value: string }) => setDays(Number(d.value))} disabled={loading}>
            {WINDOW_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {t(o.labelKey)}
              </option>
            ))}
          </Select>
          <Button
            appearance="subtle"
            icon={<ArrowClockwise16Regular />}
            disabled={loading}
            onClick={() => setReloadToken((t) => t + 1)}
          >
            {t('common.action.refresh')}
          </Button>
        </div>
      </div>

      {error && (
        <div className={styles.messages}>
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        </div>
      )}

      {notice && (
        <div className={styles.messages}>
          <MessageBar intent="success">
            <MessageBarBody>{notice}</MessageBarBody>
          </MessageBar>
        </div>
      )}

      {availability && (
        <div className={styles.messages}>
          {availability.copilotStudioCreditsEnabled && availability.copilotStudioCreditsLastError && (
            <ImportFailureBar kind="copilotStudio" error={availability.copilotStudioCreditsLastError} />
          )}
          {availability.azureCostsEnabled && availability.azureCostsLastError && (
            <ImportFailureBar kind="azure" error={availability.azureCostsLastError} />
          )}
          {availabilityMessages(availability, t).map((m) => (
            <MessageBar key={m} intent="info">
              <MessageBarBody>{m}</MessageBarBody>
            </MessageBar>
          ))}
        </div>
      )}

      {loading && !summary ? (
        <div className={styles.body}>
          <Spinner size={32} label={t('agentCosts.loading')} />
        </div>
      ) : (
        <div className={styles.cards}>
          {/* Headline figures + entitlement headroom. */}
          <Card>
            <div className={styles.cardHead}>
              <Text weight="semibold">{t('agentCosts.spend.title')}</Text>
              {availability?.copilotStudioCreditsLastImportUtc && (
                <Text className={styles.muted} size={200}>
                  {t('agentCosts.spend.importedUtc', { when: formatDateTime(availability.copilotStudioCreditsLastImportUtc) })}
                </Text>
              )}
            </div>
            <div className={`${styles.kpiGrid} ${styles.body}`}>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCredits(summary?.billedCredits ?? 0)}</span>
                <span className={styles.kpiLabel}>{t('agentCosts.kpi.creditsBilled')}</span>
              </div>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCredits(summary?.nonBilledCredits ?? 0)}</span>
                <span className={styles.kpiLabel}>{t('agentCosts.kpi.creditsNotCharged')}</span>
                <span className={styles.kpiHint}>{t('agentCosts.kpi.creditsNotCharged.hint')}</span>
              </div>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCount(summary?.distinctAgents ?? 0)}</span>
                <span className={styles.kpiLabel}>{t('agentCosts.kpi.agentsWithSpend')}</span>
              </div>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCount(summary?.distinctEnvironments ?? 0)}</span>
                <span className={styles.kpiLabel}>{t('agentCosts.kpi.environments')}</span>
              </div>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCount(summary?.peakDistinctUsersOnASlice)}</span>
                <span className={styles.kpiLabel}>{t('agentCosts.kpi.busiestSlice')}</span>
                <span className={styles.kpiHint}>{t('agentCosts.kpi.busiestSlice.hint')}</span>
              </div>
              {summary?.unclassifiedHarnessCredits ? (
                <div className={styles.kpi}>
                  <span className={styles.kpiValue}>{formatCredits(summary.unclassifiedHarnessCredits)}</span>
                  <span className={styles.kpiLabel}>{t('agentCosts.kpi.unclassifiedHarness')}</span>
                  <span className={styles.kpiHint}>{t('agentCosts.kpi.unclassifiedHarness.hint')}</span>
                </div>
              ) : null}
            </div>

            {summary?.capacity && (
              <div className={`${styles.kpiGrid} ${styles.body}`}>
                <div className={styles.kpi}>
                  <span className={styles.kpiValue}>{formatCredits(summary.capacity.available)}</span>
                  <span className={styles.kpiLabel}>{t('agentCosts.capacity.availableNow')}</span>
                  <span className={styles.kpiHint}>
                    <CapacityUsedHint capacity={summary.capacity} />
                  </span>
                </div>
                <div className={styles.kpi}>
                  <span className={styles.kpiValue}>{capacityStatusLabel(summary.capacity.status, t)}</span>
                  <span className={styles.kpiLabel}>{t('agentCosts.capacity.status')}</span>
                  <span className={styles.kpiHint}>
                    {t('agentCosts.capacity.asAt', { day: formatDay(summary.capacity.consumptionAsOf ?? summary.capacity.snapshotUtc) })}
                  </span>
                </div>
                {summary.capacity.payAsYouGoConsumed != null && (
                  <div className={styles.kpi}>
                    <span className={styles.kpiValue}>{formatCredits(summary.capacity.payAsYouGoConsumed)}</span>
                    <span className={styles.kpiLabel}>{t('agentCosts.capacity.payAsYouGo')}</span>
                    <span className={styles.kpiHint}>{t('agentCosts.capacity.payAsYouGo.hint')}</span>
                  </div>
                )}
              </div>
            )}

            {summary && summary.azureCost.length > 0 && (
              <div className={`${styles.kpiGrid} ${styles.body}`}>
                {summary.azureCost.map((c) => (
                  <div className={styles.kpi} key={c.currency ?? 'unknown'}>
                    <span className={styles.kpiValue}>{formatMoney(c.cost, c.currency)}</span>
                    <span className={styles.kpiLabel}>{t('agentCosts.azureSpend.title')}</span>
                    {c.quantity !== null && c.quantity !== undefined && (
                      <span className={styles.kpiHint}>
                        {t('agentCosts.azureSpend.meteredUnitsBilled', { quantity: formatCredits(c.quantity) })}
                      </span>
                    )}
                    {c.includesEstimates && <span className={styles.kpiHint}>{t('agentCosts.azureSpend.includesEstimates')}</span>}
                  </div>
                ))}
              </div>
            )}
          </Card>

          {/* Daily trend. Deliberately a plain bar strip: the grain is daily, and the shared
              time-series chart is built around a weekly spine. */}
          <Card>
            <Text weight="semibold">{t('agentCosts.trend.title')}</Text>
            {trend.length === 0 ? (
              <div className={styles.empty}>{t('agentCosts.trend.empty')}</div>
            ) : (
              <>
                <div className={styles.trendRow}>
                  {trend.map((p) => (
                    <div
                      key={p.date}
                      className={styles.trendBar}
                      style={{ height: `${Math.max(2, (p.billedCredits / trendMax) * 100)}%` }}
                      title={t('agentCosts.trend.barTitle', { day: formatDay(p.date), credits: formatCredits(p.billedCredits) })}
                    />
                  ))}
                </div>
                <Text className={styles.muted} size={200}>
                  {t('agentCosts.trend.caption', {
                    from: formatDay(trend[0].date),
                    to: formatDay(trend[trend.length - 1].date),
                    peak: formatCredits(trendMax),
                  })}
                </Text>
              </>
            )}
          </Card>

          {/* Filters. */}
          <Card>
            <Text weight="semibold">{t('agentCosts.filters.title')}</Text>
            <div className={`${styles.filterBar} ${styles.body}`}>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>{t('agentCosts.filter.agent')}</span>
                <Select value={agentId} onChange={(_: unknown, d: { value: string }) => setAgentId(d.value)} disabled={loading}>
                  <option value="">{t('agentCosts.filter.agent.all')}</option>
                  {options?.agents.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.label}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>{t('agentCosts.filter.environment')}</span>
                <Select value={environmentId} onChange={(_: unknown, d: { value: string }) => setEnvironmentId(d.value)} disabled={loading}>
                  <option value="">{t('agentCosts.filter.environment.all')}</option>
                  {options?.environments.map((e) => (
                    <option key={e.id} value={e.id}>
                      {e.label}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>{t('agentCosts.filter.harness')}</span>
                <Select value={harness} onChange={(_: unknown, d: { value: string }) => setHarness(d.value)} disabled={loading}>
                  <option value="">{t('agentCosts.filter.harness.all')}</option>
                  {options?.harnesses.map((h) => (
                    <option key={h} value={h}>
                      {harnessLabel(h, t)}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>{t('agentCosts.filter.billingFeature')}</span>
                <Select value={feature} onChange={(_: unknown, d: { value: string }) => setFeature(d.value)} disabled={loading}>
                  <option value="">{t('agentCosts.filter.billingFeature.all')}</option>
                  {options?.features.map((f) => (
                    <option key={f} value={f}>
                      {f}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>{t('agentCosts.filter.searchAgentName')}</span>
                <Input value={search} onChange={(_: unknown, d: { value: string }) => setSearch(d.value)} placeholder={t('agentCosts.filter.searchAgentName.placeholder')} />
              </label>
            </div>
          </Card>

          {/* The pivot. */}
          <Card>
            <div className={styles.cardHead}>
              <div>
                <Text weight="semibold">{t('agentCosts.breakdown.title')}</Text>
                {activeDimension && (
                  <div>
                    <Text className={styles.muted} size={200}>
                      {t(activeDimension.hintKey)}
                    </Text>
                  </div>
                )}
              </div>
            </div>
            <TabList
              selectedValue={dimension}
              onTabSelect={((_: unknown, d: { value: unknown }) => setDimension(d.value as CreditDimension)) as SelectTabEventHandler}
            >
              {availableCreditDimensions.map((d) => (
                <Tab key={d.key} value={d.key}>
                  {t(d.labelKey)}
                </Tab>
              ))}
            </TabList>

            <div className={styles.body}>
              <CategoryBarChart categories={categories} valueLabel={t('agentCosts.unit.credits')} />
            </div>

            {breakdown.length > 0 && (
              <div className={`${styles.wrap} ${styles.body}`}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th className={styles.th}>{activeDimension ? t(activeDimension.labelKey) : t('agentCosts.table.value')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.creditsBilled')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.share')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.notCharged')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.daysActive')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.busiestSlicePeople')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {breakdown.map((r) => (
                      <tr key={r.key ?? NOT_REPORTED}>
                        <td className={`${styles.td} ${r.key ? '' : styles.tdMuted}`}>{labelFor(dimension, r.label ?? r.key, t)}</td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCredits(r.billedCredits)}</td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>
                          {shareTotal > 0 ? `${Math.round((r.billedCredits / shareTotal) * 1000) / 10}%` : DASH}
                        </td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCredits(r.nonBilledCredits)}</td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(r.activeDays)}</td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(r.peakDistinctUsers)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                {hasHiddenRows && (
                  <Text className={styles.muted} size={200}>
                    {t('agentCosts.breakdown.hiddenRows', {
                      count: formatCount(breakdown.length),
                      credits: formatCredits(shareTotal),
                    })}
                  </Text>
                )}
              </div>
            )}
          </Card>

          {/* The granular rows - the deepest view the source supports. */}
          <Card>
            <div className={styles.cardHead}>
              <div>
                <Text weight="semibold">{t('agentCosts.detail.title')}</Text>
                <div>
                  <Text className={styles.muted} size={200}>
                    {t('agentCosts.detail.description')}
                  </Text>
                </div>
              </div>
              <div className={styles.controls}>
                <Button
                  appearance="subtle"
                  icon={<ArrowDownload16Regular />}
                  disabled={detailLoading || !detail || detail.rows.length === 0}
                  onClick={exportPage}
                >
                  {t('agentCosts.detail.exportPage')}
                </Button>
                <Button
                  appearance="primary"
                  icon={<ArrowDownload16Regular />}
                  disabled={exporting || detailLoading || !detail || detail.totalRows === 0}
                  onClick={exportAll}
                >
                  {exporting ? t('agentCosts.detail.exporting') : t('agentCosts.detail.exportAllRows', { rows: detail ? formatCount(detail.totalRows) : '' })}
                </Button>
              </div>
            </div>

            {!detail || detail.rows.length === 0 ? (
              <div className={styles.empty}>{t('agentCosts.detail.empty')}</div>
            ) : (
              <>
                <div className={`${styles.wrap} ${styles.body}`}>
                  <table className={styles.table}>
                    <thead>
                      <tr>
                        <th className={`${styles.th} ${styles.thSortable}`} onClick={() => toggleSort('date')}>
                          {t('agentCosts.table.day')}{sort === 'date' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                        <th className={`${styles.th} ${styles.thSortable}`} onClick={() => toggleSort('agent')}>
                          {t('agentCosts.table.agent')}{sort === 'agent' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                        <th className={styles.th}>{t('agentCosts.table.environment')}</th>
                        <th className={styles.th}>{t('agentCosts.table.harness')}</th>
                        <th className={`${styles.th} ${styles.thSortable}`} onClick={() => toggleSort('feature')}>
                          {t('agentCosts.table.billingFeature')}{sort === 'feature' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                        <th
                          className={`${styles.th} ${styles.thNumeric} ${styles.thSortable}`}
                          onClick={() => toggleSort('credits')}
                        >
                          {t('agentCosts.table.credits')}{sort === 'credits' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                        <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.notCharged')}</th>
                        <th
                          className={`${styles.th} ${styles.thNumeric} ${styles.thSortable}`}
                          onClick={() => toggleSort('users')}
                        >
                          {t('agentCosts.table.people')}{sort === 'users' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {detail.rows.map((r, i) => (
                        <tr key={`${r.usageDate}-${r.agentId}-${r.featureName}-${i}`}>
                          <td className={styles.td}>{formatDay(r.usageDate)}</td>
                          <td className={styles.td}>{r.agentName || r.agentId || DASH}</td>
                          <td className={styles.td}>{r.environmentName || r.environmentId || DASH}</td>
                          <td className={styles.td}>{r.harness ? harnessLabel(r.harness, t) : DASH}</td>
                          <td className={styles.td}>{r.featureName || DASH}</td>
                          <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCredits(r.billedCredits)}</td>
                          <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCredits(r.nonBilledCredits)}</td>
                          <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(r.distinctUsers)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>

                <div className={styles.pager}>
                  <Button
                    appearance="secondary"
                    disabled={loading || detail.page <= 1}
                    onClick={() => setPage((p) => Math.max(1, p - 1))}
                  >
                    {t('agentCosts.pager.previous')}
                  </Button>
                  <Text size={200} className={styles.muted}>
                    {t('agentCosts.pager.pageOfBilledLines', {
                      page: formatCount(detail.page),
                      totalPages: formatCount(totalPages),
                      rows: formatCount(detail.totalRows),
                    })}
                  </Text>
                  <Button
                    appearance="secondary"
                    disabled={loading || detail.page >= totalPages}
                    onClick={() => setPage((p) => p + 1)}
                  >
                    {t('agentCosts.pager.next')}
                  </Button>
                </div>
              </>
            )}
          </Card>

          {/* Who is spending the credits. Copilot Studio only. */}
          <Card>
            <div className={styles.cardHead}>
              <div>
                <Text weight="semibold">{t('agentCosts.users.title')}</Text>
                <div>
                  <Text className={styles.muted} size={200}>
                    {tNode('agentCosts.users.description', {
                      strong: <strong>{t('agentCosts.users.descriptionStrong')}</strong>,
                    })}
                  </Text>
                </div>
              </div>
            </div>

            {topUsers.length === 0 ? (
              <div className={styles.empty}>
                {!availability?.copilotStudioCreditsEnabled
                  ? t('agentCosts.users.empty.importOff')
                  : availability?.hasPerUserCreditData
                    ? t('agentCosts.users.empty.noUsage')
                    : t('agentCosts.users.empty.noFigures')}
              </div>
            ) : (
              <div className={`${styles.wrap} ${styles.body}`}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th className={styles.th}>{t('agentCosts.table.person')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.creditsBilled')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.share')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.daysActive')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {topUsers.map((u) => (
                      <tr key={u.userId ?? u.entraObjectId ?? ''}>
                        <td className={styles.td}>
                          {u.userPrincipalName ?? (
                            <span title={u.entraObjectId ?? undefined}>
                              {t('agentCosts.users.unresolvedUser')}{u.entraObjectId ? ` (${u.entraObjectId})` : ''}
                            </span>
                          )}
                        </td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCredits(u.billedCredits)}</td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>
                          {userTotal > 0 ? `${Math.round((u.billedCredits / userTotal) * 1000) / 10}%` : DASH}
                        </td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatCount(u.activeDays)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <Text className={styles.muted} size={200}>
                  {t('agentCosts.users.shareCaption', {
                    credits: formatCredits(userTotal),
                    people: formatCount(topUsers.length),
                  })}
                </Text>
              </div>
            )}
          </Card>

          {/* Azure spend. */}
          <Card>
            <div className={styles.cardHead}>
              <div>
                <Text weight="semibold">{t('agentCosts.azureSpend.title')}</Text>
                <div>
                  <Text className={styles.muted} size={200}>
                    {t('agentCosts.azureSpend.description')}
                  </Text>
                </div>
              </div>
              <Select
                value={azureDimension}
                onChange={(_: unknown, d: { value: string }) => setAzureDimension(d.value as AzureDimension)}
                disabled={loading}
              >
                {availableAzureDimensions.map((d) => (
                  <option key={d.key} value={d.key}>
                    {t(d.labelKey)}
                  </option>
                ))}
              </Select>
            </div>

            {azure.length === 0 ? (
              <div className={styles.empty}>
                {availability?.azureCostsEnabled
                  ? t('agentCosts.azureSpend.empty.noCosts')
                  : t('agentCosts.azureSpend.empty.importOff')}
              </div>
            ) : (
              <div className={`${styles.wrap} ${styles.body}`}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th className={styles.th}>{t(AZURE_DIMENSIONS.find((d) => d.key === azureDimension)?.labelKey ?? 'agentCosts.table.value')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.cost')}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>{t('agentCosts.table.quantity')}</th>
                      <th className={styles.th}>{t('agentCosts.table.final')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {azure.map((r) => (
                      <tr key={`${r.key ?? NOT_REPORTED}-${r.currency ?? ''}`}>
                        <td className={`${styles.td} ${r.key ? '' : styles.tdMuted}`}>{agentCostDimensionLabel(r.label, r.key, t)}</td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatMoney(r.cost, r.currency)}</td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatQuantity(r.quantity)}</td>
                        <td className={`${styles.td} ${styles.tdMuted}`}>
                          {r.includesEstimates ? t('agentCosts.azureSpend.estimate') : t('agentCosts.azureSpend.final')}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Card>
        </div>
      )}
    </>
  );
}
