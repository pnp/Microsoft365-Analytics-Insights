import { Fragment, useEffect, useMemo, useState } from 'react';
import {
  makeStyles,
  tokens,
  Text,
  Card,
  Input,
  Select,
  Checkbox,
  Button,
  MessageBar,
  MessageBarBody,
  Tooltip,
  Accordion,
  AccordionHeader,
  AccordionItem,
  AccordionPanel,
} from '@fluentui/react-components';
import { ArrowDownload16Regular, ArrowClockwise16Regular } from '@fluentui/react-icons';
import { fetchLicensedUsers, licensedUsersExportUrl } from '../../api/copilotAdoptionApi';
import { AdoptionBand } from '../../types/copilotAdoption';
import type { DateRange } from '../../types/licenceActivity';
import type {
  AdoptionActionSummary,
  AdoptionFilterOptions,
  AdoptionDataSources,
  CopilotAdoptionOptions,
  LicensedUserAdoptionRow,
  LicensedUserFilters,
  LicensedUserPage,
} from '../../types/copilotAdoption';
import Spinner from '../Spinner';
import {
  BandBadge,
  DetailRationale,
  DetailRow,
  DetailSection,
  DetailSections,
  DetailStat,
  DetailStats,
  ExpandAllButton,
  ExpandableUserCell,
  PartialPrintNote,
  PrintedFilters,
  ScoreBar,
  printedSearch,
  scoreColour,
  SortableTh,
  useAdoptionTableStyles,
  useRowExpansion,
} from './adoptionShared';
import { usePrintAllRows } from '../shared/printPreparation';
import { formatCount, formatDate, formatPct, weightSharePct } from '../shared/KpiGrid';
import ActionPlan, { ActionBadge } from './ActionPlan';
import { useT, useTNode, type TFunction, type TranslationKey } from '../../i18n';
import {
  adoptionBandLabel,
  actionLabel,
  recommendedActionText,
  reclaimEligibilityLabel,
  reclaimEligibilityReason,
} from './serverText';

const PAGE_SIZE = 50;

/**
 * The columns the table itself shows. Everything else about a seat holder - where the signal came
 * from, apps, Cowork, the reclaim tier and its reason, the score's components - is in the row's
 * expanded detail, so the table fits the page without scrolling sideways.
 */
const TABLE_COLUMNS = 8;

/**
 * The default sort. "Least engaged first" because the entire purpose of the list is finding the
 * people who are not getting value from a licence somebody is paying for. Every column is sortable
 * from its own header, so the old sort drop-down was a second way to do the same thing.
 */
const DEFAULT_SORT_BY = 'score';

/** The reclaim-tier drop-down, in order. Shared with the printout, which states the option chosen. */
const RECLAIM_OPTIONS: Array<{ value: string; labelKey: TranslationKey }> = [
  { value: '', labelKey: 'copilotAdoptionUsers.licensed.allReclaimTiers' },
  { value: 'certain', labelKey: 'copilotAdoptionUsers.licensed.certainReclaim' },
  { value: 'probable', labelKey: 'copilotAdoptionUsers.licensed.probableReclaim' },
  { value: 'review', labelKey: 'copilotAdoptionUsers.licensed.reviewBeforeReclaim' },
  { value: 'excluded', labelKey: 'copilotAdoptionUsers.licensed.excludedFromReclaim' },
];

const useStyles = makeStyles({
  filters: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: '8px',
    marginBottom: '12px',
  },
  grow: {
    flexGrow: 1,
    minWidth: '200px',
  },
  spacer: {
    flexGrow: 1,
  },
  tableWrap: {
    // Only a safety net for a very narrow window: the table is sized to fit the page, with the detail
    // that used to need sideways scrolling moved into each row's expander.
    overflowX: 'auto',
    // Makes this scrollport the container an expanded row's detail panel is sized against, and the
    // one the optional columns below measure.
    containerType: 'inline-size',
  },
  /**
   * A column that gives way on a narrow page rather than push the table sideways: what it shows is in
   * every row's expanded detail too. The rest - who, engagement, band, active days, the action - stay.
   */
  optionalColumn: {
    '@container (max-width: 1040px)': {
      display: 'none',
    },
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    marginTop: '12px',
    flexWrap: 'wrap',
  },
  /**
   * The source comparison in an expanded row. It used to be a hover title on a "both sources" marker,
   * because squeezed into a column it set the height of every row; the expander has room for it.
   */
  comparison: {
    color: tokens.colorNeutralForeground2,
    marginTop: '4px',
  },
  legend: {
    marginBottom: '8px',
  },
  disabled: {
    color: tokens.colorPaletteRedForeground1,
  },
});

