import { useCallback, useEffect, useMemo, useState } from 'react';
import { useInRouterContext, useSearchParams } from 'react-router-dom';
import {
  Badge,
  Body1,
  Button,
  Card,
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
  mergeClasses,
  tokens,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular, ArrowDownload16Regular } from '@fluentui/react-icons';
import { formatNumber, plural, useI18n, useT, type TFunction, type TranslationKey } from '../i18n';
import { usePortalAccess } from '../access';
import Spinner from '../components/Spinner';
import PrintButton from '../components/shared/PrintButton';
import CostApiWarnings from '../components/shared/CostApiWarnings';
import GlobalFilterBar from '../components/globalFilter/GlobalFilterBar';
import UserFilterBar from '../components/userFilter/UserFilterBar';
import UserFilterPrintSummary from '../components/userFilter/UserFilterPrintSummary';
import { useUserFilterDimensions } from '../components/userFilter/useUserFilterDimensions';
import { parseUserFilter, serializeUserFilter } from '../components/userFilter/userFilterModel';
import { dimensionLabel } from '../components/userFilter/describeUserFilter';
import { EMPTY_USER_FILTER, type UserFilter, type UserFilterDimension } from '../types/userFilter';
import { fetchSpendPeople, fetchSpendSummary, fetchSpendTrend, spendWorkbookUrl, type SpendQuery } from '../api/copilotSpendApi';
import type { CopilotSpendSummary, SpendDay, SpendPeopleFilters, SpendPeoplePage, SpendPersonRow, SpendTrend } from '../types/copilotSpend';
import { GROUPING_DIMENSIONS } from '../components/copilotInvestment/OrganisationPanel';
import {
  BILLING_METHOD_KEYS,
  LabelledBars,
  POLICY_STATUS_KEYS,
  SectionHeading,
  formatCredits,
  formatShareAboveZero,
  formatShortDate,
  type BarRowData,
} from '../components/copilotInvestment/investmentShared';
import SpendLineChart, { type SpendLinePoint } from '../components/copilotSpend/SpendLineChart';
import SpendDistributionPanel, { breakableName } from '../components/copilotSpend/SpendDistributionPanel';
import SpendPersonDialog from '../components/copilotSpend/SpendPersonDialog';

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
  badge: { whiteSpace: 'nowrap', flexShrink: 0 },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  numHeader: { justifyContent: 'flex-end', textAlign: 'right' },
  facts: { display: 'grid', gridTemplateColumns: 'minmax(200px, 360px) auto', justifyContent: 'start', gap: '6px 24px', alignItems: 'baseline' },
  chartBlock: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0 },
  toolbarRow: { display: 'flex', gap: '12px', alignItems: 'flex-end', flexWrap: 'wrap' },
  table: { minWidth: '900px' },
  personCol: { minWidth: '200px' },
  nowrap: { whiteSpace: 'nowrap' },
  personLink: { textAlign: 'start', overflowWrap: 'anywhere' },
  pager: { display: 'flex', gap: '12px', alignItems: 'center', justifyContent: 'flex-end' },
  code: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
  // A share drawn beside its figure: decoration only, the figure itself is the text.
  shareCell: { display: 'inline-flex', alignItems: 'center', justifyContent: 'flex-end', gap: '8px', whiteSpace: 'nowrap' },
  shareTrack: { width: '56px', height: '6px', borderRadius: '3px', backgroundColor: tokens.colorNeutralBackground4, overflow: 'hidden', flexShrink: 0 },
  shareBar: { display: 'block', height: '100%', backgroundColor: tokens.colorBrandBackground },
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

  // The readings over time feed the Overview's small chart and the readings tab, so they load once for both.
  // Grouping does not change them: only the period, the people in view and a refresh do.
  const [trend, setTrend] = useState<SpendTrend | null>(null);
  const [trendError, setTrendError] = useState<string | null>(null);
  const trendKey = `${windowDays}|${userFilterParam ?? ''}|${reloadKey}`;
  useEffect(() => {
    const controller = new AbortController();
    setTrend(null);
    setTrendError(null);
    fetchSpendTrend(query, controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) setTrend(result);
      })
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setTrendError(e instanceof Error ? e.message : String(e));
      });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [trendKey]);

  // One person's consumption opens over whichever tab named them: the people list or the highest and lowest consumers.
  const [person, setPerson] = useState<{ userId: number; name: string } | null>(null);
  const openPerson = useCallback((userId: number, name: string) => setPerson({ userId, name }), []);

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
      <CostApiWarnings />

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
          {activeTab === 'overview' && (
            <OverviewPanel
              summary={current}
              customDimensions={filterDimensions?.dimensions ?? []}
              onOpenTab={setTab}
              trend={trend}
              trendError={trendError}
              canSeePii={canSeePii}
              onOpenPerson={openPerson}
            />
          )}
          {activeTab === 'studio' && <StudioPanel summary={current} />}
          {activeTab === 'balances' && <BalancesPanel summary={current} trend={trend} trendError={trendError} />}
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
          {activeTab === 'people' && canSeePii && (
            <SpendPeoplePanel query={query} filters={peopleFilters} onFiltersChange={setPeopleFilters} onOpenPerson={openPerson} />
          )}
        </div>
      )}

      {person && canSeePii && (
        <SpendPersonDialog query={query} userId={person.userId} name={person.name} onClose={() => setPerson(null)} />
      )}
    </div>
  );
}

