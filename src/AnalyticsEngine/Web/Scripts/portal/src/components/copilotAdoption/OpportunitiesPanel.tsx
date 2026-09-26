import { useEffect, useMemo, useRef, useState, Fragment } from 'react';
import {
  makeStyles,
  tokens,
  Text,
  Card,
  Input,
  Select,
  Checkbox,
  Button,
  Badge,
  MessageBar,
  MessageBarBody,
  Tab,
  TabList,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArrowDownload16Regular,
  ArrowClockwise16Regular,
  Clock20Regular,
  PeopleList20Regular,
} from '@fluentui/react-icons';
import { fetchOpportunities, opportunitiesExportUrl } from '../../api/copilotAdoptionApi';
import type {
  AdoptionFilterOptions,
  AdoptionGuidanceLink,
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  LicenceOpportunityPage,
  LicenceOpportunityRow,
  OpportunityFilters,
} from '../../types/copilotAdoption';
import Spinner from '../Spinner';
import {
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
  SortableTh,
  printedSearch,
  revealElement,
  useAdoptionTableStyles,
  useRowExpansion,
} from './adoptionShared';
import { usePrintAllRows } from '../shared/printPreparation';
import { formatCount, formatDate } from '../shared/KpiGrid';
import { useT, useTNode } from '../../i18n';
import { copilotAdoptionWarningText, isLicenceOpportunityWarning, opportunityRationale, opportunityTierLabel } from './serverText';
import LicenceTimeSavedHero from './LicenceTimeSavedHero';
import LicenceTimeSavedModel from './LicenceTimeSavedModel';
import { useTimeSavedAssumptions } from './coworkTimeSaved';

const PAGE_SIZE = 50;

/**
 * The tab's sections. The candidate list is the tab's purpose and opens first; the licence estimate's
 * working and evidence sit beside it rather than above it, so fifty rows are never pushed below three
 * evidence cards and a sense check.
 */
type OpportunitySection = 'candidates' | 'timeSaved';

/**
 * The default sort. Strongest case first, with proven-demand candidates ahead of merely busy ones.
 * Every column is sortable from its own header, so the old sort drop-down was a second way to do the
 * same thing.
 */
const DEFAULT_SORT_BY = 'score';

const useStyles = makeStyles({
  sectionNav: {
    marginBottom: '12px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke2,
    scrollMarginTop: '12px',
  },
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
    overflowX: 'auto',
    // Makes this scrollport the container an expanded row's detail panel is sized against. Sizing
    // that panel from the viewport instead over-measures by whatever the left navigation and page
    // padding take, so part of it stayed clipped on a normal laptop.
    containerType: 'inline-size',
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
  upn: {
    display: 'flex',
    flexDirection: 'column',
  },
  evidence: {
    color: tokens.colorNeutralForegroundOnBrand,
    backgroundColor: '#107c10',
    whiteSpace: 'nowrap',
  },
  warnings: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    marginBottom: '12px',
  },
  guidance: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'baseline',
    gap: '6px',
    marginBottom: '12px',
    color: tokens.colorNeutralForeground3,
  },
  // A bare <a> does not inherit Fluent's Text sizing, so without an explicit size it rendered at the
  // browser default and towered over the label next to it.
  guidanceLink: {
    color: tokens.colorBrandForegroundLink,
    fontSize: tokens.fontSizeBase200,
    lineHeight: tokens.lineHeightBase200,
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '10px',
    maxWidth: '760px',
    padding: '8px 0 4px',
  },
  emptyList: {
    margin: 0,
    paddingLeft: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    color: tokens.colorNeutralForeground2,
  },
});

const DEFAULT_FILTERS: OpportunityFilters = {
  search: '',
  department: '',
  country: '',
  emailDomain: '',
  recommendedOnly: false,
  existingCopilotUsersOnly: false,
  sortBy: DEFAULT_SORT_BY,
  sortDesc: true,
};

