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
  harnessLabel,
  saveCsv,
  windowOfDays,
} from '../components/agentCosts/agentCostShared';

const WINDOW_OPTIONS = [
  { value: 7, label: 'Last 7 days' },
  { value: 30, label: 'Last 30 days' },
  { value: 60, label: 'Last 60 days' },
  { value: 90, label: 'Last 90 days' },
  { value: 180, label: 'Last 180 days' },
];

const PAGE_SIZE = 50;

// Fixed messages, so an effect can recognise the error IT set and clear it once its own request succeeds.
// Without that, a panel that failed and then recovered (for example paging after a timeout) keeps showing a
// stale banner over fresh, correct data.
const BREAKDOWN_ERROR = 'Could not load the credit breakdown. Try changing the period or refreshing.';
const DETAIL_ERROR = 'Could not load the billed lines. Try changing the period or refreshing.';
const AZURE_ERROR = 'Could not load the Azure cost breakdown. Try changing the period or refreshing.';

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

/** Reads a filter value from a Select/Input, mapping the "any" sentinel back to undefined. */
function orUndefined(value: string): string | undefined {
  return value === '' ? undefined : value;
}

/**
 * The display text for a pivot value. Only the harness dimension stores identifiers rather than
 * human text, so only it needs translating; everything else is already what Microsoft reported.
 */
function labelFor(dimension: CreditDimension, value: string | null | undefined): string {
  if (dimension === 'harness') return harnessLabel(value);
  return value || NOT_REPORTED;
}