/** Where each source stands: its total, who it covers, how complete and how fresh it is. Never one figure for both. */
function OverviewPanel({ summary, customDimensions, onOpenTab, trend, trendError, canSeePii, onOpenPerson }: {
  summary: CopilotSpendSummary;
  customDimensions: UserFilterDimension[];
  onOpenTab: (tab: SpendTab) => void;
  trend: SpendTrend | null;
  trendError: string | null;
  canSeePii: boolean;
  onOpenPerson: (userId: number, name: string) => void;
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
          <StudioDaysChart summary={summary} height={150} />
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
          {balances.hasData && (
            <div className={styles.chartBlock}>
              <Text size={200} weight="semibold">
                {t('copilotSpend.balances.trendTitle')}
              </Text>
              <ReadingsTrendChart trend={trend} error={trendError} height={150} />
              <Text size={100} block className={styles.muted}>
                {t('copilotSpend.overview.trendNote')}
              </Text>
            </div>
          )}
        </Card>
      </div>

      <SpendDistributionPanel studio={studio.distribution} balances={balances.distribution} canSeePii={canSeePii} onOpenPerson={onOpenPerson} />

      <ConsumedOnCard summary={summary} onOpenTab={onOpenTab} />

      <Card className={styles.card}>
        <SectionHeading
          title={t('copilotSpend.overview.topGroups')}
          description={t(summary.studio.available ? 'copilotSpend.overview.topGroupsDescription' : 'copilotSpend.overview.topGroupsDescriptionReadings', {
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

/** One calendar day of the Studio ledger as a chart point: a day with no charges is zero, a day not reported yet is a gap. */
function studioDayPoint(t: TFunction, day: SpendDay): SpendLinePoint {
  const date = formatShortDate(day.date);
  if (day.status === 'recorded') {
    return {
      date: day.date,
      values: [day.credits],
      label: t(plural(day.people ?? 0, 'copilotSpend.studio.dayCell.one', 'copilotSpend.studio.dayCell.other'), {
        date,
        credits: formatCredits(day.credits),
        people: formatNumber(day.people ?? 0),
      }),
    };
  }
  if (day.status === 'noneRecorded') return { date: day.date, values: [0], label: t('copilotSpend.studio.dayNoneRecorded', { date }) };
  return { date: day.date, values: [null], notReported: true, label: t('copilotSpend.studio.dayNotReported', { date }) };
}

/**
 * Billed credits for every calendar day of the period, so weekends and quiet days keep their place. A day the ledger holds
 * no charges for is drawn at zero; a day after the ledger's latest stored day is hatched with diagonal stripes and breaks the
 * line, because it is not reported yet, which is not zero. Every point is also read out in words.
 */
function StudioDaysChart({ summary, height = 200 }: { summary: CopilotSpendSummary; height?: number }) {
  const styles = useStyles();
  const t = useT();
  const studio = summary.studio;
  if (!studio.available) return null;
  if (studio.breakdownFailureCode) return <Text size={200}>{t('copilotSpend.studio.breakdownUnavailable')}</Text>;
  if (studio.days.length === 0) return <Text size={200}>{t('copilotSpend.studio.noDays')}</Text>;
  return (
    <div className={styles.chartBlock}>
      <Text size={200} weight="semibold">
        {t('copilotSpend.studio.daysTitle')}
      </Text>
      <SpendLineChart
        points={studio.days.map((d) => studioDayPoint(t, d))}
        series={[{ label: t('copilotSpend.studio.daysSeries'), colorIndex: 0 }]}
        ariaLabel={t('copilotSpend.studio.daysAria')}
        listLabel={t('copilotSpend.studio.daysList')}
        height={height}
        area
        notReportedLabel={t('copilotSpend.studio.notReported')}
      />
      <Text size={100} block className={styles.muted}>
        {studio.latestDate ? `${t('copilotSpend.studio.latestDay', { date: formatShortDate(studio.latestDate) })} ` : ''}
        {t('copilotSpend.studio.dayLegend')}
      </Text>
    </div>
  );
}

/**
 * The consumed credits read on each date of the period. Each point is a total as of its date, not a day's spend, so the
 * points are joined as a line and never added together.
 */
function ReadingsTrendChart({ trend, error, height = 200 }: { trend: SpendTrend | null; error: string | null; height?: number }) {
  const t = useT();
  if (error) {
    return (
      <MessageBar intent="error">
        <MessageBarBody>{error}</MessageBarBody>
      </MessageBar>
    );
  }
  if (!trend) return <FluentSpinner size="tiny" label={t('copilotSpend.balances.trendLoading')} />;
  if (trend.points.length === 0) return <Text size={200}>{t('copilotSpend.balances.noData')}</Text>;
  // ISO dates sort correctly as plain strings, whatever the reader's language.
  const points: SpendLinePoint[] = [...trend.points]
    .sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : 0))
    .map((p) => {
      const date = formatShortDate(p.date);
      const details = [
        t(plural(p.consumingUsers, 'copilotSpend.balances.trendPeople.one', 'copilotSpend.balances.trendPeople.other'), {
          people: formatNumber(p.consumingUsers),
          observed: formatNumber(p.observedUsers),
        }),
      ];
      if (p.limitExhaustedUsers > 0) details.push(t('copilotSpend.balances.trendAtLimit', { people: formatNumber(p.limitExhaustedUsers) }));
      return {
        date: p.date,
        values: [p.credits],
        details,
        label:
          p.credits === null
            ? t('copilotSpend.balances.trendPointUnknown', { date })
            : t(plural(p.consumingUsers, 'copilotSpend.balances.trendPoint.one', 'copilotSpend.balances.trendPoint.other'), {
                date,
                credits: formatCredits(p.credits),
                people: formatNumber(p.consumingUsers),
              }),
      };
    });
  return (
    <SpendLineChart
      points={points}
      series={[{ label: t('copilotSpend.balances.trendSeries'), colorIndex: 1 }]}
      ariaLabel={t('copilotSpend.balances.trendAria')}
      listLabel={t('copilotSpend.balances.trendList')}
      height={height}
      area
      missingLabel={t('copilotSpend.flag.unknown')}
    />
  );
}

const CONSUMED_ON_ROWS = 5;

/**
 * What the credits went on, in each source's own breakdown, largest first: billed Studio credits by environment and by
 * agent, and consumed credits by category. Each share is of its own breakdown; the three are never added together.
 */
function ConsumedOnCard({ summary, onOpenTab }: { summary: CopilotSpendSummary; onOpenTab: (tab: SpendTab) => void }) {
  const styles = useStyles();
  const t = useT();
  const studio = summary.studio;
  const balances = summary.balances;
  const value = (credits: number, total: number | null) =>
    t('copilotSpend.overview.consumedOn.value', { credits: formatCredits(credits), share: formatShareAboveZero(total ? credits / total : null) });

  const studioReadable = studio.available && !studio.breakdownFailureCode;
  const environments: BarRowData[] = studioReadable
    ? studio.environments
        .filter((e) => e.credits !== null && e.credits > 0 && !e.withheld)
        .slice(0, CONSUMED_ON_ROWS)
        .map((e, i) => ({
          key: `${e.emptyKey ?? e.environmentId ?? '-'}-${i}`,
          label:
            e.emptyKey === 'otherEnvironments'
              ? t('copilotSpend.studio.otherEnvironments', { count: formatNumber(e.folded) })
              : (e.environmentName ?? t('copilotSpend.studio.unnamedEnvironment')),
          value: e.credits ?? 0,
          display: value(e.credits ?? 0, studio.credits),
        }))
    : [];
  const agents: BarRowData[] =
    studioReadable && studio.agents
      ? [...studio.agents]
          .filter((a) => a.credits > 0)
          .sort((a, b) => b.credits - a.credits)
          .slice(0, CONSUMED_ON_ROWS)
          .map((a, i) => ({
            key: `${a.agentId ?? '-'}-${i}`,
            label: a.agentName ?? t('copilotSpend.studio.unnamedAgent'),
            value: a.credits,
            display: value(a.credits, studio.agentLedgerCredits),
          }))
      : [];
  const credited = balances.available ? balances.services.filter((s) => s.unit === CREDIT_UNIT && s.quantity > 0) : [];
  const categoryTotal = credited.reduce((sum, s) => sum + s.quantity, 0);
  const categories: BarRowData[] = [...credited]
    .sort((a, b) => b.quantity - a.quantity)
    .slice(0, CONSUMED_ON_ROWS)
    .map((s) => ({ key: s.category, label: s.category, value: s.quantity, display: value(s.quantity, categoryTotal) }));

  if (environments.length === 0 && agents.length === 0 && categories.length === 0) return null;
  return (
    <Card className={styles.card}>
      <SectionHeading title={t('copilotSpend.overview.consumedOn.title')} description={t('copilotSpend.overview.consumedOn.description')} />
      <div className={styles.sources}>
        {environments.length > 0 && (
          <div className={styles.chartBlock}>
            <Text size={200} weight="semibold">
              {t('copilotSpend.overview.consumedOn.environments')}
            </Text>
            <LabelledBars rows={environments} />
          </div>
        )}
        {agents.length > 0 && (
          <div className={styles.chartBlock}>
            <Text size={200} weight="semibold">
              {t('copilotSpend.overview.consumedOn.agents')}
            </Text>
            <LabelledBars rows={agents} />
          </div>
        )}
        {categories.length > 0 && (
          <div className={styles.chartBlock}>
            <Text size={200} weight="semibold">
              {t('copilotSpend.overview.consumedOn.categories')}
            </Text>
            <LabelledBars rows={categories} />
          </div>
        )}
      </div>
      <div className={styles.badges} data-print="hide">
        {environments.length + agents.length > 0 && (
          <Button appearance="secondary" onClick={() => onOpenTab('studio')}>
            {t('copilotSpend.overview.consumedOn.openStudio')}
          </Button>
        )}
        {categories.length > 0 && (
          <Button appearance="secondary" onClick={() => onOpenTab('balances')}>
            {t('copilotSpend.overview.consumedOn.openBalances')}
          </Button>
        )}
      </div>
    </Card>
  );
}

/** A share drawn as a short bar beside its figure. The bar is decoration only: the figure is the text. */
function ShareCell({ share, text }: { share: number | null; text: string }) {
  const styles = useStyles();
  const width = share === null || !Number.isFinite(share) ? 0 : Math.max(0, Math.min(1, share));
  return (
    <span className={styles.shareCell}>
      <span aria-hidden="true" className={styles.shareTrack}>
        <span className={styles.shareBar} style={{ width: `${width * 100}%` }} />
      </span>
      {text}
    </span>
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
        <StudioDaysChart summary={summary} />
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
                      <Text size={100} block className={mergeClasses(styles.muted, styles.code)}>
                        {e.environmentId}
                      </Text>
                    )}
                  </TableCell>
                  <TableCell className={styles.num}>
                    {e.credits === null ? (
                      formatCredits(e.credits)
                    ) : (
                      <ShareCell share={studio.credits ? e.credits / studio.credits : null} text={formatCredits(e.credits)} />
                    )}
                  </TableCell>
                  <TableCell className={styles.num}>{e.people === null ? '\u2014' : formatNumber(e.people)}</TableCell>
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
                      <Text size={100} block className={mergeClasses(styles.muted, styles.code)}>
                        {a.agentId}
                      </Text>
                    )}
                  </TableCell>
                  <TableCell className={styles.num}>
                    <ShareCell share={studio.agentLedgerCredits ? a.credits / studio.agentLedgerCredits : null} text={formatCredits(a.credits)} />
                  </TableCell>
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
function BalancesPanel({ summary, trend, trendError }: { summary: CopilotSpendSummary; trend: SpendTrend | null; trendError: string | null }) {
  const styles = useStyles();
  const t = useT();
  const balances = summary.balances;
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
        <ReadingsTrendChart trend={trend} error={trendError} />
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
              <TableCell className={styles.num}>
                {g.studioCredits === null ? cell('', true, reason) : <ShareCell share={g.shareOfStudio} text={formatShareAboveZero(g.shareOfStudio)} />}
              </TableCell>
              <TableCell className={styles.num}>{cell(formatCredits(g.balanceCredits), g.balanceCredits === null, reason)}</TableCell>
              <TableCell className={styles.num}>
                {g.balanceCredits === null ? cell('', true, reason) : <ShareCell share={g.shareOfBalances} text={formatShareAboveZero(g.shareOfBalances)} />}
              </TableCell>
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

/** The server's reasons the people cannot be grouped (`CopilotSpendGroupingUnavailable`), each with its wording. */
export const GROUPING_UNAVAILABLE_KEYS: Record<'directoryUnavailable' | 'dimensionUnknown', TranslationKey> = {
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

function SpendPeoplePanel({ query, filters, onFiltersChange, onOpenPerson }: {
  query: SpendQuery;
  filters: SpendPeopleFilters;
  onFiltersChange: (filters: SpendPeopleFilters) => void;
  onOpenPerson: (userId: number, name: string) => void;
}) {
  const styles = useStyles();
  const t = useT();
  const [searchText, setSearchText] = useState(filters.search);
  const [page, setPage] = useState<SpendPeoplePage | null>(null);
  const [paging, setPaging] = useState<{ key: string; skip: number }>({ key: '', skip: 0 });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

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
                      <Link as="button" className={styles.personLink} onClick={() => onOpenPerson(row.userId, name)} aria-label={t('copilotSpend.people.open', { name })}>
                        {breakableName(name)}
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
                    <TableCell className={mergeClasses(styles.num, styles.nowrap)}>{row.balanceDate ? formatShortDate(row.balanceDate) : '\u2014'}</TableCell>
                    <TableCell>
                      <div className={styles.badges}>
                        {flags.map((f) => (
                          <Badge key={f} className={styles.badge} appearance="outline" color="warning">
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
    </div>
  );
}