const DEFAULT_FILTERS: LicensedUserFilters = {
  search: '',
  bands: [],
  actions: [],
  reclaimEligibility: '',
  coworkOnly: false,
  disabledOnly: false,
  sortBy: DEFAULT_SORT_BY,
  sortDesc: false,
};

/**
 * The licensed-user list: everyone holding a Microsoft 365 Copilot seat, how much they actually use
 * it, and the single recommended next step for each of them.
 *
 * Filtering and sorting happen server-side against the cached analysis, so the CSV export - which
 * takes the identical parameters - is always exactly what is on screen.
 */
export default function LicensedUsersPanel({
  windowDays,
  dateRange,
  filterOptions,
  actionPlan,
  options,
  dataSources,
  seatLicenceTypeIds,
  initialBands,
  initialAction,
  userFilter,
  shown = true,
}: {
  windowDays: number;
  dateRange?: DateRange | null;
  filterOptions: AdoptionFilterOptions | null;
  /** The action catalogue, used for the legend that replaced the repeated per-row prose column. */
  actionPlan: AdoptionActionSummary[];
  /** The thresholds actually used, so the column explanations quote real numbers rather than prose. */
  options: CopilotAdoptionOptions;
  /** Source dates and periods used to label dual-source comparisons. */
  dataSources?: AdoptionDataSources;
  seatLicenceTypeIds?: number[];
  initialBands?: AdoptionBand[];
  /**
   * Pre-applies a recommended-action filter. Set when the user arrives here by clicking a row of
   * the enablement plan on the overview, so the list they land on is exactly the group of people
   * that plan counted - not a similar-looking one they then have to reconstruct by hand.
   */
  initialAction?: string;
  /**
   * The page-wide user filter in its wire form - Entra ID attributes, email domain and custom
   * organisations - applied to this list and its export too, so the table can never describe a
   * different population from the summary above it.
   */
  userFilter?: string | null;
  /**
   * False while the list sits in a section of the tab that is not showing. A hidden list is not
   * printed, so it must not hold the printout up loading every row.
   */
  shown?: boolean;
}) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();
  const t = useT();
  const tNode = useTNode();

  const [filters, setFilters] = useState<LicensedUserFilters>({
    ...DEFAULT_FILTERS,
    bands: initialBands ?? [],
    actions: initialAction ? [initialAction] : [],
    userFilter: userFilter ?? '',
  });
  const [searchDraft, setSearchDraft] = useState('');
  const [page, setPage] = useState(0);
  const [data, setData] = useState<LicensedUserPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const { isExpanded, toggle: toggleRow, resetRows, expandAll, collapseAll, allExpanded } = useRowExpansion();

  // Any filter change invalidates the current page number - staying on page 5 of a result set that
  // now has two pages shows an empty table and looks like a bug.
  useEffect(() => setPage(0), [filters, windowDays]);

  // Paging or re-filtering replaces the rows under an open detail, so the expander would end up
  // describing whoever happens to land on that line next. "Expand all" survives it: that is a
  // choice about the whole list, not about the rows that happened to be on screen.
  useEffect(() => resetRows(), [filters, windowDays, page, resetRows]);

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchLicensedUsers(windowDays, filters, page * PAGE_SIZE, PAGE_SIZE, seatLicenceTypeIds, controller.signal, ...(dateRange ? [dateRange] as const : []))
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((e: any) => {
        if (cancelled || controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : t('copilotAdoptionUsers.licensed.loadError'));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
      // These requests poll while the analysis is building, so cleanup has to actually stop them -
      // a bare `cancelled` flag would only suppress the state update and leave the loop running.
      controller.abort();
    };
  }, [windowDays, filters, page, seatLicenceTypeIds, reloadKey, t]);

  /**
   * Applies a column-header sort. The effect above already resets the page whenever `filters`
   * changes, so there is nothing else to do here.
   */
  const applySort = (sortBy: string, sortDesc: boolean) => {
    setFilters((f) => ({ ...f, sortBy, sortDesc }));
  };

  const exportUrl = useMemo(
    () => licensedUsersExportUrl(windowDays, filters, seatLicenceTypeIds, ...(dateRange ? [dateRange] as const : [])),
    [windowDays, filters, seatLicenceTypeIds],
  );

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  // The whole list while a print is being produced; the page on screen the rest of the time.
  const printRows = usePrintAllRows<LicensedUserAdoptionRow>({
    enabled: shown && !loading && data !== null,
    total: data?.total ?? 0,
    loadedRows: data?.rows.length ?? 0,
    loadPage: (skip, take, signal) => fetchLicensedUsers(windowDays, filters, skip, take, seatLicenceTypeIds, signal, ...(dateRange ? [dateRange] as const : [])),
  });
  const rows = printRows ?? data?.rows ?? [];

  const selectedBand = filters.bands.length === 1 ? filters.bands[0] : null;
  const selectedAction = filters.actions.length === 1 ? actionPlan.find((a) => a.code === filters.actions[0]) : undefined;
  const reclaimOption = RECLAIM_OPTIONS.find((o) => o.value === filters.reclaimEligibility);

  const scoreWeights = [options.frequencyWeight, options.depthWeight, options.breadthWeight];
  const weightSum = scoreWeights.reduce((total, w) => total + w, 0);
  const bands = {
    champion: options.championScore,
    established: options.establishedScore,
    developing: options.developingScore,
  };

  // Only the actions that actually appear in the rows on screen - or on paper, where that is the
  // whole list. Showing all seven when the filter has narrowed the list to one band would be
  // padding, not explanation.
  const visibleActions = useMemo(() => {
    const present = new Set(rows.map((r) => r.recommendedActionCode));
    return actionPlan.filter((a) => present.has(a.code));
  }, [rows, actionPlan]);

  return (
    <Card>
      {/* Chrome: nothing here can be used on paper. What it is set to is printed below instead. */}
      <div className={styles.filters} data-print="hide">
        <Input
          className={styles.grow}
          value={searchDraft}
          placeholder={t('copilotAdoptionUsers.common.searchPlaceholder')}
          aria-label={t('copilotAdoptionUsers.licensed.searchAria')}
          onChange={(_e: any, d: any) => setSearchDraft(d.value)}
          onKeyDown={(e: any) => {
            if (e.key === 'Enter') setFilters((f) => ({ ...f, search: searchDraft }));
          }}
        />
        <Button size="small" onClick={() => setFilters((f) => ({ ...f, search: searchDraft }))}>
          {t('copilotAdoptionUsers.common.search')}
        </Button>

        <Select
          value={filters.bands.length === 1 ? String(filters.bands[0]) : ''}
          aria-label={t('copilotAdoptionUsers.licensed.filterEngagementBandAria')}
          onChange={(_e: any, d: any) =>
            setFilters((f) => ({ ...f, bands: d.value === '' ? [] : [Number(d.value) as AdoptionBand] }))
          }
        >
          <option value="">{t('copilotAdoptionUsers.licensed.allEngagementBands')}</option>
          {(filterOptions?.bands ?? []).map((b) => (
            <option key={b.value} value={b.value}>
              {adoptionBandLabel(t, b.value, b.name)}
            </option>
          ))}
        </Select>

        <Select
          value={filters.actions.length === 1 ? filters.actions[0] : ''}
          aria-label={t('copilotAdoptionUsers.licensed.filterRecommendedActionAria')}
          onChange={(_e: any, d: any) => setFilters((f) => ({ ...f, actions: d.value === '' ? [] : [d.value] }))}
        >
          <option value="">{t('copilotAdoptionUsers.licensed.allRecommendedActions')}</option>
          {actionPlan.map((a) => (
            <option key={a.code} value={a.code}>
              {actionLabel(t, a.code, a.label)} ({formatCount(a.users)})
            </option>
          ))}
        </Select>

        <Select
          value={filters.reclaimEligibility}
          aria-label={t('copilotAdoptionUsers.licensed.filterReclaimAria')}
          onChange={(_e: any, d: any) => setFilters((f) => ({ ...f, reclaimEligibility: d.value }))}
        >
          {RECLAIM_OPTIONS.map((o) => (
            <option key={o.value} value={o.value}>
              {t(o.labelKey)}
            </option>
          ))}
        </Select>

        <Checkbox
          label={t('copilotAdoptionUsers.licensed.coworkUsersOnly')}
          checked={filters.coworkOnly}
          onChange={(_e: any, d: any) => setFilters((f) => ({ ...f, coworkOnly: !!d.checked }))}
        />
        <Tooltip
          content={t('copilotAdoptionUsers.licensed.disabledOnlyTooltip')}
          relationship="description"
        >
          <Checkbox
            label={t('copilotAdoptionUsers.licensed.disabledAccountsOnly')}
            checked={filters.disabledOnly}
            onChange={(_e: any, d: any) => setFilters((f) => ({ ...f, disabledOnly: !!d.checked }))}
          />
        </Tooltip>

        <div className={styles.spacer} />

        <ExpandAllButton
          allExpanded={allExpanded}
          onExpandAll={expandAll}
          onCollapseAll={collapseAll}
          disabled={rows.length === 0}
        />
        <Button
          size="small"
          appearance="subtle"
          icon={<ArrowClockwise16Regular />}
          onClick={() => setReloadKey((k) => k + 1)}
        >
          {t('copilotAdoptionUsers.common.refresh')}
        </Button>
        <Button size="small" icon={<ArrowDownload16Regular />} as="a" href={exportUrl}>
          {t('copilotAdoptionUsers.common.exportCsv')}
        </Button>
      </div>

      <PrintedFilters
        filters={[
          printedSearch(t, filters.search),
          {
            label: t('copilotAdoptionUsers.licensed.bandHeader'),
            value:
              selectedBand === null
                ? t('copilotAdoptionUsers.licensed.allEngagementBands')
                : adoptionBandLabel(
                    t,
                    selectedBand,
                    (filterOptions?.bands ?? []).find((b) => b.value === selectedBand)?.name ?? String(selectedBand),
                  ),
          },
          {
            label: t('copilotAdoptionUsers.licensed.actionHeader'),
            value:
              filters.actions.length === 1
                ? actionLabel(t, filters.actions[0], selectedAction?.label ?? filters.actions[0])
                : t('copilotAdoptionUsers.licensed.allRecommendedActions'),
          },
          {
            label: t('copilotAdoptionUsers.licensed.reclaimTierHeader'),
            value: t(reclaimOption?.labelKey ?? 'copilotAdoptionUsers.licensed.allReclaimTiers'),
          },
          filters.coworkOnly && { value: t('copilotAdoptionUsers.licensed.coworkUsersOnly') },
          filters.disabledOnly && { value: t('copilotAdoptionUsers.licensed.disabledAccountsOnly') },
        ]}
      />

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {!loading && data && <PartialPrintNote shownRows={rows.length} totalRows={data.total} truncated={printRows !== null} />}

      {loading && (
        <div style={{ textAlign: 'center', padding: '28px' }}>
          <Spinner size={56} label={t('copilotAdoptionUsers.licensed.loadingUsers')} />
        </div>
      )}

      {!loading && data && data.rows.length === 0 && (
        <Text className={styles.muted}>{t('copilotAdoptionUsers.licensed.noMatches')}</Text>
      )}

      {!loading && data && data.rows.length > 0 && (
        <>
          {visibleActions.length > 0 && (
            <Accordion collapsible className={styles.legend}>
              <AccordionItem value="actions">
                <AccordionHeader>{t('copilotAdoptionUsers.licensed.actionsLegendTitle')}</AccordionHeader>
                <AccordionPanel>
                  <ActionPlan actions={visibleActions} options={options} showCounts={false} />
                </AccordionPanel>
              </AccordionItem>
            </Accordion>
          )}

          <div className={styles.tableWrap}>
          <table className={table.table}>
            <thead>
              <tr>
                <SortableTh label={t('copilotAdoptionUsers.common.userSortLabel')} sortKey="upn" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort}>
                  {t('copilotAdoptionUsers.common.user')}
                </SortableTh>
                <SortableTh label={t('copilotAdoptionUsers.common.departmentSortLabel')} sortKey="department" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort}>
                  {t('copilotAdoptionUsers.common.department')}
                </SortableTh>
                <SortableTh
                  label={t('copilotAdoptionUsers.licensed.engagementScoreLabel')}
                  sortKey="score"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  infoTitle={t('copilotAdoptionUsers.licensed.engagementScoreTitle')}
                  info={{
                    what: t('copilotAdoptionUsers.licensed.engagementScoreWhat'),
                    how: (
                      <>
                        <p>{t('copilotAdoptionUsers.licensed.engagementScoreHowIntro')}</p>
                        <ul>
                          <li>
                            <strong>
                              {t('copilotAdoptionUsers.licensed.engagementFrequencyLabel', {
                                weight: formatPct(weightSharePct(options.frequencyWeight, scoreWeights)),
                              })}
                            </strong>
                            {t('copilotAdoptionUsers.licensed.engagementFrequencyText', {
                              target: formatPct(options.frequencyTargetRatio * 100),
                            })}
                          </li>
                          <li>
                            <strong>
                              {t('copilotAdoptionUsers.licensed.engagementDepthLabel', {
                                weight: formatPct(weightSharePct(options.depthWeight, scoreWeights)),
                              })}
                            </strong>
                            {t('copilotAdoptionUsers.licensed.engagementDepthText', {
                              target: options.depthTargetInteractionsPerActiveDay,
                            })}
                          </li>
                          <li>
                            <strong>
                              {t('copilotAdoptionUsers.licensed.engagementBreadthLabel', {
                                weight: formatPct(weightSharePct(options.breadthWeight, scoreWeights)),
                              })}
                            </strong>
                            {t('copilotAdoptionUsers.licensed.engagementBreadthText', {
                              target: options.breadthTargetApps,
                            })}
                          </li>
                        </ul>
                        <p>
                          {tNode('copilotAdoptionUsers.licensed.confidenceSentence', {
                            confidence: <strong>{t('copilotAdoptionUsers.licensed.confidenceLabel')}</strong>,
                            days: options.depthMinActiveDays,
                          })}
                        </p>
                        <p>{t('copilotAdoptionUsers.licensed.newAccountsFrequency')}</p>
                      </>
                    ),
                    formula: t(
                      Math.abs(weightSum - 1) < 1e-9
                        ? 'copilotAdoptionUsers.licensed.engagementFormula.unitWeight'
                        : 'copilotAdoptionUsers.licensed.engagementFormula.weighted',
                      {
                        depthMinDays: options.depthMinActiveDays,
                        depthTarget: options.depthTargetInteractionsPerActiveDay,
                        breadthTarget: options.breadthTargetApps,
                        frequencyWeight: options.frequencyWeight,
                        depthWeight: options.depthWeight,
                        breadthWeight: options.breadthWeight,
                        weightSum,
                      },
                    ),
                    source: t('copilotAdoptionUsers.licensed.engagementScoreSource'),
                  }}
                >
                  {t('copilotAdoptionUsers.licensed.engagementHeader')}
                </SortableTh>
                <SortableTh
                  label={t('copilotAdoptionUsers.licensed.bandSortLabel')}
                  sortKey="band"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  infoTitle={t('copilotAdoptionUsers.licensed.bandTitle')}
                  info={{
                    what: t('copilotAdoptionUsers.licensed.bandWhat'),
                    how: t('copilotAdoptionUsers.licensed.bandHow', {
                      champion: options.championScore,
                      established: options.establishedScore,
                      developing: options.developingScore,
                      historyDays: options.historyDays,
                    }),
                    source: t('copilotAdoptionUsers.licensed.bandSource'),
                  }}
                >
                  {t('copilotAdoptionUsers.licensed.bandHeader')}
                </SortableTh>
                <SortableTh
                  label={t('copilotAdoptionUsers.licensed.interactionsSortLabel')}
                  sortKey="interactions"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  numeric
                  defaultDescending
                  className={styles.optionalColumn}
                >
                  {t('copilotAdoptionUsers.licensed.interactionsHeader')}
                </SortableTh>
                <SortableTh
                  label={t('copilotAdoptionUsers.licensed.activeDaysSortLabel')}
                  sortKey="activeDays"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  numeric
                  defaultDescending
                  infoTitle={t('copilotAdoptionUsers.licensed.activeDaysTitle')}
                  info={{
                    what: t('copilotAdoptionUsers.licensed.activeDaysWhat'),
                    how: t('copilotAdoptionUsers.licensed.activeDaysHow', {
                      target: formatPct(options.frequencyTargetRatio * 100),
                      workingDays: options.workingDaysPerWeek,
                    }),
                    formula: t('copilotAdoptionUsers.licensed.activeDaysFormula', {
                      windowDays: options.windowDays,
                      workingDays: options.workingDaysPerWeek,
                      ratio: options.frequencyTargetRatio,
                    }),
                  }}
                >
                  {t('copilotAdoptionUsers.licensed.activeDaysHeader')}
                </SortableTh>
                <SortableTh label={t('copilotAdoptionUsers.licensed.lastUsedLabel')} sortKey="lastUse" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort} className={styles.optionalColumn}>
                  {t('copilotAdoptionUsers.licensed.lastUsedHeader')}
                </SortableTh>
                <SortableTh
                  label={t('copilotAdoptionUsers.licensed.recommendedActionLabel')}
                  sortKey="action"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  infoTitle={t('copilotAdoptionUsers.licensed.recommendedActionTitle')}
                  info={{
                    what: t('copilotAdoptionUsers.licensed.recommendedActionWhat'),
                    how: t('copilotAdoptionUsers.licensed.recommendedActionHow'),
                    source: t('copilotAdoptionUsers.licensed.recommendedActionSource'),
                  }}
                >
                  {t('copilotAdoptionUsers.licensed.actionHeader')}
                </SortableTh>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const open = isExpanded(row.userId);
                const disabled = row.accountEnabled === false;
                const reclaimTier = reclaimTierText(t, row.reclaimEligibility);
                const tierLine = reclaimTierLine(t, row, reclaimTier);
                return (
                  <Fragment key={row.userId}>
                    <tr>
                      <ExpandableUserCell
                        open={open}
                        onToggle={() => toggleRow(row.userId)}
                        userPrincipalName={row.userPrincipalName}
                        secondary={disabled ? t('copilotAdoptionUsers.licensed.accountDisabled') : row.jobTitle || row.mail}
                        secondaryClassName={disabled ? styles.disabled : undefined}
                        wrapName
                      />
                      <td className={table.td}>{row.department || '\u2014'}</td>
                      <td className={table.td}>
                        <Tooltip
                          relationship="description"
                          content={t('copilotAdoptionUsers.licensed.scoreTooltip', {
                            frequency: Math.round(row.frequencyScore),
                            depth: Math.round(row.depthScore),
                            breadth: Math.round(row.breadthScore),
                            activeDays: row.activeDays,
                            expectedDays: row.expectedActiveDays,
                          })}
                        >
                          <div>
                            <ScoreBar score={row.adoptionScore} colour={scoreColour(row.adoptionScore, bands)} />
                          </div>
                        </Tooltip>
                      </td>
                      <td className={`${table.td} ${table.tdNoWrap}`}>
                        <BandBadge band={row.band} name={adoptionBandLabel(t, row.band, row.bandName)} />
                      </td>
                      <td className={`${table.td} ${table.tdNumeric} ${styles.optionalColumn}`}>{formatCount(row.interactions)}</td>
                      <td className={`${table.td} ${table.tdNumeric} ${table.tdNoWrap}`}>
                        {row.activeDays} <span className={styles.muted}>/ {Math.round(row.expectedActiveDays)}</span>
                      </td>
                      <td className={`${table.td} ${table.tdNoWrap} ${styles.optionalColumn}`}>
                        {formatDate(row.lastInteractionUtc)}
                        {row.daysSinceLastUse !== null && row.daysSinceLastUse > 0 && (
                          <Text size={100} block className={table.tdSub}>
                            {t('copilotAdoptionUsers.licensed.daysAgo', { days: row.daysSinceLastUse })}
                          </Text>
                        )}
                      </td>
                      <td className={table.td}>
                        <Tooltip relationship="description" content={recommendedActionText(t, row, options)}>
                          <div>
                            <ActionBadge code={row.recommendedActionCode} label={row.recommendedActionLabel} wrap />
                          </div>
                        </Tooltip>
                        {/* The reclaim tier lost its column, but a seat that is in one is the reason many
                            readers open this list, so it stays on the row - under the action it qualifies. */}
                        {tierLine && (
                          <Text size={100} block className={table.tdSub}>
                            {tierLine}
                          </Text>
                        )}
                      </td>
                    </tr>
                    {open && (
                      <DetailRow colSpan={TABLE_COLUMNS}>
                        <DetailSections>
                          <DetailSection
                            title={t('copilotAdoptionUsers.licensed.detail.engagementTitle', { score: Math.round(row.adoptionScore) })}
                          >
                            <DetailStats>
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.detail.frequency')}
                                value={Math.round(row.frequencyScore)}
                                sub={t('copilotAdoptionUsers.licensed.detail.frequencySub', {
                                  activeDays: formatCount(row.activeDays),
                                  expectedDays: formatCount(Math.round(row.expectedActiveDays)),
                                })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.detail.depth')}
                                value={Math.round(row.depthScore)}
                                sub={t('copilotAdoptionUsers.licensed.detail.depthSub', {
                                  target: formatCount(options.depthTargetInteractionsPerActiveDay),
                                })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.detail.breadth')}
                                value={Math.round(row.breadthScore)}
                                sub={t('copilotAdoptionUsers.licensed.detail.breadthSub', {
                                  apps: formatCount(row.appsUsed),
                                  target: formatCount(options.breadthTargetApps),
                                })}
                              />
                            </DetailStats>
                          </DetailSection>

                          <DetailSection title={t('copilotAdoptionUsers.licensed.detail.useTitle')}>
                            <DetailStats>
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.interactionsHeader')}
                                value={formatCount(row.interactions)}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.appsHeader')}
                                value={formatCount(row.appsUsed)}
                                sub={t('copilotAdoptionUsers.licensed.detail.agentsSub', { agents: formatCount(row.agentsUsed) })}
                              />
                              <DetailStat label={t('copilotAdoptionUsers.licensed.coworkHeader')} value={coworkCell(t, row)} />
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.detail.firstUsed')}
                                value={formatDate(row.firstInteractionUtc)}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.lastUsedHeader')}
                                value={formatDate(row.lastInteractionUtc)}
                                sub={
                                  row.daysSinceLastUse !== null && row.daysSinceLastUse > 0
                                    ? t('copilotAdoptionUsers.licensed.daysAgo', { days: row.daysSinceLastUse })
                                    : undefined
                                }
                              />
                            </DetailStats>
                          </DetailSection>

                          <DetailSection
                            title={t('copilotAdoptionUsers.licensed.signalSourceTitle')}
                            info={{
                              what: t('copilotAdoptionUsers.licensed.signalSourceWhat'),
                              how: t('copilotAdoptionUsers.licensed.signalSourceHow', { windowDays }),
                              source: t('copilotAdoptionUsers.licensed.signalSourceSource'),
                            }}
                          >
                            <DetailStats>
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.signalHeader')}
                                value={sourceLabel(t, row.signalSource)}
                                sub={row.sourceComparisonAvailable ? t('copilotAdoptionUsers.licensed.bothSources') : undefined}
                              />
                            </DetailStats>
                            {row.sourceComparisonAvailable && (
                              <Text size={200} block className={styles.comparison}>
                                {sourceComparisonText(t, row, windowDays, dataSources)}
                              </Text>
                            )}
                          </DetailSection>

                          <DetailSection
                            title={t('copilotAdoptionUsers.licensed.detail.seatTitle')}
                            info={{
                              what: t('copilotAdoptionUsers.licensed.reclaimEligibilityWhat'),
                              how: t('copilotAdoptionUsers.licensed.reclaimEligibilityHow', { days: options.reclaimGraceDays }),
                              source: t('copilotAdoptionUsers.licensed.reclaimEligibilitySource'),
                            }}
                            infoTitle={t('copilotAdoptionUsers.licensed.reclaimEligibilityTitle')}
                          >
                            <DetailStats>
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.reclaimTierHeader')}
                                value={reclaimTier || t('copilotAdoptionUsers.licensed.detail.noReclaimTier')}
                                sub={row.reclaimExclusionExpired ? t('copilotAdoptionUsers.licensed.exclusionExpired') : undefined}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.detail.account')}
                                value={t(disabled ? 'copilotAdoptionUsers.licensed.accountDisabled' : 'copilotAdoptionUsers.licensed.detail.accountEnabled')}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.licensed.detail.licences')}
                                value={row.seatLicences || '\u2014'}
                              />
                            </DetailStats>
                          </DetailSection>
                        </DetailSections>

                        <DetailSection title={t('copilotAdoptionUsers.licensed.detail.reclaimReasonTitle')}>
                          <DetailRationale
                            text={reclaimEligibilityReason(t, row, options) || t('copilotAdoptionUsers.licensed.activeSeatNotReclaimable')}
                          />
                        </DetailSection>

                        <DetailSection
                          title={t('copilotAdoptionUsers.licensed.detail.actionTitle', {
                            action: actionLabel(t, row.recommendedActionCode, row.recommendedActionLabel),
                          })}
                        >
                          <DetailRationale text={recommendedActionText(t, row, options)} />
                        </DetailSection>
                      </DetailRow>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
          </div>
        </>
      )}

      {!loading && data && data.total > 0 && (
        <div className={styles.footer}>
          <Text size={200} className={styles.muted}>
            {t('copilotAdoptionUsers.licensed.showingUsers', {
              start: formatCount(printRows ? 1 : data.skip + 1),
              end: formatCount(printRows ? printRows.length : Math.min(data.skip + PAGE_SIZE, data.total)),
              total: formatCount(data.total),
            })}
          </Text>
          {/* A printout holds the whole list, so there is no page to turn to. */}
          {!printRows && (
            <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }} data-print="hide">
              <Button size="small" disabled={page === 0} onClick={() => setPage((p) => Math.max(0, p - 1))}>
                {t('copilotAdoptionUsers.common.previous')}
              </Button>
              <Text size={200} className={styles.muted}>
                {t('copilotAdoptionUsers.common.page', { page: page + 1, totalPages })}
              </Text>
              <Button
                size="small"
                disabled={page + 1 >= totalPages}
                onClick={() => setPage((p) => p + 1)}
              >
                {t('copilotAdoptionUsers.common.next')}
              </Button>
            </div>
          )}
        </div>
      )}
    </Card>
  );
}