export default function AgentCostsPage() {
  const styles = useStyles();

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
  const [model, setModel] = useState('');
  const [tool, setTool] = useState('');
  const [knowledge, setKnowledge] = useState('');
  const [channel, setChannel] = useState('');
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
      model: orUndefined(model),
      tool: orUndefined(tool),
      knowledge: orUndefined(knowledge),
      channel: orUndefined(channel),
      search: debouncedSearch.trim() || undefined,
    };
  }, [days, agentId, environmentId, harness, feature, model, tool, knowledge, channel, debouncedSearch]);

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
        setError(ex instanceof Error ? ex.message : 'Could not load the agent cost figures.');
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
          setError((e) => (e === BREAKDOWN_ERROR ? null : e));
        }
      } catch (ex) {
        if (cancelled || controller.signal.aborted) return;
        setError(BREAKDOWN_ERROR);
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
          setError((e) => (e === DETAIL_ERROR ? null : e));
        }
      } catch (ex) {
        if (cancelled || controller.signal.aborted) return;
        setError(DETAIL_ERROR);
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
          setError((e) => (e === AZURE_ERROR ? null : e));
        }
      } catch (ex) {
        if (cancelled || controller.signal.aborted) return;
        setError(AZURE_ERROR);
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
    () => breakdown.map((r) => ({ label: labelFor(dimension, r.label ?? r.key), value: r.billedCredits })),
    [breakdown, dimension],
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
    saveCsv(detailRowsToCsv(detail.rows), `agent-credits-${filters.from}-to-${filters.to}-page${detail.page}.csv`);
  }, [detail, filters.from, filters.to]);

  const exportAll = useCallback(async () => {
    setExporting(true);
    setNotice(null);
    try {
      const result = await fetchAllDetailRows(filters, sort, direction);
      saveCsv(detailRowsToCsv(result.rows), `agent-credits-${filters.from}-to-${filters.to}-filtered.csv`);
      setNotice(
        result.truncated
          ? `Exported the first ${result.rows.length.toLocaleString()} of ${result.totalRows.toLocaleString()} billed lines. Narrow the filters or shorten the period to export the rest.`
          : `Exported ${result.rows.length.toLocaleString()} billed line(s).`,
      );
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : 'Could not export the billed lines.');
    } finally {
      setExporting(false);
    }
  }, [filters, sort, direction]);

  return (
    <>
      <div className={styles.header}>
        <div>
          <Title3 block>Agent costs</Title3>
          <Body1 block className={styles.intro}>
            What Microsoft charged for your Copilot Studio agents, broken down as far as the billing data allows -
            by agent, environment, harness, billing feature, AI model, tool, knowledge source and, where Microsoft
            reports it, by person.
          </Body1>
        </div>
        <div className={styles.controls}>
          <Select value={String(days)} onChange={(_, d) => setDays(Number(d.value))} disabled={loading}>
            {WINDOW_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>
          <Button
            appearance="subtle"
            icon={<ArrowClockwise16Regular />}
            disabled={loading}
            onClick={() => setReloadToken((t) => t + 1)}
          >
            Refresh
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
            <MessageBar intent="warning">
              <MessageBarBody>
                <strong>Copilot Studio credit import is failing.</strong> {availability.copilotStudioCreditsLastError}
              </MessageBarBody>
            </MessageBar>
          )}
          {availability.azureCostsEnabled && availability.azureCostsLastError && (
            <MessageBar intent="warning">
              <MessageBarBody>
                <strong>Azure cost import is failing.</strong> {availability.azureCostsLastError}
              </MessageBarBody>
            </MessageBar>
          )}
          {availability.messages.map((m) => (
            <MessageBar key={m} intent="info">
              <MessageBarBody>{m}</MessageBarBody>
            </MessageBar>
          ))}
        </div>
      )}

      {loading && !summary ? (
        <div className={styles.body}>
          <Spinner size={32} label="Loading agent costs..." />
        </div>
      ) : (
        <div className={styles.cards}>
          {/* Headline figures + entitlement headroom. */}
          <Card>
            <div className={styles.cardHead}>
              <Text weight="semibold">Spend in the selected period</Text>
              {availability?.copilotStudioCreditsLastImportUtc && (
                <Text className={styles.muted} size={200}>
                  Imported {formatDateTime(availability.copilotStudioCreditsLastImportUtc)} UTC
                </Text>
              )}
            </div>
            <div className={`${styles.kpiGrid} ${styles.body}`}>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCredits(summary?.billedCredits ?? 0)}</span>
                <span className={styles.kpiLabel}>Copilot Credits billed</span>
              </div>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCredits(summary?.nonBilledCredits ?? 0)}</span>
                <span className={styles.kpiLabel}>Credits not charged</span>
                <span className={styles.kpiHint}>Used but covered by an allowance</span>
              </div>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCount(summary?.distinctAgents ?? 0)}</span>
                <span className={styles.kpiLabel}>Agents with spend</span>
              </div>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCount(summary?.distinctEnvironments ?? 0)}</span>
                <span className={styles.kpiLabel}>Environments</span>
              </div>
              <div className={styles.kpi}>
                <span className={styles.kpiValue}>{formatCount(summary?.peakDistinctUsersOnASlice)}</span>
                <span className={styles.kpiLabel}>Busiest slice</span>
                <span className={styles.kpiHint}>Most people on a single billed line - never a tenant user total</span>
              </div>
              {summary?.unclassifiedHarnessCredits ? (
                <div className={styles.kpi}>
                  <span className={styles.kpiValue}>{formatCredits(summary.unclassifiedHarnessCredits)}</span>
                  <span className={styles.kpiLabel}>Unclassified harness</span>
                  <span className={styles.kpiHint}>Microsoft reported a feature we do not recognise</span>
                </div>
              ) : null}
            </div>

            {summary?.capacity && (
              <div className={`${styles.kpiGrid} ${styles.body}`}>
                <div className={styles.kpi}>
                  <span className={styles.kpiValue}>{formatCredits(summary.capacity.available)}</span>
                  <span className={styles.kpiLabel}>Credits available now</span>
                  <span className={styles.kpiHint}>
                    {formatCredits(summary.capacity.consumed)} of {formatCredits(summary.capacity.entitled)} used
                    {summary.capacity.consumptionType ? ` (${summary.capacity.consumptionType})` : ''}
                  </span>
                </div>
                <div className={styles.kpi}>
                  <span className={styles.kpiValue}>{summary.capacity.status ?? DASH}</span>
                  <span className={styles.kpiLabel}>Capacity status</span>
                  <span className={styles.kpiHint}>
                    As at {formatDay(summary.capacity.consumptionAsOf ?? summary.capacity.snapshotUtc)}
                  </span>
                </div>
                {summary.capacity.payAsYouGoConsumed != null && (
                  <div className={styles.kpi}>
                    <span className={styles.kpiValue}>{formatCredits(summary.capacity.payAsYouGoConsumed)}</span>
                    <span className={styles.kpiLabel}>Pay-as-you-go credits</span>
                    <span className={styles.kpiHint}>Billed on top of pre-purchased capacity</span>
                  </div>
                )}
              </div>
            )}

            {summary && summary.azureCost.length > 0 && (
              <div className={`${styles.kpiGrid} ${styles.body}`}>
                {summary.azureCost.map((c) => (
                  <div className={styles.kpi} key={c.currency ?? 'unknown'}>
                    <span className={styles.kpiValue}>{formatMoney(c.cost, c.currency)}</span>
                    <span className={styles.kpiLabel}>Azure spend</span>
                    {c.includesEstimates && <span className={styles.kpiHint}>Includes estimates that can still change</span>}
                  </div>
                ))}
              </div>
            )}
          </Card>

          {/* Daily trend. Deliberately a plain bar strip: the grain is daily, and the shared
              time-series chart is built around a weekly spine. */}
          <Card>
            <Text weight="semibold">Credits per day</Text>
            {trend.length === 0 ? (
              <div className={styles.empty}>No credit usage in this period.</div>
            ) : (
              <>
                <div className={styles.trendRow}>
                  {trend.map((p) => (
                    <div
                      key={p.date}
                      className={styles.trendBar}
                      style={{ height: `${Math.max(2, (p.billedCredits / trendMax) * 100)}%` }}
                      title={`${formatDay(p.date)}: ${formatCredits(p.billedCredits)} credits`}
                    />
                  ))}
                </div>
                <Text className={styles.muted} size={200}>
                  {formatDay(trend[0].date)} to {formatDay(trend[trend.length - 1].date)}, peak{' '}
                  {formatCredits(trendMax)} credits in a day
                </Text>
              </>
            )}
          </Card>

          {/* Filters. */}
          <Card>
            <Text weight="semibold">Narrow the per-agent figures</Text>
            <div className={`${styles.filterBar} ${styles.body}`}>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>Agent</span>
                <Select value={agentId} onChange={(_, d) => setAgentId(d.value)} disabled={loading}>
                  <option value="">All agents</option>
                  {options?.agents.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.label}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>Environment</span>
                <Select value={environmentId} onChange={(_, d) => setEnvironmentId(d.value)} disabled={loading}>
                  <option value="">All environments</option>
                  {options?.environments.map((e) => (
                    <option key={e.id} value={e.id}>
                      {e.label}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>Harness</span>
                <Select value={harness} onChange={(_, d) => setHarness(d.value)} disabled={loading}>
                  <option value="">All harnesses</option>
                  {options?.harnesses.map((h) => (
                    <option key={h} value={h}>
                      {harnessLabel(h)}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>Billing feature</span>
                <Select value={feature} onChange={(_, d) => setFeature(d.value)} disabled={loading}>
                  <option value="">All features</option>
                  {options?.features.map((f) => (
                    <option key={f} value={f}>
                      {f}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>AI model</span>
                <Select value={model} onChange={(_, d) => setModel(d.value)} disabled={loading}>
                  <option value="">All models</option>
                  {options?.models.map((m) => (
                    <option key={m} value={m}>
                      {m}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>Tool invoked</span>
                <Select value={tool} onChange={(_, d) => setTool(d.value)} disabled={loading}>
                  <option value="">All tools</option>
                  {options?.tools.map((t) => (
                    <option key={t} value={t}>
                      {t}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>Knowledge source</span>
                <Select value={knowledge} onChange={(_, d) => setKnowledge(d.value)} disabled={loading}>
                  <option value="">All knowledge sources</option>
                  {options?.knowledgeSources.map((k) => (
                    <option key={k} value={k}>
                      {k}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>Channel</span>
                <Select value={channel} onChange={(_, d) => setChannel(d.value)} disabled={loading}>
                  <option value="">All channels</option>
                  {options?.channels.map((c) => (
                    <option key={c} value={c}>
                      {c}
                    </option>
                  ))}
                </Select>
              </label>
              <label className={styles.filterField}>
                <span className={styles.filterLabel}>Search agent name</span>
                <Input value={search} onChange={(_, d) => setSearch(d.value)} placeholder="Agent name or ID" />
              </label>
            </div>
          </Card>

          {/* The pivot. */}
          <Card>
            <div className={styles.cardHead}>
              <div>
                <Text weight="semibold">Where the credits went</Text>
                {activeDimension && (
                  <div>
                    <Text className={styles.muted} size={200}>
                      {activeDimension.hint}
                    </Text>
                  </div>
                )}
              </div>
            </div>
            <TabList
              selectedValue={dimension}
              onTabSelect={((_, d) => setDimension(d.value as CreditDimension)) as SelectTabEventHandler}
            >
              {CREDIT_DIMENSIONS.map((d) => (
                <Tab key={d.key} value={d.key}>
                  {d.label}
                </Tab>
              ))}
            </TabList>

            <div className={styles.body}>
              <CategoryBarChart categories={categories} valueLabel="credits" />
            </div>

            {breakdown.length > 0 && (
              <div className={`${styles.wrap} ${styles.body}`}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th className={styles.th}>{activeDimension?.label ?? 'Value'}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Credits billed</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Share</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Not charged</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Days active</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Busiest slice (people)</th>
                    </tr>
                  </thead>
                  <tbody>
                    {breakdown.map((r) => (
                      <tr key={r.key ?? NOT_REPORTED}>
                        <td className={`${styles.td} ${r.key ? '' : styles.tdMuted}`}>{labelFor(dimension, r.label ?? r.key)}</td>
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
                    Showing the top {breakdown.length} of this period&apos;s spend. Shares are of the full{' '}
                    {formatCredits(shareTotal)} credits, so they will not add up to 100%.
                  </Text>
                )}
              </div>
            )}
          </Card>

          {/* The granular rows - the deepest view the source supports. */}
          <Card>
            <div className={styles.cardHead}>
              <div>
                <Text weight="semibold">Every billed line</Text>
                <div>
                  <Text className={styles.muted} size={200}>
                    One row per day, agent and billing dimension - the most detailed view Microsoft&apos;s billing data
                    allows. Click a column heading to sort.
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
                  Export this page
                </Button>
                <Button
                  appearance="primary"
                  icon={<ArrowDownload16Regular />}
                  disabled={exporting || detailLoading || !detail || detail.totalRows === 0}
                  onClick={exportAll}
                >
                  {exporting ? 'Exporting...' : `Export all ${detail ? formatCount(detail.totalRows) : ''} rows`}
                </Button>
              </div>
            </div>

            {!detail || detail.rows.length === 0 ? (
              <div className={styles.empty}>No billed lines match the current filters.</div>
            ) : (
              <>
                <div className={`${styles.wrap} ${styles.body}`}>
                  <table className={styles.table}>
                    <thead>
                      <tr>
                        <th className={`${styles.th} ${styles.thSortable}`} onClick={() => toggleSort('date')}>
                          Day{sort === 'date' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                        <th className={`${styles.th} ${styles.thSortable}`} onClick={() => toggleSort('agent')}>
                          Agent{sort === 'agent' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                        <th className={styles.th}>Environment</th>
                        <th className={styles.th}>Harness</th>
                        <th className={`${styles.th} ${styles.thSortable}`} onClick={() => toggleSort('feature')}>
                          Billing feature{sort === 'feature' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                        <th className={styles.th}>AI model</th>
                        <th className={styles.th}>Tool</th>
                        <th className={styles.th}>Knowledge source</th>
                        <th className={styles.th}>Channel</th>
                        <th
                          className={`${styles.th} ${styles.thNumeric} ${styles.thSortable}`}
                          onClick={() => toggleSort('credits')}
                        >
                          Credits{sort === 'credits' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                        <th className={`${styles.th} ${styles.thNumeric}`}>Not charged</th>
                        <th
                          className={`${styles.th} ${styles.thNumeric} ${styles.thSortable}`}
                          onClick={() => toggleSort('users')}
                        >
                          People{sort === 'users' ? (direction === 'asc' ? ' \u2191' : ' \u2193') : ''}
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {detail.rows.map((r, i) => (
                        <tr key={`${r.usageDate}-${r.agentId}-${r.featureName}-${i}`}>
                          <td className={styles.td}>{formatDay(r.usageDate)}</td>
                          <td className={styles.td}>{r.agentName || r.agentId || DASH}</td>
                          <td className={styles.td}>{r.environmentName || r.environmentId || DASH}</td>
                          <td className={styles.td}>{r.harness ? harnessLabel(r.harness) : DASH}</td>
                          <td className={styles.td}>{r.featureName || DASH}</td>
                          <td className={`${styles.td} ${r.llmModel ? '' : styles.tdMuted}`}>{r.llmModel || DASH}</td>
                          <td className={`${styles.td} ${r.toolInvoked ? '' : styles.tdMuted}`}>{r.toolInvoked || DASH}</td>
                          <td className={`${styles.td} ${r.knowledgeSources ? '' : styles.tdMuted}`}>
                            {r.knowledgeSources || DASH}
                          </td>
                          <td className={`${styles.td} ${r.channelId ? '' : styles.tdMuted}`}>{r.channelId || DASH}</td>
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
                    Previous
                  </Button>
                  <Text size={200} className={styles.muted}>
                    Page {detail.page} of {totalPages} &middot; {formatCount(detail.totalRows)} billed lines
                  </Text>
                  <Button
                    appearance="secondary"
                    disabled={loading || detail.page >= totalPages}
                    onClick={() => setPage((p) => p + 1)}
                  >
                    Next
                  </Button>
                </div>
              </>
            )}
          </Card>

          {/* Who is spending the credits. Copilot Studio only. */}
          <Card>
            <div className={styles.cardHead}>
              <div>
                <Text weight="semibold">Who is spending the credits</Text>
                <div>
                  <Text className={styles.muted} size={200}>
                    Billed Copilot Studio credits per person, reported by Microsoft. Nothing here is estimated or
                    shared out - but it comes from a different Microsoft report than the per-agent figures above, so
                    the two totals will not always match exactly. <strong>The agent, feature, model, tool and channel
                    filters do not apply here</strong> - Microsoft's per-person report does not carry those
                    dimensions, so this panel always shows everyone (narrowed only by environment). Azure spend is
                    not included: Azure bills by resource and never records who caused a charge.
                  </Text>
                </div>
              </div>
            </div>

            {topUsers.length === 0 ? (
              <div className={styles.empty}>
                {!availability?.copilotStudioCreditsEnabled
                  ? 'The Copilot Studio credit import is switched off.'
                  : availability?.hasPerUserCreditData
                    ? 'No per-person credit usage in this period.'
                    : "No per-person figures yet. Microsoft added these to the Power Platform licensing API in July 2026, so a tenant whose API does not offer them will only ever show the per-agent view above."}
              </div>
            ) : (
              <div className={`${styles.wrap} ${styles.body}`}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th className={styles.th}>Person</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Credits billed</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Share</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Days active</th>
                    </tr>
                  </thead>
                  <tbody>
                    {topUsers.map((u) => (
                      <tr key={u.userId ?? u.entraObjectId ?? ''}>
                        <td className={styles.td}>
                          {u.userPrincipalName ?? (
                            <span title={u.entraObjectId ?? undefined}>
                              Unresolved user{u.entraObjectId ? ` (${u.entraObjectId})` : ''}
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
                  Shares are of the {formatCredits(userTotal)} credits shown here, which is the top{' '}
                  {topUsers.length} people - not necessarily every person who used an agent.
                </Text>
              </div>
            )}
          </Card>

          {/* Azure spend. */}
          <Card>
            <div className={styles.cardHead}>
              <div>
                <Text weight="semibold">Azure spend</Text>
                <div>
                  <Text className={styles.muted} size={200}>
                    Daily costs from Microsoft Cost Management for the scopes this deployment is configured to read. Only the
                    date range applies here - the Copilot filters above do not. Azure bills by resource, so these
                    figures cannot be attributed to individual people.
                  </Text>
                </div>
              </div>
              <Select
                value={azureDimension}
                onChange={(_, d) => setAzureDimension(d.value as AzureDimension)}
                disabled={loading}
              >
                {availableAzureDimensions.map((d) => (
                  <option key={d.key} value={d.key}>
                    {d.label}
                  </option>
                ))}
              </Select>
            </div>

            {azure.length === 0 ? (
              <div className={styles.empty}>
                {availability?.azureCostsEnabled
                  ? 'No Azure costs stored for this period.'
                  : 'The Azure cost import is switched off.'}
              </div>
            ) : (
              <div className={`${styles.wrap} ${styles.body}`}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th className={styles.th}>{AZURE_DIMENSIONS.find((d) => d.key === azureDimension)?.label}</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Cost</th>
                      <th className={`${styles.th} ${styles.thNumeric}`}>Quantity</th>
                      <th className={styles.th}>Final?</th>
                    </tr>
                  </thead>
                  <tbody>
                    {azure.map((r) => (
                      <tr key={`${r.key ?? NOT_REPORTED}-${r.currency ?? ''}`}>
                        <td className={`${styles.td} ${r.key ? '' : styles.tdMuted}`}>{r.label || r.key || NOT_REPORTED}</td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatMoney(r.cost, r.currency)}</td>
                        <td className={`${styles.td} ${styles.tdNumeric}`}>{formatQuantity(r.quantity)}</td>
                        <td className={`${styles.td} ${styles.tdMuted}`}>
                          {r.includesEstimates ? 'Estimate - can still change' : 'Final - billing period closed'}
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
