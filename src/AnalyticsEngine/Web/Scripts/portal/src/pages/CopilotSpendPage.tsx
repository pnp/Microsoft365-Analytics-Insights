import { useCallback, useEffect, useMemo, useState } from 'react';
import { useInRouterContext, useSearchParams } from 'react-router-dom';
import {
  Badge,
  Body1,
  Button,
  Card,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  Field,
  Input,
  Link,
  MessageBar,
  MessageBarBody,
  Select,
  Spinner as FluentSpinner,
  Tab,
  TabList,
  Table,
  TableBody,
  TableCell,
  TableCellLayout,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Text,
  Title3,
  Tooltip,
  makeStyles,
  tokens,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, ArrowDownload16Regular } from '@fluentui/react-icons';
import { formatNumber, useI18n, useT, type TFunction, type TranslationKey } from '../i18n';
import { usePortalAccess } from '../access';
import Spinner from '../components/Spinner';
import PrintButton from '../components/shared/PrintButton';
import GlobalFilterBar from '../components/globalFilter/GlobalFilterBar';
import UserFilterBar from '../components/userFilter/UserFilterBar';
import UserFilterPrintSummary from '../components/userFilter/UserFilterPrintSummary';
import { useUserFilterDimensions } from '../components/userFilter/useUserFilterDimensions';
import { parseUserFilter, serializeUserFilter } from '../components/userFilter/userFilterModel';
import { dimensionLabel } from '../components/userFilter/describeUserFilter';
import { EMPTY_USER_FILTER, type UserFilter, type UserFilterDimension } from '../types/userFilter';
import { fetchSpendPeople, fetchSpendPerson, fetchSpendSummary, fetchSpendTrend, spendWorkbookUrl, type SpendQuery } from '../api/copilotSpendApi';
import type { CopilotSpendSummary, SpendPeopleFilters, SpendPeoplePage, SpendPersonDetail, SpendPersonRow, SpendTrend } from '../types/copilotSpend';
import { GROUPING_DIMENSIONS } from '../components/copilotInvestment/OrganisationPanel';
import {
  BILLING_METHOD_KEYS,
  POLICY_STATUS_KEYS,
  REASON_LABEL_KEYS,
  SectionHeading,
  formatCredits,
  formatShare,
  formatShortDate,
} from '../components/copilotInvestment/investmentShared';

type SpendTab = 'overview' | 'studio' | 'balances' | 'organisation' | 'people';

const TAB_LABEL_KEYS: Record<SpendTab, TranslationKey> = {
  overview: 'copilotSpend.tab.overview',
  studio: 'copilotSpend.tab.studio',
  balances: 'copilotSpend.tab.balances',
  organisation: 'copilotSpend.tab.organisation',
  people: 'copilotSpend.tab.people',
};

const WINDOWS: { days: number; labelKey: TranslationKey }[] = [
  { days: 7, labelKey: 'copilotSpend.period.last7' },
  { days: 28, labelKey: 'copilotSpend.period.last28' },
  { days: 90, labelKey: 'copilotSpend.period.last90' },
];

const PAGE_SIZE = 50;
const SEARCH_DEBOUNCE_MS = 350;
const CREDIT_UNIT = 'copilotCredit';

/** The URL parameter the reader's own filter is kept in, as on the other Copilot reports. */
export const SPEND_USER_FILTER_URL_PARAM = 'filter';

const useStyles = makeStyles({
  header: { display: 'flex', flexDirection: 'column', gap: '8px' },
  toolbar: { display: 'flex', gap: '12px', alignItems: 'center', flexWrap: 'wrap', marginTop: '12px' },
  spacer: { flexGrow: 1 },
  scope: { display: 'flex', gap: '12px', flexWrap: 'wrap', color: tokens.colorNeutralForeground3, marginTop: '8px' },
  tabs: { marginTop: '16px', marginBottom: '16px', overflowX: 'auto', maxWidth: '100%' },
  notes: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '12px' },
  stack: { display: 'flex', flexDirection: 'column', gap: '20px' },
  sources: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))', gap: '16px' },
  card: { display: 'flex', flexDirection: 'column', gap: '10px', padding: '16px', overflowX: 'auto' },
  kpi: { fontSize: tokens.fontSizeHero700, lineHeight: tokens.lineHeightHero700, fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums' },
  kpiRow: { display: 'flex', alignItems: 'baseline', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  badges: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  numHeader: { justifyContent: 'flex-end', textAlign: 'right' },
  facts: { display: 'grid', gridTemplateColumns: 'minmax(200px, 360px) auto', justifyContent: 'start', gap: '6px 24px', alignItems: 'baseline' },
  chart: { display: 'flex', alignItems: 'flex-end', gap: '2px', height: '120px', paddingTop: '8px', borderBottom: `1px solid ${tokens.colorNeutralStroke1}` },
  bar: { flex: '1 1 0', minWidth: '3px', backgroundColor: tokens.colorBrandBackground, borderRadius: '2px 2px 0 0' },
  barEmpty: { backgroundColor: tokens.colorNeutralBackground5 },
  barUnreported: {
    backgroundColor: 'transparent',
    border: `1px dashed ${tokens.colorNeutralStroke1}`,
    boxSizing: 'border-box',
  },
  axis: { display: 'flex', justifyContent: 'space-between' },
  toolbarRow: { display: 'flex', gap: '12px', alignItems: 'flex-end', flexWrap: 'wrap' },
  table: { minWidth: '900px' },
  personCol: { minWidth: '200px' },
  nowrap: { whiteSpace: 'nowrap' },
  personLink: { textAlign: 'start', overflowWrap: 'anywhere' },
  pager: { display: 'flex', gap: '12px', alignItems: 'center', justifyContent: 'flex-end' },
  dialog: { maxWidth: 'min(860px, calc(100vw - 32px))', width: '100%' },
  section: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '16px' },
  list: { display: 'grid', gridTemplateColumns: 'minmax(140px, 1fr) auto', gap: '4px 16px' },
  code: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
});

export default function CopilotSpendPage() {
  return useInRouterContext() ? <RoutedSpendPage /> : <SpendView />;
}

function RoutedSpendPage() {
  const [params, setParams] = useSearchParams();
  const raw = params.get(SPEND_USER_FILTER_URL_PARAM);
  const userFilter = useMemo(() => parseUserFilter(raw), [raw]);
  const onUserFilterChange = useCallback(
    (filter: UserFilter) => {
      setParams(
        (current) => {
          const next = new URLSearchParams(current);
          const serialized = serializeUserFilter(filter);
          if (serialized) next.set(SPEND_USER_FILTER_URL_PARAM, serialized);
          else next.delete(SPEND_USER_FILTER_URL_PARAM);
          return next;
        },
        { replace: true },
      );
    },
    [setParams],
  );
  return <SpendView userFilter={userFilter} onUserFilterChange={onUserFilterChange} />;
}

/**
 * Copilot spend: who consumed what, when, from which source and where in the organisation. Facts only - the page has no
 * prices, value or recommendations, so it works before anyone sets the investment assumptions.
 */