function sourceLabel(t: TFunction, source: string): string {
  return source === 'usageReport'
    ? t('copilotAdoptionUsers.licensed.sourceUsageReport')
    : source === 'audit'
      ? t('copilotAdoptionUsers.licensed.sourceAudit')
      : source;
}

/**
 * A seat's reclaim tier as the filter names it - "Probable reclaim" rather than the bare "probable",
 * which says nothing on its own under an action badge. Empty for a seat in no tier.
 */
function reclaimTierText(t: TFunction, tier: string | null): string {
  if (!tier) return '';
  const option = RECLAIM_OPTIONS.find((o) => o.value === tier);
  return option ? t(option.labelKey) : reclaimEligibilityLabel(t, tier);
}

/**
 * The line under a row's action badge: the reclaim tier the seat is in. Empty for a seat in no tier.
 *
 * Also empty when the tier IS the action. A never-used seat that cannot be judged yet is recommended
 * "Review before reclaim" from the "Review before reclaim" tier, and an excluded seat is "Excluded from
 * reclaim" in both, so the line would repeat the badge word for word. An expired exclusion is the one
 * thing the badge cannot say, so that still gets a line of its own.
 */
function reclaimTierLine(t: TFunction, row: LicensedUserAdoptionRow, tierText: string): string {
  if (!tierText) return '';
  const repeatsAction = row.reclaimEligibility === row.recommendedActionCode;
  if (row.reclaimExclusionExpired) {
    return repeatsAction
      ? t('copilotAdoptionUsers.licensed.exclusionExpired')
      : t('copilotAdoptionUsers.licensed.reclaimTierExpired', { tier: tierText });
  }
  return repeatsAction ? '' : tierText;
}