/**
 * The licence-opportunity list: users with no Copilot seat, ranked by how strong the business case
 * for giving them one is.
 *
 * The "already using Copilot Chat" badge is the single most persuasive thing on this screen - it is
 * evidence of demand rather than an inference from general Microsoft 365 activity - so it is
 * surfaced as its own column and its own filter rather than being buried in the score.
 *
 * Above the list sits the time a licence could give back to the people it recommends: the figure a
 * licence purchase is justified with, and the only place the Copilot minutes are applied. Its working
 * and the published evidence behind it are one section away.
 */
export default function OpportunitiesPanel({
  windowDays,
  summary,
  filterOptions,
  options,
  guidanceLinks,
  seatLicenceTypeIds,
  emailDomain,
}: {
  windowDays: number;
  /** The analysis the licence estimate is published on, and whose assumptions the reader can change. */
  summary: CopilotAdoptionSummary;
  filterOptions: AdoptionFilterOptions | null;
  /** The weights and targets actually used, so the score explanation quotes them rather than guessing. */
  options: CopilotAdoptionOptions;
  guidanceLinks?: AdoptionGuidanceLink[];
  seatLicenceTypeIds?: number[];
  /**
   * The page-wide email-domain filter, applied to this list too so it can never describe a
   * different population from the rest of the report.
   */
  emailDomain?: string | null;
}) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();
  const t = useT();
  const tNode = useTNode();

  // Mirrors CopilotAdoptionScoring.OpportunityCopilotTargetForWindow. The Copilot component is the only
  // one of the four that is a raw total rather than a per-active-day average, so it is scaled from its
  // basis period to the selected window. Shown as the computation rather than a rounded product: a
  // rounded denominator puts the published arithmetic on the wrong side of the recommendation bar for a
  // candidate sitting exactly on it. `approx` is for prose only, never for the formula.
  const opportunityCopilotTargetExpression = `${options.opportunityCopilotTarget} x ${windowDays} / ${options.opportunityCopilotTargetBasisDays}`;
  const opportunityCopilotTargetApprox =
    Math.round(
      Math.max(
        1,
        (options.opportunityCopilotTarget * Math.max(1, windowDays)) /
          Math.max(1, options.opportunityCopilotTargetBasisDays),
      ) * 10,
    ) / 10;

  const [filters, setFilters] = useState<OpportunityFilters>({
    ...DEFAULT_FILTERS,
    emailDomain: emailDomain ?? '',
  });

  /**
   * Resets the panel's own filters while KEEPING the page-wide email-domain scope.
   *
   * The domain is not one of this panel's filters - it is the population the whole report is
   * describing, and the banner at the top of the page says so. Clearing it here would silently
   * widen the list back to the whole tenant while the page still claimed to be showing one
   * organisation, and the CSV export built from the same state would follow it.
   */
  const clearPanelFilters = () => {
    setSearchDraft('');
    setFilters({ ...DEFAULT_FILTERS, emailDomain: emailDomain ?? '' });
  };
  const [searchDraft, setSearchDraft] = useState('');
  const [page, setPage] = useState(0);
  const [data, setData] = useState<LicenceOpportunityPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const { isExpanded, toggle: toggleRow, resetRows, expandAll, collapseAll, allExpanded } = useRowExpansion();

  const timeSaved = useTimeSavedAssumptions(summary);
  const [section, setSection] = useState<OpportunitySection>('candidates');
  // Requests, not flags: each click must act again, including a second click on a section that is
  // already open - which is exactly when a plain setSection() changes nothing the reader can see.
  const [assumptionFocusRequest, setAssumptionFocusRequest] = useState(0);
  const [sectionRevealRequest, setSectionRevealRequest] = useState(0);
  const sectionNavRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (sectionRevealRequest) revealElement(sectionNavRef.current);
  }, [sectionRevealRequest]);

  /** Shows exactly the people the headline counts: the recommended candidates. */
  const showRecommended = () => {
    setSearchDraft('');
    setFilters((f) => ({ ...f, recommendedOnly: true }));
    setSection('candidates');
    setSectionRevealRequest((n) => n + 1);
  };

  /** Takes the reader to the editable figures - from either section, including the one already open. */
  const adjustAssumptions = () => {
    setSection('timeSaved');
    setAssumptionFocusRequest((n) => n + 1);
  };

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

    fetchOpportunities(windowDays, filters, page * PAGE_SIZE, PAGE_SIZE, seatLicenceTypeIds, controller.signal)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((e: any) => {
        if (cancelled || controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : t('copilotAdoptionUsers.opportunities.loadError'));
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
    () => opportunitiesExportUrl(windowDays, filters, seatLicenceTypeIds),
    [windowDays, filters, seatLicenceTypeIds],
  );

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  // With a licence estimate the tab is sectioned, and a list in the section that is not showing is
  // not printed - so it must not hold the printout up, or refuse it for being long.
  const sectioned = (summary?.licenceOpportunityEstimate?.cohortUsers ?? 0) > 0;
  const printRows = usePrintAllRows<LicenceOpportunityRow>({
    enabled: (!sectioned || section === 'candidates') && !loading && data !== null,
    total: data?.total ?? 0,
    loadedRows: data?.rows.length ?? 0,
    loadPage: (skip, take, signal) =>
      fetchOpportunities(windowDays, filters, skip, take, seatLicenceTypeIds, signal),
  });
  const rows = printRows ?? data?.rows ?? [];

  const filtersActive =
    filters.search !== '' ||
    filters.department !== '' ||
    filters.country !== '' ||
    filters.recommendedOnly ||
    filters.existingCopilotUsersOnly;

  // Only the warnings that explain an empty or thin candidate list. The page header already carries
  // the full set, and repeating all of them here would bury the one that answers "why is this empty?".
  const relevantWarnings = (data?.warnings ?? [])
    .map((warning, index) => ({ warning, detail: data?.warningDetails?.[index] }))
    .filter(({ detail }) => isLicenceOpportunityWarning(detail));
  const unlicensedGuidance = (guidanceLinks ?? []).filter((l) => l.actionCode === 'unlicensed');

  const list = (
    <Card>
      {/* Chrome: nothing here can be used on paper. What it is set to is printed below instead. */}
      <div className={styles.filters} data-print="hide">
        <Input
          className={styles.grow}
          value={searchDraft}
          placeholder={t('copilotAdoptionUsers.common.searchPlaceholder')}
          aria-label={t('copilotAdoptionUsers.opportunities.searchAria')}
          onChange={(_e: any, d: any) => setSearchDraft(d.value)}
          onKeyDown={(e: any) => {
            if (e.key === 'Enter') setFilters((f) => ({ ...f, search: searchDraft }));
          }}
        />
        <Button size="small" onClick={() => setFilters((f) => ({ ...f, search: searchDraft }))}>
          {t('copilotAdoptionUsers.common.search')}
        </Button>

        <Select
          value={filters.department}
          aria-label={t('copilotAdoptionUsers.opportunities.filterDepartmentAria')}
          onChange={(_e: any, d: any) => setFilters((f) => ({ ...f, department: d.value }))}
        >
          <option value="">{t('copilotAdoptionUsers.common.allDepartments')}</option>
          {(filterOptions?.departments ?? []).map((dept) => (
            <option key={dept} value={dept}>
              {dept}
            </option>
          ))}
        </Select>

        <Checkbox
          label={t('copilotAdoptionUsers.opportunities.recommendedOnly')}
          checked={filters.recommendedOnly}
          onChange={(_e: any, d: any) => setFilters((f) => ({ ...f, recommendedOnly: !!d.checked }))}
        />
        <Tooltip
          content={t('copilotAdoptionUsers.opportunities.alreadyUsingTooltip')}
          relationship="description"
        >
          <Checkbox
            label={t('copilotAdoptionUsers.opportunities.alreadyUsingFilter')}
            checked={filters.existingCopilotUsersOnly}
            onChange={(_e: any, d: any) => setFilters((f) => ({ ...f, existingCopilotUsersOnly: !!d.checked }))}
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
            label: t('copilotAdoptionUsers.common.department'),
            value: filters.department || t('copilotAdoptionUsers.common.allDepartments'),
          },
          filters.recommendedOnly && { value: t('copilotAdoptionUsers.opportunities.recommendedOnly') },
          filters.existingCopilotUsersOnly && { value: t('copilotAdoptionUsers.opportunities.alreadyUsingFilter') },
        ]}
      />

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {!loading && relevantWarnings.length > 0 && (
        <div className={styles.warnings}>
          {relevantWarnings.map(({ warning, detail }) => (
            <MessageBar key={`${detail?.key ?? warning}:${warning}`} intent="warning">
              <MessageBarBody>{copilotAdoptionWarningText(t, detail, warning)}</MessageBarBody>
            </MessageBar>
          ))}
        </div>
      )}

      {unlicensedGuidance.length > 0 && (
        <div className={styles.guidance}>
          <Text size={200}>{t('copilotAdoptionUsers.opportunities.guidanceIntro')}</Text>
          {unlicensedGuidance.map((link) => (
            <a key={link.url} className={styles.guidanceLink} href={link.url} target="_blank" rel="noreferrer">
              {link.title}
            </a>
          ))}
        </div>
      )}

      {loading && (
        <div style={{ textAlign: 'center', padding: '28px' }}>
          <Spinner size={56} label={t('copilotAdoptionUsers.opportunities.loading')} />
        </div>
      )}

      {!loading && data && data.rows.length === 0 && (
        <div className={styles.emptyState}>
          {filtersActive ? (
            <>
              <Text weight="semibold" block>
                {t('copilotAdoptionUsers.opportunities.noMatches')}
              </Text>
              <Text size={200} block className={styles.muted}>
                {data.total === 0
                  ? t('copilotAdoptionUsers.opportunities.clearFiltersNoCandidates')
                  : t('copilotAdoptionUsers.opportunities.candidatesFoundNoMatches', { count: formatCount(data.total) })}
              </Text>
              <Button size="small" onClick={clearPanelFilters} data-print="hide">
                {t('copilotAdoptionUsers.opportunities.clearFilters')}
              </Button>
            </>
          ) : (
            <>
              <Text weight="semibold" block>
                {t('copilotAdoptionUsers.opportunities.noneQualified')}
              </Text>
              <Text size={200} block className={styles.muted}>
                {tNode('copilotAdoptionUsers.opportunities.emptyIntro', {
                  count: <strong>{t('copilotAdoptionUsers.opportunities.emptyIntroAll')}</strong>,
                })}
              </Text>
              <ul className={styles.emptyList}>
                <li>
                  <Text size={200}>{t('copilotAdoptionUsers.opportunities.emptySkuRequirement')}</Text>
                </li>
                <li>
                  <Text size={200}>{t('copilotAdoptionUsers.opportunities.emptyEnabledRequirement')}</Text>
                </li>
                <li>
                  <Text size={200}>
                    {tNode('copilotAdoptionUsers.opportunities.emptyActivity', {
                      either: <strong>{t('copilotAdoptionUsers.opportunities.emptyActivityEither')}</strong>,
                      or: <strong>{t('copilotAdoptionUsers.opportunities.emptyActivityOr')}</strong>,
                    })}
                  </Text>
                </li>
              </ul>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoptionUsers.opportunities.emptyUsageReportsMissing')}
              </Text>
            </>
          )}
        </div>
      )}

      {!loading && data && data.rows.length > 0 && (
        <div className={styles.tableWrap}>
          <table className={table.table}>
            <thead>
              <tr>
                <SortableTh label={t('copilotAdoptionUsers.common.userSortLabel')} sortKey="upn" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort} className={table.stickyLeft}>
                  {t('copilotAdoptionUsers.common.user')}
                </SortableTh>
                <SortableTh label={t('copilotAdoptionUsers.common.departmentSortLabel')} sortKey="department" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort}>
                  {t('copilotAdoptionUsers.common.department')}
                </SortableTh>
                <SortableTh
                  label={t('copilotAdoptionUsers.opportunities.businessCaseLabel')}
                  sortKey="score"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  defaultDescending
                  infoTitle={t('copilotAdoptionUsers.opportunities.businessCaseTitle')}
                  info={{
                    what: t('copilotAdoptionUsers.opportunities.businessCaseWhat', {
                      days: options.opportunityProvenDemandMinActiveDays,
                      score: options.opportunityRecommendScore,
                    }),
                    how: t('copilotAdoptionUsers.opportunities.businessCaseHow', {
                      copilotWeight: options.opportunityUnlicensedCopilotWeight,
                      collaborationWeight: options.opportunityCollaborationWeight,
                      emailWeight: options.opportunityEmailWeight,
                      documentWeight: options.opportunityDocumentWeight,
                    }),
                    formula: t('copilotAdoptionUsers.opportunities.businessCaseFormula', {
                      copilotTarget: opportunityCopilotTargetExpression,
                      collaborationTarget: options.opportunityCollaborationTarget,
                      emailTarget: options.opportunityEmailTarget,
                      documentTarget: options.opportunityDocumentTarget,
                      copilotWeight: options.opportunityUnlicensedCopilotWeight,
                      collaborationWeight: options.opportunityCollaborationWeight,
                      emailWeight: options.opportunityEmailWeight,
                      documentWeight: options.opportunityDocumentWeight,
                      provenDemandDays: options.opportunityProvenDemandMinActiveDays,
                      recommendScore: options.opportunityRecommendScore,
                    }),
                    source: t('copilotAdoptionUsers.opportunities.businessCaseSource', {
                      target: options.opportunityCopilotTarget,
                      basisDays: options.opportunityCopilotTargetBasisDays,
                      approx: opportunityCopilotTargetApprox,
                    }),
                  }}
                >
                  {t('copilotAdoptionUsers.opportunities.businessCaseHeader')}
                </SortableTh>
                <SortableTh
                  label={t('copilotAdoptionUsers.opportunities.unlicensedUseLabel')}
                  sortKey="copilot"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  defaultDescending
                  infoTitle={t('copilotAdoptionUsers.opportunities.alreadyUsingTitle')}
                  info={{
                    what: t('copilotAdoptionUsers.opportunities.alreadyUsingWhat'),
                    how: t('copilotAdoptionUsers.opportunities.alreadyUsingHow'),
                    source: t('copilotAdoptionUsers.opportunities.alreadyUsingSource'),
                  }}
                >
                  {t('copilotAdoptionUsers.opportunities.alreadyUsingTitle')}
                </SortableTh>
                <SortableTh label={t('copilotAdoptionUsers.opportunities.teamsActivityLabel')} sortKey="collaboration" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort} numeric defaultDescending>
                  {t('copilotAdoptionUsers.opportunities.teamsPerDayHeader')}
                </SortableTh>
                <SortableTh label={t('copilotAdoptionUsers.opportunities.emailActivityLabel')} sortKey="email" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort} numeric defaultDescending>
                  {t('copilotAdoptionUsers.opportunities.emailPerDayHeader')}
                </SortableTh>
                <SortableTh label={t('copilotAdoptionUsers.opportunities.documentActivityLabel')} sortKey="documents" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort} numeric defaultDescending>
                  {t('copilotAdoptionUsers.opportunities.filesPerDayHeader')}
                </SortableTh>
                <SortableTh label={t('copilotAdoptionUsers.opportunities.lastM365Label')} sortKey="lastM365" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort}>
                  {t('copilotAdoptionUsers.opportunities.lastM365Header')}
                </SortableTh>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const open = isExpanded(row.userId);
                return (
                  <Fragment key={row.userId}>
                    <tr>
                      <ExpandableUserCell
                        open={open}
                        onToggle={() => toggleRow(row.userId)}
                        userPrincipalName={row.userPrincipalName}
                        secondary={row.jobTitle || row.mail}
                        className={table.stickyLeft}
                      />
                      <td className={`${table.td} ${table.tdNoWrap}`}>{row.department || '\u2014'}</td>
                      <td className={table.td}>
                        <Tooltip
                          relationship="description"
                          content={t('copilotAdoptionUsers.opportunities.scoreTooltip', {
                            copilot: Math.round(row.copilotDemandScore),
                            collaboration: Math.round(row.collaborationScore),
                            email: Math.round(row.emailScore),
                            documents: Math.round(row.documentScore),
                          })}
                        >
                          <div>
                            <ScoreBar score={row.opportunityScore} />
                          </div>
                        </Tooltip>
                      </td>
                      <td className={`${table.td} ${table.tdNoWrap}`}>
                        {row.unlicensedCopilotInteractions > 0 ? (
                          <Badge className={styles.evidence} size="small">
                            {t('copilotAdoptionUsers.opportunities.unlicensedBadge', { interactions: formatCount(row.unlicensedCopilotInteractions), days: row.unlicensedCopilotActiveDays })}
                          </Badge>
                        ) : (
                          <Text size={200} className={styles.muted}>
                            {t('copilotAdoptionUsers.opportunities.notYet')}
                          </Text>
                        )}
                      </td>
                      <td className={`${table.td} ${table.tdNumeric}`}>
                        {formatCount(row.teamsMessages)}
                        <Text size={100} block className={table.tdSub}>
                          {t('copilotAdoptionUsers.opportunities.meetingsAbbrev', { count: formatCount(row.teamsMeetings) })}
                        </Text>
                      </td>
                      <td className={`${table.td} ${table.tdNumeric}`}>
                        {formatCount(row.emailsSent)}
                        <Text size={100} block className={table.tdSub}>
                          {t('copilotAdoptionUsers.opportunities.readAbbrev', { count: formatCount(row.emailsRead) })}
                        </Text>
                      </td>
                      <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.filesViewedOrEdited)}</td>
                      <td className={`${table.td} ${table.tdNoWrap}`}>{formatDate(row.lastM365ActivityUtc)}</td>
                    </tr>
                    {open && (
                      <DetailRow colSpan={8}>
                        <DetailSections>
                          <DetailSection title={t('copilotAdoptionUsers.opportunities.businessCaseDetailTitle', { score: Math.round(row.opportunityScore) })}>
                            <DetailStats>
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.copilotDemand')}
                                value={Math.round(row.copilotDemandScore)}
                                sub={t('copilotAdoptionUsers.opportunities.weightSub', { weight: options.opportunityUnlicensedCopilotWeight })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.collaboration')}
                                value={Math.round(row.collaborationScore)}
                                sub={t('copilotAdoptionUsers.opportunities.weightSub', { weight: options.opportunityCollaborationWeight })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.email')}
                                value={Math.round(row.emailScore)}
                                sub={t('copilotAdoptionUsers.opportunities.weightSub', { weight: options.opportunityEmailWeight })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.documents')}
                                value={Math.round(row.documentScore)}
                                sub={t('copilotAdoptionUsers.opportunities.weightSub', { weight: options.opportunityDocumentWeight })}
                              />
                            </DetailStats>
                          </DetailSection>

                          <DetailSection title={t('copilotAdoptionUsers.opportunities.m365ActivityTitle')}>
                            <DetailStats>
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.teamsMessages')}
                                value={formatCount(row.teamsMessages)}
                                sub={t('copilotAdoptionUsers.opportunities.withMeetingsTarget', { meetings: formatCount(row.teamsMeetings), target: options.opportunityCollaborationTarget })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.emailsSent')}
                                value={formatCount(row.emailsSent)}
                                sub={t('copilotAdoptionUsers.opportunities.readTarget', { read: formatCount(row.emailsRead), target: options.opportunityEmailTarget })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.files')}
                                value={formatCount(row.filesViewedOrEdited)}
                                sub={t('copilotAdoptionUsers.opportunities.viewedOrEditedTarget', { target: options.opportunityDocumentTarget })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.lastM365Header')}
                                value={formatDate(row.lastM365ActivityUtc)}
                              />
                            </DetailStats>
                          </DetailSection>

                          <DetailSection title={t('copilotAdoptionUsers.opportunities.unlicensedUseTitle')}>
                            <DetailStats>
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.interactions')}
                                value={formatCount(row.unlicensedCopilotInteractions)}
                                sub={t('copilotAdoptionUsers.opportunities.targetThisPeriod', { target: opportunityCopilotTargetApprox })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.activeDays')}
                                value={formatCount(row.unlicensedCopilotActiveDays)}
                                sub={t('copilotAdoptionUsers.opportunities.provenDemandAt', { days: options.opportunityProvenDemandMinActiveDays })}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.lastCopilotUse')}
                                value={formatDate(row.lastCopilotInteractionUtc)}
                              />
                              <DetailStat
                                label={t('copilotAdoptionUsers.opportunities.qualifiedBy')}
                                value={
                                  opportunityTierLabel(t, row.qualificationTier, row.qualificationTierLabel)
                                  || (row.recommended ? t('copilotAdoptionUsers.opportunities.recommended') : t('copilotAdoptionUsers.opportunities.notRecommended'))
                                }
                                sub={t('copilotAdoptionUsers.opportunities.recommendAt', { score: options.opportunityRecommendScore })}
                              />
                            </DetailStats>
                          </DetailSection>
                        </DetailSections>

                        <DetailSection
                          title={t('copilotAdoptionUsers.opportunities.justificationTitle')}
                          info={{
                            what: t('copilotAdoptionUsers.opportunities.justificationWhat'),
                            how: t('copilotAdoptionUsers.opportunities.justificationHow'),
                            source: t('copilotAdoptionUsers.opportunities.justificationSource'),
                          }}
                        >
                          <DetailRationale text={opportunityRationale(t, row)} />
                        </DetailSection>
                      </DetailRow>
                    )}
                  </Fragment>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {!loading && data && data.total > 0 && (
        <div className={styles.footer}>
          <Text size={200} className={styles.muted}>
            {t('copilotAdoptionUsers.opportunities.showingCandidates', {
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
              <Button size="small" disabled={page + 1 >= totalPages} onClick={() => setPage((p) => p + 1)}>
                {t('copilotAdoptionUsers.common.next')}
              </Button>
            </div>
          )}
        </div>
      )}
      {!loading && data && <PartialPrintNote shownRows={rows.length} totalRows={data.total} />}
    </Card>
  );

  // No estimate - nobody recommended, or no Microsoft 365 usage reports to model from - means no
  // headline and nothing to show the working for: the list alone, exactly as before.
  if (!((summary?.licenceOpportunityEstimate?.cohortUsers ?? 0) > 0)) return list;

  return (
    <div>
      {/* ---------- The headline ---------- */}
      <LicenceTimeSavedHero
        summary={summary}
        options={options}
        timeSaved={timeSaved}
        onAdjust={adjustAssumptions}
        onShowRecommended={showRecommended}
      />

      <div className={styles.sectionNav} data-print="hide" ref={sectionNavRef}>
        <TabList
          selectedValue={section}
          onTabSelect={(_e, d) => setSection(d.value as OpportunitySection)}
          aria-label={t('copilotAdoptionUsers.opportunities.sections.ariaLabel')}
        >
          <Tab value="candidates" icon={<PeopleList20Regular />}>
            {t('copilotAdoptionUsers.opportunities.sections.candidates')}
          </Tab>
          <Tab value="timeSaved" icon={<Clock20Regular />}>
            {t('copilotAdoptionUsers.opportunities.sections.timeSaved')}
          </Tab>
        </TabList>
      </div>

      <div role="tabpanel" aria-label={t('copilotAdoptionUsers.opportunities.sections.candidates')} hidden={section !== 'candidates'}>
        {list}
      </div>

      <div role="tabpanel" aria-label={t('copilotAdoptionUsers.opportunities.sections.timeSaved')} hidden={section !== 'timeSaved'}>
        <LicenceTimeSavedModel
          summary={summary}
          options={options}
          timeSaved={timeSaved}
          focusRequest={assumptionFocusRequest}
        />
      </div>
    </div>
  );
}