function SpendView({ userFilter: controlledFilter, onUserFilterChange }: {
  userFilter?: UserFilter;
  onUserFilterChange?: (filter: UserFilter) => void;
}) {
  const styles = useStyles();
  const t = useT();
  const { language } = useI18n();
  const access = usePortalAccess();
  const canSeePii = access.seePii;

  const [windowDays, setWindowDays] = useState(28);
  const [dimension, setDimension] = useState('department');
  const [tab, setTab] = useState<SpendTab>('overview');
  const [reloadKey, setReloadKey] = useState(0);
  const [peopleFilters, setPeopleFilters] = useState<SpendPeopleFilters>(DEFAULT_SPEND_PEOPLE_FILTERS);

  // A reader without See PII sees aggregates only: no filter of their own, no grouping that names people.
  const [ownFilter, setOwnFilter] = useState<UserFilter>(EMPTY_USER_FILTER);
  const requestedFilter = controlledFilter ?? ownFilter;
  const userFilter = canSeePii ? requestedFilter : EMPTY_USER_FILTER;
  const setUserFilter = useCallback(
    (next: UserFilter) => {
      if (controlledFilter === undefined) setOwnFilter(next);
      onUserFilterChange?.(next);
    },
    [controlledFilter, onUserFilterChange],
  );
  const userFilterParam = serializeUserFilter(userFilter);
  const effectiveDimension = !canSeePii && (dimension === 'manager' || dimension.startsWith('org:')) ? 'department' : dimension;
  const { list: filterDimensions } = useUserFilterDimensions(canSeePii);

  const query: SpendQuery = useMemo(
    () => ({ windowDays, userFilter: userFilterParam, dimension: effectiveDimension }),
    [windowDays, userFilterParam, effectiveDimension],
  );
  const queryKey = JSON.stringify(query);
  // The people the figures cover: a new grouping of the same people keeps the page on screen while it loads.
  const scopeKey = JSON.stringify({ windowDays, userFilter: userFilterParam });

  const [summary, setSummary] = useState<CopilotSpendSummary | null>(null);
  const [summaryKey, setSummaryKey] = useState<string | null>(null);
  const [summaryScopeKey, setSummaryScopeKey] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetchSpendSummary(query, controller.signal)
      .then((result) => {
        // A response for a query the reader has since changed is never shown under the new one.
        if (controller.signal.aborted) return;
        setSummary(result);
        setSummaryKey(queryKey);
        setSummaryScopeKey(scopeKey);
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
    // The key stands for the query: a new object with the same content must not refetch.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey, reloadKey]);

  const visibleTabs = (Object.keys(TAB_LABEL_KEYS) as SpendTab[]).filter((value) => canSeePii || value !== 'people');
  const activeTab: SpendTab = visibleTabs.includes(tab) ? tab : 'overview';
  const onTabSelect: SelectTabEventHandler = (_e, data) => setTab(data.value as SpendTab);
  // Only the figures for the scope on screen: a stale report under a new filter or period would be read as the new one.
  // A new grouping of the same scope keeps the previous report, labelled with its own grouping, until the new one arrives.
  const current = summary && summaryScopeKey === scopeKey ? summary : null;
  const regrouping = current !== null && summaryKey !== queryKey && loading;

  return (
    <div>
      <div className={styles.header}>
        <Title3>{t('copilotSpend.page.title')}</Title3>
        <Body1 block>{t('copilotSpend.page.intro')}</Body1>
      </div>

      <div className={styles.toolbar} data-print="hide">
        <Select value={String(windowDays)} onChange={(_e, data) => setWindowDays(Number(data.value))} aria-label={t('copilotSpend.period.ariaLabel')}>
          {WINDOWS.map((w) => (
            <option key={w.days} value={w.days}>
              {t(w.labelKey)}
            </option>
          ))}
        </Select>
        <Button appearance="subtle" icon={<ArrowClockwise16Regular />} onClick={() => setReloadKey((k) => k + 1)} disabled={loading}>
          {t('common.action.refresh')}
        </Button>
        <span className={styles.spacer} />
        <PrintButton tooltip={t('copilotSpend.controls.print')} />
        <Tooltip
          relationship="description"
          content={current ? t(canSeePii ? 'copilotSpend.controls.excelReady' : 'copilotSpend.controls.excelReadyNoPii') : t('copilotSpend.controls.excelLoading')}
        >
          <Button appearance="primary" icon={<ArrowDownload16Regular />} as="a" href={current ? spendWorkbookUrl(query, language) : undefined} disabled={!current}>
            {t('copilotSpend.controls.excel')}
          </Button>
        </Tooltip>
      </div>

      {current && (
        <div className={styles.scope}>
          <Text size={200}>
            {t('copilotSpend.page.range', { from: formatShortDate(current.fromUtc), to: formatShortDate(current.toUtc), days: formatNumber(current.windowDays) })}
          </Text>
          <Text size={200}>{t('copilotSpend.page.generated', { when: formatShortDate(current.generatedUtc) })}</Text>
          {current.filtered && <Text size={200}>{t('copilotSpend.page.filtered')}</Text>}
        </div>
      )}

      <GlobalFilterBar />

      {canSeePii && (
        <>
          <UserFilterBar filter={userFilter} onChange={setUserFilter} echoNames={current?.userFilter?.dimensionNames} />
          <UserFilterPrintSummary
            filter={userFilter}
            echo={current?.userFilter ?? undefined}
            dimensions={filterDimensions?.dimensions}
            withinGlobalFilter={!!current?.globalFilter}
          />
        </>
      )}

      <div className={styles.notes}>
        <MessageBar intent="info">
          <MessageBarBody>{t('copilotSpend.page.sourcesNote')}</MessageBarBody>
        </MessageBar>
        {error && (
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}
      </div>

      <div className={styles.tabs} data-print="hide">
        <TabList selectedValue={activeTab} onTabSelect={onTabSelect}>
          {visibleTabs.map((value) => (
            <Tab key={value} value={value}>
              {t(TAB_LABEL_KEYS[value])}
            </Tab>
          ))}
        </TabList>
      </div>

      {loading && !current && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={64} label={t('copilotSpend.page.loading')} />
        </div>
      )}

      {current && (
        <div data-print="flow">
          {activeTab === 'overview' && <OverviewPanel summary={current} customDimensions={filterDimensions?.dimensions ?? []} onOpenTab={setTab} />}
          {activeTab === 'studio' && <StudioPanel summary={current} />}
          {activeTab === 'balances' && <BalancesPanel summary={current} query={query} reloadKey={reloadKey} />}
          {activeTab === 'organisation' && (
            <OrganisationSpendPanel
              summary={current}
              dimension={effectiveDimension}
              onDimensionChange={setDimension}
              canSeePii={canSeePii}
              customDimensions={filterDimensions?.dimensions ?? []}
              regrouping={regrouping}
            />
          )}
          {activeTab === 'people' && canSeePii && <SpendPeoplePanel query={query} filters={peopleFilters} onFiltersChange={setPeopleFilters} />}
        </div>
      )}
    </div>
  );
}

/** Where each source stands: its total, who it covers, how complete and how fresh it is. Never one figure for both. */
function OverviewPanel({ summary, customDimensions, onOpenTab }: {
  summary: CopilotSpendSummary;
  customDimensions: UserFilterDimension[];
  onOpenTab: (tab: SpendTab) => void;
}) {
  const styles = useStyles();
  const t = useT();
  const studio = summary.studio;
  const balances = summary.balances;
  const groups = summary.organisation.groups.slice(0, 5);
  return (
    <div className={styles.stack}>
      <div className={styles.sources}>
        <Card className={styles.card}>
          <SectionHeading
            title={t('copilotSpend.studio.title')}
            badge={
              <Badge size="small" appearance="outline">
                {t('copilotSpend.studio.badge')}
              </Badge>
            }
          />
          <StudioStatus summary={summary} />
          <div className={styles.kpiRow}>
            <span className={styles.kpi} title={studio.credits === null ? t('copilotSpend.studio.notAvailable') : undefined}>
              {formatCredits(studio.credits)}
            </span>
            <Text>{t('copilotSpend.overview.studioCredits')}</Text>
          </div>
          {studio.people !== null && <Text>{t('copilotSpend.overview.studioPeople', { people: formatNumber(studio.people) })}</Text>}
          {studio.unmatchedCredits !== null && studio.unmatchedCredits > 0 && (
            <Text size={200} className={styles.muted}>
              {t('copilotSpend.overview.unmatched', { credits: formatCredits(studio.unmatchedCredits) })}
            </Text>
          )}
          <DayChart summary={summary} />
        </Card>
        <Card className={styles.card}>
          <SectionHeading
            title={t('copilotSpend.balances.title')}
            badge={
              <Badge size="small" appearance="outline" color="warning">
                {t('copilotSpend.balances.badge')}
              </Badge>
            }
          />
          <BalanceStatus summary={summary} />
          <div className={styles.kpiRow}>
            <span className={styles.kpi} title={balances.credits === null ? t('copilotSpend.studio.notAvailable') : undefined}>
              {formatCredits(balances.credits)}
            </span>
            <Text>{t('copilotSpend.overview.balanceCredits')}</Text>
          </div>
          {balances.snapshotDate && (
            <Text>
              {t('copilotSpend.overview.balanceReadings', {
                date: formatShortDate(balances.snapshotDate),
                people: formatNumber(balances.consumingPeople),
                observed: formatNumber(balances.observedPeople),
              })}
            </Text>
          )}
          {balances.periodStatus !== 'verified' && balances.hasData && (
            <div className={styles.badges}>
              <Badge appearance="tint" color="warning">
                {t('copilotSpend.balances.periodUnverified')}
              </Badge>
            </div>
          )}
          {(balances.unknownAmountPeople > 0 || balances.partialPeople > 0) && (
            <Text size={200} className={styles.muted}>
              {t('copilotSpend.overview.balanceGaps', {
                unknown: formatNumber(balances.unknownAmountPeople),
                partial: formatNumber(balances.partialPeople),
              })}
            </Text>
          )}
          {balances.unresolvedAccounts !== null && balances.unresolvedAccounts > 0 && (
            <Text size={200} className={styles.muted}>
              {t('copilotSpend.overview.unresolved', {
                accounts: formatNumber(balances.unresolvedAccounts),
                credits: formatCredits(balances.unresolvedCredits),
              })}
            </Text>
          )}
        </Card>
      </div>

      <Card className={styles.card}>
        <SectionHeading
          title={t('copilotSpend.overview.topGroups')}
          description={t('copilotSpend.overview.topGroupsDescription', {
            dimension: dimensionLabel(t, summary.organisation.dimension, { dimensions: customDimensions, names: { [summary.organisation.dimension]: summary.organisation.dimensionName ?? '' } }),
          })}
        />
        {summary.organisation.hidden ? (
          <Text>{t('copilotSpend.org.hidden')}</Text>
        ) : summary.organisation.unavailableCode ? (
          <Text>{t(GROUPING_UNAVAILABLE_KEYS[summary.organisation.unavailableCode])}</Text>
        ) : groups.length === 0 ? (
          <Text>{t('copilotSpend.org.empty')}</Text>
        ) : (
          <GroupTable summary={summary} groups={groups} customDimensions={customDimensions} />
        )}
        <div>
          <Button appearance="secondary" onClick={() => onOpenTab('organisation')}>
            {t('copilotSpend.overview.allGroups')}
          </Button>
        </div>
      </Card>
    </div>
  );
}

function StudioStatus({ summary }: { summary: CopilotSpendSummary }) {
  const t = useT();
  const studio = summary.studio;
  const notes: { key: string; intent: 'info' | 'warning' | 'error'; text: string }[] = [];
  if (studio.failureCode) notes.push({ key: 'failed', intent: 'error', text: t('copilotSpend.studio.unavailable') });
  else if (!studio.importEnabled && !studio.hasData) notes.push({ key: 'disabled', intent: 'info', text: t('copilotSpend.studio.disabled') });
  else if (!studio.hasData) notes.push({ key: 'empty', intent: 'info', text: t('copilotSpend.studio.noData') });
  if (studio.lastImportFailed) notes.push({ key: 'import', intent: 'warning', text: t('copilotSpend.studio.lastImportFailed') });
  if (studio.hasData && studio.earliestDate && !studio.coversWindow)
    notes.push({ key: 'partial', intent: 'warning', text: t('copilotSpend.studio.partialWindow', { date: formatShortDate(studio.earliestDate) }) });
  return (
    <>
      {notes.map((n) => (
        <MessageBar key={n.key} intent={n.intent}>
          <MessageBarBody>{n.text}</MessageBarBody>
        </MessageBar>
      ))}
    </>
  );
}

function BalanceStatus({ summary }: { summary: CopilotSpendSummary }) {
  const t = useT();
  const balances = summary.balances;
  const notes: { key: string; intent: 'info' | 'warning' | 'error'; text: string }[] = [];
  if (balances.failureCode) notes.push({ key: 'failed', intent: 'error', text: t('copilotSpend.balances.unavailable') });
  else if (!balances.tablesPresent) notes.push({ key: 'tables', intent: 'info', text: t('copilotSpend.balances.notInstalled') });
  else if (!balances.hasData) notes.push({ key: 'empty', intent: 'info', text: t('copilotSpend.balances.noData') });
  if (balances.tablesPresent && !balances.collectionEnabled) notes.push({ key: 'off', intent: 'info', text: t('copilotSpend.balances.notCollecting') });
  if (balances.snapshotBeforeWindow && balances.snapshotDate)
    notes.push({ key: 'stale', intent: 'warning', text: t('copilotSpend.balances.snapshotBeforeWindow', { date: formatShortDate(balances.snapshotDate) }) });
  return (
    <>
      {notes.map((n) => (
        <MessageBar key={n.key} intent={n.intent}>
          <MessageBarBody>{n.text}</MessageBarBody>
        </MessageBar>
      ))}
    </>
  );
}

/**
 * Billed credits for every calendar day of the period, so weekends and quiet days keep their place. A day the ledger holds
 * no charges for is drawn as a short empty bar; a day after the ledger's latest stored day is drawn as an outline, because
 * it is not reported yet, which is not zero. Every bar says so in words, for a screen reader and on hover.
 */
function DayChart({ summary }: { summary: CopilotSpendSummary }) {
  const styles = useStyles();
  const t = useT();
  const studio = summary.studio;
  if (!studio.available) return null;
  if (studio.breakdownFailureCode) return <Text size={200}>{t('copilotSpend.studio.breakdownUnavailable')}</Text>;
  if (studio.days.length === 0) return <Text size={200}>{t('copilotSpend.studio.noDays')}</Text>;
  const max = Math.max(...studio.days.map((d) => d.credits ?? 0), 0) || 1;
  const label = (d: (typeof studio.days)[number]) =>
    d.status === 'recorded'
      ? t('copilotSpend.studio.dayCell', { date: formatShortDate(d.date), credits: formatCredits(d.credits), people: formatNumber(d.people ?? 0) })
      : d.status === 'noneRecorded'
        ? t('copilotSpend.studio.dayNoneRecorded', { date: formatShortDate(d.date) })
        : t('copilotSpend.studio.dayNotReported', { date: formatShortDate(d.date) });
  return (
    <div>
      <Text size={200} weight="semibold">
        {t('copilotSpend.studio.daysTitle')}
      </Text>
      <div className={styles.chart} role="list" aria-label={t('copilotSpend.studio.daysTitle')}>
        {studio.days.map((d) => {
          const text = label(d);
          const className =
            d.status === 'recorded' ? styles.bar : d.status === 'noneRecorded' ? `${styles.bar} ${styles.barEmpty}` : `${styles.bar} ${styles.barUnreported}`;
          const height = d.status === 'recorded' ? Math.max(2, ((d.credits ?? 0) / max) * 100) : d.status === 'noneRecorded' ? 2 : 100;
          return <div key={d.date} role="listitem" aria-label={text} title={text} className={className} style={{ height: `${height}%` }} />;
        })}
      </div>
      <div className={styles.axis}>
        <Text size={100} className={styles.muted}>
          {formatShortDate(studio.days[0].date)}
        </Text>
        <Text size={100} className={styles.muted}>
          {formatShortDate(studio.days[studio.days.length - 1].date)}
        </Text>
      </div>
      <Text size={100} block className={styles.muted}>
        {studio.latestDate ? `${t('copilotSpend.studio.latestDay', { date: formatShortDate(studio.latestDate) })} ` : ''}
        {t('copilotSpend.studio.dayLegend')}
      </Text>
    </div>
  );
}

function StudioPanel({ summary }: { summary: CopilotSpendSummary }) {
  const styles = useStyles();
  const t = useT();
  const studio = summary.studio;
  return (
    <div className={styles.stack}>
      <Card className={styles.card}>
        <SectionHeading title={t('copilotSpend.studio.title')} description={t('copilotSpend.studio.description')} />
        <StudioStatus summary={summary} />
        <div className={styles.facts}>
          <Text size={200}>{t('copilotSpend.overview.studioCredits')}</Text>
          <Text weight="semibold">{formatCredits(studio.credits)}</Text>
          <Text size={200}>{t('copilotSpend.studio.peopleLabel')}</Text>
          <Text weight="semibold">{studio.people === null ? '\u2014' : formatNumber(studio.people)}</Text>
          {studio.unmatchedCredits !== null && (
            <>
              <Text size={200}>{t('copilotSpend.studio.unmatchedLabel')}</Text>
              <Text weight="semibold">{formatCredits(studio.unmatchedCredits)}</Text>
            </>
          )}
          <Text size={200}>{t('copilotSpend.studio.ledgerSince')}</Text>
          <Text weight="semibold">{studio.earliestDate ? formatShortDate(studio.earliestDate) : '\u2014'}</Text>
          <Text size={200}>{t('copilotSpend.studio.ledgerLatest')}</Text>
          <Text weight="semibold">{studio.latestDate ? formatShortDate(studio.latestDate) : '\u2014'}</Text>
          <Text size={200}>{t('copilotSpend.studio.lastImport')}</Text>
          <Text weight="semibold">{studio.lastImportUtc ? formatShortDate(studio.lastImportUtc) : '\u2014'}</Text>
        </div>
        <DayChart summary={summary} />
      </Card>
      <Card className={styles.card}>
        <SectionHeading title={t('copilotSpend.studio.environmentsTitle')} description={t('copilotSpend.studio.environmentsDescription')} />
        {!studio.available ? (
          <Text size={200}>{t('copilotSpend.studio.notAvailableLong')}</Text>
        ) : studio.breakdownFailureCode ? (
          <Text size={200}>{t('copilotSpend.studio.breakdownUnavailable')}</Text>
        ) : studio.environments.length === 0 ? (
          <Text size={200}>{t('copilotSpend.studio.noDays')}</Text>
        ) : (
          <Table size="small" aria-label={t('copilotSpend.studio.environmentsTitle')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('copilotSpend.studio.col.environment')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.studio.col.credits')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.studio.col.people')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.studio.col.days')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {studio.environments.map((e) => (
                <TableRow key={e.emptyKey ?? e.environmentId ?? '-'}>
                  <TableCell>
                    {e.emptyKey === 'otherEnvironments'
                      ? t('copilotSpend.studio.otherEnvironments', { count: formatNumber(e.folded) })
                      : (e.environmentName ?? t('copilotSpend.studio.unnamedEnvironment'))}
                    {e.withheld && (
                      <Text size={100} block className={styles.muted}>
                        {t('copilotSpend.studio.environmentsWithheld')}
                      </Text>
                    )}
                    {e.environmentId && (
                      <Text size={100} block className={`${styles.muted} ${styles.code}`}>
                        {e.environmentId}
                      </Text>
                    )}
                  </TableCell>
                  <TableCell className={styles.num}>{formatCredits(e.credits)}</TableCell>
                  <TableCell className={styles.num}>{formatNumber(e.people)}</TableCell>
                  <TableCell className={styles.num}>{e.days === null ? '\u2014' : formatNumber(e.days)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
      <Card className={styles.card}>
        <SectionHeading
          title={t('copilotSpend.studio.agentsTitle')}
          description={
            studio.agents === null || studio.agentLedgerCredits === null
              ? undefined
              : t('copilotSpend.studio.agentsDescription', { credits: formatCredits(studio.agentLedgerCredits) })
          }
        />
        {!studio.available ? (
          <Text size={200}>{t('copilotSpend.studio.notAvailableLong')}</Text>
        ) : studio.breakdownFailureCode ? (
          <Text size={200}>{t('copilotSpend.studio.breakdownUnavailable')}</Text>
        ) : studio.agents === null ? (
          // Withheld from a filtered view by design; unfiltered, the agent ledger simply was not there to read.
          <Text size={200}>{t(summary.filtered ? 'copilotSpend.studio.agentsWithheld' : 'copilotSpend.studio.agentsNotAvailable')}</Text>
        ) : studio.agents.length === 0 ? (
          <Text size={200}>{t('copilotSpend.studio.noAgents')}</Text>
        ) : (
          <Table size="small" aria-label={t('copilotSpend.studio.agentsTitle')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('copilotSpend.studio.col.agent')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.studio.col.credits')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.studio.col.days')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {studio.agents.map((a) => (
                <TableRow key={a.agentId ?? '-'}>
                  <TableCell>
                    {a.agentName ?? t('copilotSpend.studio.unnamedAgent')}
                    {a.agentId && (
                      <Text size={100} block className={`${styles.muted} ${styles.code}`}>
                        {a.agentId}
                      </Text>
                    )}
                  </TableCell>
                  <TableCell className={styles.num}>{formatCredits(a.credits)}</TableCell>
                  <TableCell className={styles.num}>{formatNumber(a.days)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
    </div>
  );
}

/** The latest balances: when they were read, how complete and fresh they are, by category, and the tenant's own context. */
function BalancesPanel({ summary, query, reloadKey }: { summary: CopilotSpendSummary; query: SpendQuery; reloadKey: number }) {
  const styles = useStyles();
  const t = useT();
  const balances = summary.balances;
  const [trend, setTrend] = useState<SpendTrend | null>(null);
  const [trendError, setTrendError] = useState<string | null>(null);
  const trendKey = `${query.windowDays}|${query.userFilter ?? ''}|${reloadKey}`;

  useEffect(() => {
    const controller = new AbortController();
    setTrend(null);
    setTrendError(null);
    fetchSpendTrend(query, controller.signal)
      .then(setTrend)
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setTrendError(e instanceof Error ? e.message : String(e));
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [trendKey]);

  const maxTrend = Math.max(1, ...(trend?.points ?? []).map((p) => p.credits ?? 0));
  return (
    <div className={styles.stack}>
      <Card className={styles.card}>
        <SectionHeading title={t('copilotSpend.balances.title')} description={t('copilotSpend.balances.description')} />
        <BalanceStatus summary={summary} />
        <div className={styles.facts}>
          <Text size={200}>{t('copilotSpend.balances.facts.credits')}</Text>
          <Text weight="semibold">{balances.hasData ? formatCredits(balances.credits) : '\u2014'}</Text>
          <Text size={200}>{t('copilotSpend.balances.facts.snapshot')}</Text>
          <Text weight="semibold">{balances.snapshotDate ? formatShortDate(balances.snapshotDate) : '\u2014'}</Text>
          <Text size={200}>{t('copilotSpend.balances.facts.period')}</Text>
          <Text weight="semibold">
            {balances.periodStatus === 'verified'
              ? t('copilotInvestment.quality.period.verified')
              : balances.periodStatus === 'mixed'
                ? t('copilotInvestment.quality.period.mixed')
                : t('copilotInvestment.quality.period.unverified')}
          </Text>
          <Text size={200}>{t('copilotSpend.balances.facts.freshness')}</Text>
          <Text weight="semibold">
            {balances.oldestFreshness && balances.newestFreshness
              ? t('copilotSpend.balances.facts.freshnessValue', { oldest: formatShortDate(balances.oldestFreshness), newest: formatShortDate(balances.newestFreshness) })
              : '\u2014'}
          </Text>
          <Text size={200}>{t('copilotSpend.balances.facts.sweeps')}</Text>
          <Text weight="semibold">
            {t('copilotSpend.balances.facts.sweepsValue', { started: formatNumber(balances.sweepsInWindow), completed: formatNumber(balances.completedSweepsInWindow) })}
          </Text>
          <Text size={200}>{t('copilotSpend.balances.facts.observed')}</Text>
          <Text weight="semibold">{formatNumber(balances.observedPeople)}</Text>
          <Text size={200}>{t('copilotSpend.balances.facts.consuming')}</Text>
          <Text weight="semibold">{formatNumber(balances.consumingPeople)}</Text>
          <Text size={200}>{t('copilotSpend.balances.facts.partial')}</Text>
          <Text weight="semibold">{formatNumber(balances.partialPeople)}</Text>
          <Text size={200}>{t('copilotSpend.balances.facts.unknown')}</Text>
          <Text weight="semibold">{formatNumber(balances.unknownAmountPeople)}</Text>
          <Text size={200}>{t('copilotSpend.balances.facts.otherUnit')}</Text>
          <Text weight="semibold">{formatNumber(balances.otherUnitPeople)}</Text>
          <Text size={200}>{t('copilotSpend.balances.facts.limit')}</Text>
          <Text weight="semibold">{formatNumber(balances.limitExhaustedPeople)}</Text>
          <Text size={200}>{t('copilotSpend.balances.facts.notEntitled')}</Text>
          <Text weight="semibold">{formatNumber(balances.notEntitledPeople)}</Text>
          {balances.unresolvedAccounts !== null && (
            <>
              <Text size={200}>{t('copilotSpend.balances.facts.unresolved')}</Text>
              <Text weight="semibold">
                {t('copilotSpend.balances.facts.unresolvedValue', {
                  accounts: formatNumber(balances.unresolvedAccounts),
                  credits: formatCredits(balances.unresolvedCredits),
                })}
              </Text>
            </>
          )}
        </div>
      </Card>

      <Card className={styles.card}>
        <SectionHeading title={t('copilotSpend.balances.trendTitle')} description={t('copilotSpend.balances.trendDescription')} />
        {trendError ? (
          <MessageBar intent="error">
            <MessageBarBody>{trendError}</MessageBarBody>
          </MessageBar>
        ) : !trend ? (
          <FluentSpinner size="tiny" label={t('copilotSpend.balances.trendLoading')} />
        ) : trend.points.length === 0 ? (
          <Text size={200}>{t('copilotSpend.balances.noData')}</Text>
        ) : (
          <div>
            <div className={styles.chart} role="list" aria-label={t('copilotSpend.balances.trendTitle')}>
              {trend.points.map((p) => {
                const label = t('copilotSpend.balances.trendPoint', {
                  date: formatShortDate(p.date),
                  credits: formatCredits(p.credits),
                  people: formatNumber(p.consumingUsers),
                });
                return (
                  <div
                    key={p.date}
                    role="listitem"
                    aria-label={label}
                    title={label}
                    className={styles.bar}
                    style={{ height: `${Math.max(2, ((p.credits ?? 0) / maxTrend) * 100)}%` }}
                  />
                );
              })}
            </div>
            <div className={styles.axis} aria-hidden="true">
              <Text size={100} className={styles.muted}>
                {formatShortDate(trend.points[0].date)}
              </Text>
              <Text size={100} className={styles.muted}>
                {formatShortDate(trend.points[trend.points.length - 1].date)}
              </Text>
            </div>
          </div>
        )}
      </Card>

      <Card className={styles.card}>
        <SectionHeading title={t('copilotSpend.balances.servicesTitle')} description={t('copilotSpend.balances.servicesDescription')} />
        {balances.services.length === 0 ? (
          <Text size={200}>{t('copilotSpend.balances.noData')}</Text>
        ) : (
          <Table size="small" aria-label={t('copilotSpend.balances.servicesTitle')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('copilotInvestment.services.col.service')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.services.col.unit')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotInvestment.services.col.consumers')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotInvestment.services.col.quantity')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotInvestment.services.col.refused')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotInvestment.services.col.unevaluated')}</TableHeaderCell>
                <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotInvestment.services.col.unknown')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {balances.services.map((s) => (
                <TableRow key={`${s.category}|${s.unit ?? ''}`}>
                  <TableCell>
                    <span className={styles.code}>{s.category}</span>
                  </TableCell>
                  <TableCell>
                    {s.unit === CREDIT_UNIT
                      ? t('copilotInvestment.services.unitCredits')
                      : s.unit
                        ? t('copilotInvestment.services.unitOther', { unit: s.unit })
                        : t('copilotInvestment.services.unitMissing')}
                  </TableCell>
                  <TableCell className={styles.num}>{formatNumber(s.consumers)}</TableCell>
                  <TableCell className={styles.num}>{formatCredits(s.quantity)}</TableCell>
                  <TableCell className={styles.num}>{formatNumber(s.limitExhaustedUsers + s.notEntitledUsers)}</TableCell>
                  <TableCell className={styles.num}>{formatNumber(s.unevaluatedUsers)}</TableCell>
                  <TableCell className={styles.num}>{formatNumber(s.unknownAmountUsers)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>

      <Card className={styles.card}>
        <SectionHeading
          title={t('copilotInvestment.policies.title')}
          badge={
            <Badge size="small" appearance="outline">
              {t('copilotInvestment.badge.tenantWide')}
            </Badge>
          }
        />
        {summary.filtered ? (
          <Text size={200}>{t('copilotSpend.balances.tenantWithheld')}</Text>
        ) : balances.policies.length === 0 ? (
          <Text size={200}>{t('copilotInvestment.policies.empty')}</Text>
        ) : (
          <Table size="small" aria-label={t('copilotInvestment.policies.title')}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('copilotInvestment.policies.col.name')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.policies.col.status')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.policies.col.billing')}</TableHeaderCell>
                <TableHeaderCell>{t('copilotInvestment.policies.col.services')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {balances.policies.map((p) => (
                <TableRow key={p.id}>
                  <TableCell>{p.displayName ?? p.id}</TableCell>
                  <TableCell>{p.status && POLICY_STATUS_KEYS[p.status] ? t(POLICY_STATUS_KEYS[p.status]) : p.status ?? t('copilotInvestment.value.unknown')}</TableCell>
                  <TableCell>
                    {(p.billingMethods ?? '')
                      .split(',')
                      .map((m) => m.trim())
                      .filter((m) => m.length > 0)
                      .map((m) => (BILLING_METHOD_KEYS[m] ? t(BILLING_METHOD_KEYS[m]) : m))
                      .join(' \u00b7 ')}
                  </TableCell>
                  <TableCell>
                    <span className={styles.code}>{p.services.filter((s) => s.state === 'enabled').map((s) => s.serviceId).join(', ')}</span>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Card>
    </div>
  );
}

function GroupTable({ summary, groups, customDimensions }: { summary: CopilotSpendSummary; groups: CopilotSpendSummary['organisation']['groups']; customDimensions: UserFilterDimension[] }) {
  const styles = useStyles();
  const t = useT();
  const source = { dimensions: customDimensions, names: { [summary.organisation.dimension]: summary.organisation.dimensionName ?? '' } };
  const withheld = t('copilotSpend.org.withheld', { count: formatNumber(summary.organisation.minGroupSize) });
  const notAvailable = t('copilotSpend.studio.notAvailable');
  // A figure the source did not provide, or a combined row too small to show, reads as a dash with its reason - never 0.
  const cell = (text: string, missing: boolean, reason: string) =>
    missing ? (
      <span title={reason} aria-label={reason} role="img">
        {'\u2014'}
      </span>
    ) : (
      text
    );
  return (
    <Table size="small" aria-label={t('copilotSpend.org.title')} className={styles.table}>
      <TableHeader>
        <TableRow>
          <TableHeaderCell>{dimensionLabel(t, summary.organisation.dimension, source)}</TableHeaderCell>
          <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.org.col.people')}</TableHeaderCell>
          <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.org.col.studioCredits')}</TableHeaderCell>
          <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.org.col.shareOfStudio')}</TableHeaderCell>
          <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.org.col.balanceCredits')}</TableHeaderCell>
          <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.org.col.shareOfBalances')}</TableHeaderCell>
          <TableHeaderCell className={styles.num} button={{ className: styles.numHeader }}>{t('copilotSpend.org.col.unknown')}</TableHeaderCell>
        </TableRow>
      </TableHeader>
      <TableBody>
        {groups.map((g) => {
          const reason = g.withheld ? withheld : notAvailable;
          return (
            <TableRow key={g.label ?? g.emptyKey ?? '-'}>
              <TableCell>
                {g.label ??
                  (g.emptyKey === 'otherSmallGroups'
                    ? t('copilotSpend.org.combined', { count: formatNumber(g.folded) })
                    : g.emptyKey === 'moreGroups'
                      ? t('copilotSpend.org.moreGroups', { count: formatNumber(g.folded) })
                      : t('copilotSpend.org.notSet'))}
                {g.withheld && (
                  <Text size={100} block className={styles.muted}>
                    {withheld}
                  </Text>
                )}
              </TableCell>
              <TableCell className={styles.num}>{formatNumber(g.people)}</TableCell>
              <TableCell className={styles.num}>{cell(formatCredits(g.studioCredits), g.studioCredits === null, reason)}</TableCell>
              <TableCell className={styles.num}>{cell(formatShare(g.shareOfStudio), g.studioCredits === null, reason)}</TableCell>
              <TableCell className={styles.num}>{cell(formatCredits(g.balanceCredits), g.balanceCredits === null, reason)}</TableCell>
              <TableCell className={styles.num}>{cell(formatShare(g.shareOfBalances), g.balanceCredits === null, reason)}</TableCell>
              <TableCell className={styles.num}>
                {cell(g.unknownAmountPeople === null ? '' : formatNumber(g.unknownAmountPeople), g.unknownAmountPeople === null, reason)}
              </TableCell>
            </TableRow>
          );
        })}
      </TableBody>
    </Table>
  );
}

function OrganisationSpendPanel({ summary, dimension, onDimensionChange, canSeePii, customDimensions, regrouping }: {
  summary: CopilotSpendSummary;
  dimension: string;
  onDimensionChange: (dimension: string) => void;
  canSeePii: boolean;
  customDimensions: UserFilterDimension[];
  regrouping: boolean;
}) {
  const styles = useStyles();
  const t = useT();
  const extra: string[] = canSeePii ? ['manager', ...customDimensions.filter((d) => d.kind === 'custom').map((d) => d.key)] : [];
  const options = [...GROUPING_DIMENSIONS, ...extra];
  const source = { dimensions: customDimensions };
  const organisation = summary.organisation;
  return (
    <Card className={styles.card}>
      <SectionHeading
        title={t('copilotSpend.org.title')}
        description={
          organisation.minGroupSize > 1
            ? t('copilotSpend.org.description', { count: formatNumber(organisation.minGroupSize) })
            : t('copilotSpend.org.descriptionEveryGroup')
        }
      />
      <div className={styles.toolbarRow} data-print="hide">
        <Field label={t('copilotSpend.org.groupBy')}>
          {/* Stays mounted while a new grouping loads, so keyboard focus and the native list stay where the reader left them. */}
          <Select value={dimension} onChange={(_e, data) => onDimensionChange(data.value)}>
            {options.map((key) => (
              <option key={key} value={key}>
                {dimensionLabel(t, key, source)}
              </option>
            ))}
          </Select>
        </Field>
        {regrouping && (
          <FluentSpinner size="tiny" label={t('copilotSpend.org.regrouping', { dimension: dimensionLabel(t, dimension, source) })} />
        )}
      </div>
      {organisation.hidden ? (
        <Text>{t('copilotSpend.org.hidden')}</Text>
      ) : organisation.unavailableCode ? (
        <MessageBar intent="warning">
          <MessageBarBody>{t(GROUPING_UNAVAILABLE_KEYS[organisation.unavailableCode])}</MessageBarBody>
        </MessageBar>
      ) : organisation.groups.length === 0 ? (
        <Text>{t('copilotSpend.org.empty')}</Text>
      ) : (
        <GroupTable summary={summary} groups={organisation.groups} customDimensions={customDimensions} />
      )}
    </Card>
  );
}

const GROUPING_UNAVAILABLE_KEYS: Record<'directoryUnavailable' | 'dimensionUnknown', TranslationKey> = {
  directoryUnavailable: 'copilotSpend.org.unavailable.directoryUnavailable',
  dimensionUnknown: 'copilotSpend.org.unavailable.dimensionUnknown',
};

/** A sortable column of the people table, by the server's sort key, with its share of the table's width. */
type SpendSortKey = 'user' | 'department' | 'manager' | 'studioCredits' | 'studioDays' | 'balanceCredits' | 'balanceDate';

const PEOPLE_COLUMNS: { key: SpendSortKey; labelKey: TranslationKey; numeric: boolean; firstDescending: boolean; width: string }[] = [
  { key: 'user', labelKey: 'copilotSpend.people.col.person', numeric: false, firstDescending: false, width: '26%' },
  { key: 'department', labelKey: 'copilotSpend.people.col.department', numeric: false, firstDescending: false, width: '11%' },
  { key: 'manager', labelKey: 'copilotSpend.people.col.manager', numeric: false, firstDescending: false, width: '14%' },
  { key: 'studioCredits', labelKey: 'copilotSpend.people.col.studioCredits', numeric: true, firstDescending: true, width: '9%' },
  { key: 'studioDays', labelKey: 'copilotSpend.people.col.studioDays', numeric: true, firstDescending: true, width: '7%' },
  { key: 'balanceCredits', labelKey: 'copilotSpend.people.col.balance', numeric: true, firstDescending: true, width: '9%' },
  { key: 'balanceDate', labelKey: 'copilotSpend.people.col.balanceDate', numeric: true, firstDescending: true, width: '10%' },
];

export const DEFAULT_SPEND_PEOPLE_FILTERS: SpendPeopleFilters = { search: '', origin: 'all', sortBy: 'studioCredits', sortDesc: true };

function personFlags(t: TFunction, row: SpendPersonRow): string[] {
  const flags: string[] = [];
  if (row.balancePartial) flags.push(t('copilotSpend.flag.partial'));
  if (row.unknownAmounts) flags.push(t('copilotSpend.flag.unknown'));
  if (row.otherUnits) flags.push(t('copilotSpend.flag.otherUnit'));
  if (row.limitExhausted) flags.push(t('copilotSpend.flag.limit'));
  if (row.notEntitled) flags.push(t('copilotSpend.flag.notEntitled'));
  return flags;
}

function SpendPeoplePanel({ query, filters, onFiltersChange }: {
  query: SpendQuery;
  filters: SpendPeopleFilters;
  onFiltersChange: (filters: SpendPeopleFilters) => void;
}) {
  const styles = useStyles();
  const t = useT();
  const [searchText, setSearchText] = useState(filters.search);
  const [page, setPage] = useState<SpendPeoplePage | null>(null);
  const [paging, setPaging] = useState<{ key: string; skip: number }>({ key: '', skip: 0 });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<SpendPersonRow | null>(null);

  // Typing is debounced so a search sends one request, not one per keystroke.
  useEffect(() => {
    if (searchText === filters.search) return undefined;
    const timer = setTimeout(() => onFiltersChange({ ...filters, search: searchText }), SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [searchText, filters, onFiltersChange]);

  const queryKey = JSON.stringify({ windowDays: query.windowDays, userFilter: query.userFilter });
  const filtersKey = JSON.stringify(filters);
  // The page offset belongs to one query and one set of filters: changing either starts again at the top.
  const scopeKey = `${queryKey}|${filtersKey}`;
  const skip = paging.key === scopeKey ? paging.skip : 0;
  const setSkip = (next: number) => setPaging({ key: scopeKey, skip: next });

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    setError(null);
    fetchSpendPeople(query, filters, skip, PAGE_SIZE, controller.signal)
      .then(setPage)
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [queryKey, filtersKey, skip]);

  const sortOn = (key: SpendSortKey) => {
    const column = PEOPLE_COLUMNS.find((c) => c.key === key)!;
    onFiltersChange({ ...filters, sortBy: key, sortDesc: filters.sortBy === key ? !filters.sortDesc : column.firstDescending });
  };
  const total = page?.total ?? 0;

  return (
    <div className={styles.stack}>
      <SectionHeading title={t('copilotSpend.people.title')} description={t('copilotSpend.people.description')} />
      <div className={styles.toolbarRow} data-print="hide">
        <Field label={t('copilotSpend.people.search')}>
          <Input value={searchText} onChange={(_e, data) => setSearchText(data.value)} placeholder={t('copilotSpend.people.searchPlaceholder')} />
        </Field>
        <Field label={t('copilotSpend.people.source')}>
          <Select value={filters.origin} onChange={(_e, data) => onFiltersChange({ ...filters, origin: data.value as SpendPeopleFilters['origin'] })}>
            <option value="all">{t('copilotSpend.people.source.all')}</option>
            <option value="studio">{t('copilotSpend.people.source.studio')}</option>
            <option value="balances">{t('copilotSpend.people.source.balances')}</option>
          </Select>
        </Field>
        <Field label={t('copilotSpend.people.sort')}>
          <Select
            value={filters.sortBy}
            onChange={(_e, data) =>
              onFiltersChange({ ...filters, sortBy: data.value, sortDesc: PEOPLE_COLUMNS.find((c) => c.key === data.value)?.firstDescending ?? true })
            }
          >
            {PEOPLE_COLUMNS.map((c) => (
              <option key={c.key} value={c.key}>
                {t(c.labelKey)}
              </option>
            ))}
          </Select>
        </Field>
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      <Card className={styles.card}>
        {loading && !page ? (
          <FluentSpinner size="small" label={t('copilotSpend.people.loading')} />
        ) : page && page.rows.length === 0 ? (
          <Text>{t('copilotSpend.people.empty')}</Text>
        ) : page ? (
          <Table size="small" sortable aria-label={t('copilotSpend.people.title')} className={styles.table}>
            <TableHeader>
              <TableRow>
                {PEOPLE_COLUMNS.map((c) => {
                  const label = t(c.labelKey);
                  return (
                    <TableHeaderCell
                      key={c.key}
                      className={c.numeric ? styles.num : undefined}
                      style={{ width: c.width }}
                      button={c.numeric ? { className: styles.numHeader } : undefined}
                      sortDirection={filters.sortBy === c.key ? (filters.sortDesc ? 'descending' : 'ascending') : undefined}
                      onClick={() => sortOn(c.key)}
                      title={t('copilotSpend.people.sortColumn', { column: label })}
                    >
                      {label}
                    </TableHeaderCell>
                  );
                })}
                <TableHeaderCell style={{ width: '14%' }}>{t('copilotSpend.people.col.notes')}</TableHeaderCell>
              </TableRow>
            </TableHeader>
            <TableBody>
              {page.rows.map((row) => {
                const name = row.userPrincipalName ?? String(row.userId);
                const flags = personFlags(t, row);
                return (
                  <TableRow key={row.userId}>
                    <TableCell className={styles.personCol}>
                      <Link as="button" className={styles.personLink} onClick={() => setSelected(row)} aria-label={t('copilotSpend.people.open', { name })}>
                        {name}
                      </Link>
                      {row.jobTitle && (
                        <Text size={100} block className={styles.muted}>
                          {row.jobTitle}
                        </Text>
                      )}
                    </TableCell>
                    <TableCell>{row.department ?? '\u2014'}</TableCell>
                    <TableCell>
                      {/* A sign-in name can be wider than its column: cut short on screen, whole on hover and to a screen reader. */}
                      <TableCellLayout truncate title={row.manager ?? undefined}>
                        {row.manager ?? '\u2014'}
                      </TableCellLayout>
                    </TableCell>
                    <TableCell className={styles.num}>
                      {row.studioCredits === null ? (
                        <span title={t('copilotSpend.studio.notAvailable')} aria-label={t('copilotSpend.studio.notAvailable')} role="img">
                          {'\u2014'}
                        </span>
                      ) : (
                        formatCredits(row.studioCredits)
                      )}
                    </TableCell>
                    <TableCell className={styles.num}>{row.studioDays === null ? '\u2014' : formatNumber(row.studioDays)}</TableCell>
                    <TableCell className={styles.num}>
                      {row.balanceCredits === null ? (
                        <span title={t('copilotSpend.people.noReading')} aria-label={t('copilotSpend.people.noReading')} role="img">
                          {'\u2014'}
                        </span>
                      ) : (
                        formatCredits(row.balanceCredits)
                      )}
                    </TableCell>
                    <TableCell className={`${styles.num} ${styles.nowrap}`}>{row.balanceDate ? formatShortDate(row.balanceDate) : '\u2014'}</TableCell>
                    <TableCell>
                      <div className={styles.badges}>
                        {flags.map((f) => (
                          <Badge key={f} appearance="outline" color="warning">
                            {f}
                          </Badge>
                        ))}
                      </div>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        ) : null}
        {page && page.total > 0 && (
          <div className={styles.pager} data-print="hide">
            <Text size={200}>{t('copilotSpend.people.count', { shown: formatNumber(Math.min(total, skip + page.rows.length)), total: formatNumber(total) })}</Text>
            <Button size="small" disabled={skip === 0 || loading} onClick={() => setSkip(Math.max(0, skip - PAGE_SIZE))}>
              {t('copilotSpend.people.previous')}
            </Button>
            <Button size="small" disabled={skip + PAGE_SIZE >= total || loading} onClick={() => setSkip(skip + PAGE_SIZE)}>
              {t('copilotSpend.people.next')}
            </Button>
          </div>
        )}
      </Card>

      {selected && <SpendPersonDialog query={query} person={selected} onClose={() => setSelected(null)} />}
    </div>
  );
}

/** One person's consumption: their latest balance by category and over time, and their billed Studio use by environment. */
function SpendPersonDialog({ query, person, onClose }: { query: SpendQuery; person: SpendPersonRow; onClose: () => void }) {
  const styles = useStyles();
  const t = useT();
  const [detail, setDetail] = useState<SpendPersonDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const name = person.userPrincipalName ?? String(person.userId);

  useEffect(() => {
    const controller = new AbortController();
    fetchSpendPerson(query, person.userId, controller.signal)
      .then(setDetail)
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : String(e));
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query.windowDays, query.userFilter, person.userId]);

  return (
    <Dialog open onOpenChange={(_e, data) => !data.open && onClose()}>
      <DialogSurface className={styles.dialog}>
        <DialogBody>
          <DialogTitle>{t('copilotSpend.person.title', { name })}</DialogTitle>
          <DialogContent>
            {error && (
              <MessageBar intent="error">
                <MessageBarBody>{error}</MessageBarBody>
              </MessageBar>
            )}
            {!detail && !error && <FluentSpinner size="small" label={t('copilotSpend.person.loading')} />}
            {detail && (
              <>
                <div className={styles.section}>
                  <Text weight="semibold">{t('copilotSpend.person.studioTitle')}</Text>
                  {detail.studioEnvironments.length === 0 ? (
                    <Text size={200}>{t('copilotSpend.person.noStudio')}</Text>
                  ) : (
                    <div className={styles.list}>
                      {detail.studioEnvironments.map((e) => (
                        <AgentLine
                          key={e.environmentId ?? '-'}
                          name={e.environmentName ?? e.environmentId ?? t('copilotSpend.studio.unnamedEnvironment')}
                          text={t('copilotSpend.person.studioRow', { credits: formatCredits(e.credits), days: formatNumber(e.activeDays) })}
                        />
                      ))}
                    </div>
                  )}
                </div>
                <div className={styles.section}>
                  <Text weight="semibold">{t('copilotSpend.person.balanceTitle')}</Text>
                  {detail.services.length === 0 ? (
                    <Text size={200}>{t('copilotSpend.person.noBalance')}</Text>
                  ) : (
                    <div className={styles.list}>
                      {detail.services.map((s) => (
                        <AgentLine
                          key={s.category}
                          name={s.category}
                          text={
                            s.unevaluatedReason
                              ? `${t('copilotInvestment.person.credits.unevaluated')} \u00b7 ${t(REASON_LABEL_KEYS[s.unevaluatedReason] ?? 'copilotInvestment.reason.other')}`
                              : [
                                  s.consumed === null ? t('copilotSpend.flag.unknown') : formatCredits(s.consumed),
                                  s.unit && s.unit !== CREDIT_UNIT ? t('copilotInvestment.services.unitOther', { unit: s.unit }) : null,
                                  s.decisionReason ? t(REASON_LABEL_KEYS[s.decisionReason] ?? 'copilotInvestment.reason.other') : null,
                                ]
                                  .filter(Boolean)
                                  .join(' \u00b7 ')
                          }
                        />
                      ))}
                    </div>
                  )}
                  {detail.balances.length > 0 && (
                    <>
                      <Text size={200} weight="semibold">
                        {t('copilotSpend.person.historyTitle')}
                      </Text>
                      <Text size={200}>
                        {detail.balances
                          .map((b) => t('copilotSpend.person.historyPoint', { date: formatShortDate(b.date), credits: b.credits === null ? '\u2014' : formatCredits(b.credits) }))
                          .join(' \u00b7 ')}
                      </Text>
                      <Text size={100} className={styles.muted}>
                        {t('copilotSpend.person.historyNote')}
                      </Text>
                    </>
                  )}
                </div>
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" onClick={onClose}>
              {t('copilotSpend.person.close')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}

/** A name from the tenant (an environment, a category) shown verbatim, beside a translated description. */
function AgentLine({ name, text }: { name: string; text: string }) {
  const styles = useStyles();
  return (
    <>
      <span className={styles.code}>{name}</span>
      <Text size={200}>{text}</Text>
    </>
  );
}