/**
 * Cowork use as a short value rather than a sentence.
 *
 * Cowork use comes from the Copilot audit log only, and it counts interactions, never tasks (#692). The
 * qualifier is abbreviated so the column stays one line wide: "Yes (312 audit interactions)" was wide
 * enough on its own to push the pinned Action column over the top of it.
 */
function coworkCell(t: TFunction, row: LicensedUserAdoptionRow): string {
  if (!row.usedCowork) return t('copilotAdoptionUsers.licensed.coworkNo');
  return t('copilotAdoptionUsers.licensed.coworkAudited', { count: formatCount(row.coworkInteractions) });
}

function sourceComparisonText(
  t: TFunction,
  row: LicensedUserAdoptionRow,
  windowDays: number,
  dataSources?: AdoptionDataSources,
): string {
  const reportPeriod = dataSources?.copilotUsageReportPeriodDays
    ? `D${dataSources.copilotUsageReportPeriodDays}`
    : t('copilotAdoptionUsers.licensed.microsoftWindow');
  const snapshotDate = dataSources?.copilotUsageReportDate
    ? t('copilotAdoptionUsers.licensed.sourceComparisonSnapshotDate', { date: formatDate(dataSources.copilotUsageReportDate) })
    : '';

  return t('copilotAdoptionUsers.licensed.sourceComparison', {
    windowDays,
    auditInteractions: formatCount(row.auditInteractions),
    auditDays: formatCount(row.auditActiveDays),
    reportPeriod,
    snapshotDate,
    prompts: row.reportPrompts === null ? '—' : formatCount(row.reportPrompts),
    reportDays: row.reportActiveDays === null ? '—' : formatCount(row.reportActiveDays),
  });
}
