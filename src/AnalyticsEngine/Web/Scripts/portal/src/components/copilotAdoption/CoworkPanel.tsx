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
  ArrowTrendingLines20Regular,
  Clock20Regular,
  DataScatter20Regular,
  PeopleList20Regular,
} from '@fluentui/react-icons';
import { fetchCowork, coworkExportUrl } from '../../api/copilotAdoptionApi';
import type {
  AdoptionFilterOptions,
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  CoworkBasis,
  CoworkFilters,
  CoworkReadinessPage,
  CoworkReadinessRow,
  CoworkTier,
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
  printedSearch,
  revealElement,
  useAdoptionTableStyles,
  useRowExpansion,
} from './adoptionShared';
import { usePrintAllRows } from '../shared/printPreparation';
import { formatCount, formatDate } from '../shared/KpiGrid';
import { formatNumber, useT, useTNode, type TFunction, type TranslationKey } from '../../i18n';
// Credits are fractional and a per-user total over a short window is routinely below 1.
// formatCount is documented as a WHOLE-number formatter, so it renders a real 0.4 as "0" -
// the same "we do not know" / "it is nothing" conflation the null path here is careful to
// avoid, and the reason formatCredits exists (agentCostShared.test.ts pins
// formatCredits(0.000125) !== '0'). Every credit figure on this tab uses it.
import { formatCredits } from '../agentCosts/agentCostShared';
import InfoTip from '../shared/InfoTip';
import CoworkQuadrant from './CoworkQuadrant';
import CoworkTimeSavedHero from './CoworkTimeSavedHero';
import CoworkTimeSavedModel from './CoworkTimeSavedModel';
import { useTimeSavedAssumptions } from './coworkTimeSaved';
import { coworkRationaleText, coworkTierLabel } from './serverText';

const PAGE_SIZE = 50;

/**
 * The tab's sections, in order.
 *
 * The tab used to be one long scroll - explanation, tiers, quadrant, rollout table, credits, estimate
 * and a fifty-row list - and the list buried everything above it. Each section now answers one
 * question, under a headline that answers the one everybody asks first.
 */
type CoworkSection = 'timeSaved' | 'readiness' | 'rollout' | 'people';

/**
 * The scheduled / user-initiated split, naming only the halves Microsoft actually reported.
 *
 * These two columns are independently nullable and a blank one means "not reported", not "none".
 * Coercing either to 0 would state a measurement Microsoft never made - the same conflation the
 * per-user credit column goes out of its way to avoid.
 */
function taskSplitLabel(row: CoworkReadinessRow, t: TFunction): string {
  const parts: string[] = [];
  if (row.coworkReportScheduledTasks !== null) {
    parts.push(t('copilotAdoptionCowork.detail.taskSplit.scheduled', { count: formatCount(row.coworkReportScheduledTasks) }));
  }
  if (row.coworkReportUserInitiatedTasks !== null) {
    parts.push(t('copilotAdoptionCowork.detail.taskSplit.userInitiated', { count: formatCount(row.coworkReportUserInitiatedTasks) }));
  }
  return parts.length > 0 ? parts.join(', ') : t('copilotAdoptionCowork.detail.taskSplit.notReported');
}

const SORT_OPTIONS: Array<{ value: string; labelKey: TranslationKey }> = [
  { value: 'load:desc', labelKey: 'copilotAdoptionCowork.sort.mostCoordinationLoad' },
  { value: 'fluency:desc', labelKey: 'copilotAdoptionCowork.sort.mostCopilotFluency' },
  { value: 'meetings:desc', labelKey: 'copilotAdoptionCowork.sort.mostMeetings' },
  { value: 'coworkActiveDays:desc', labelKey: 'copilotAdoptionCowork.sort.mostCoworkUse' },
  { value: 'tier:asc', labelKey: 'copilotAdoptionCowork.sort.coworkTier' },
  { value: 'department:asc', labelKey: 'copilotAdoptionCowork.sort.departmentAz' },
  { value: 'upn:asc', labelKey: 'copilotAdoptionCowork.sort.userNameAz' },
];

const COWORK_TIER_KEYS: Record<CoworkTier, { label: TranslationKey; description: TranslationKey }> = {
  established: {
    label: 'copilotAdoptionCowork.tier.established.label',
    description: 'copilotAdoptionCowork.tier.established.description',
  },
  trialling: {
    label: 'copilotAdoptionCowork.tier.trialling.label',
    description: 'copilotAdoptionCowork.tier.trialling.description',
  },
  primeCandidate: {
    label: 'copilotAdoptionCowork.tier.primeCandidate.label',
    description: 'copilotAdoptionCowork.tier.primeCandidate.description',
  },
  buildFluencyFirst: {
    label: 'copilotAdoptionCowork.tier.buildFluencyFirst.label',
    description: 'copilotAdoptionCowork.tier.buildFluencyFirst.description',
  },
  lowCoordinationLoad: {
    label: 'copilotAdoptionCowork.tier.lowCoordinationLoad.label',
    description: 'copilotAdoptionCowork.tier.lowCoordinationLoad.description',
  },
  notIndicated: {
    label: 'copilotAdoptionCowork.tier.notIndicated.label',
    description: 'copilotAdoptionCowork.tier.notIndicated.description',
  },
};

function coworkTierText(
  t: TFunction,
  code: string,
  field: 'label' | 'description',
  fallback: string,
  days: string,
): string {
  if (!(code in COWORK_TIER_KEYS)) return fallback;
  const catalogKey = COWORK_TIER_KEYS[code as CoworkTier][field];
  const translated = t(catalogKey, { days });
  return translated === catalogKey ? fallback : translated;
}

const useStyles = makeStyles({
  section: {
    marginBottom: '16px',
  },
  sectionNav: {
    marginBottom: '12px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke2,
    scrollMarginTop: '12px',
  },
  sectionHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    marginBottom: '2px',
  },
  sectionNote: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginBottom: '10px',
    maxWidth: '900px',
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
  inference: {
    whiteSpace: 'nowrap',
  },
  thWithInfo: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '2px',
  },
  warnings: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    marginBottom: '12px',
  },
  tierGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(230px, 1fr))',
    gap: '10px',
    marginBottom: '12px',
  },
  tierCard: {
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: '10px 12px',
    cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground1,
    textAlign: 'start',
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  tierCardActive: {
    border: `1px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorBrandBackground2,
  },
  tierTop: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
  },
  tierCount: {
    fontSize: '22px',
    fontWeight: 700,
    fontVariantNumeric: 'tabular-nums',
  },
  tierDesc: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginTop: '4px',
  },
  creditGrid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))',
    gap: '10px',
  },
  creditCell: {
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: '10px 12px',
  },
  creditValue: {
    fontSize: '20px',
    fontWeight: 600,
    fontVariantNumeric: 'tabular-nums',
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: '10px',
    maxWidth: '760px',
    padding: '8px 0 4px',
  },
});

const DEFAULT_FILTERS: CoworkFilters = {
  search: '',
  tiers: [],
  department: '',
  country: '',
  emailDomain: '',
  recommendedOnly: false,
  coworkUsersOnly: false,
  sortBy: 'load',
  sortDesc: true,
};

function BasisBadge({ basis }: { basis: CoworkBasis }) {
  const styles = useStyles();
  const t = useT();

  // Spelled out rather than shortened to "Observed"/"Predicted" alone, because this badge is the one
  // thing stopping a reader treating a forecast as a fact.
  return basis === 'evidence' ? (
    <Tooltip relationship="description" content={t('copilotAdoptionCowork.basis.observedTooltip')}>
      <Badge className={styles.evidence} size="small">
        {t('copilotAdoptionCowork.basis.observed')}
      </Badge>
    </Tooltip>
  ) : (
    <Tooltip relationship="description" content={t('copilotAdoptionCowork.basis.predictedTooltip')}>
      <Badge className={styles.inference} size="small" appearance="outline" color="informative">
        {t('copilotAdoptionCowork.basis.predicted')}
      </Badge>
    </Tooltip>
  );
}

/**
 * The Cowork tab.
 *
 * Opens on its headline - how much time Copilot and Cowork could give back, with the working one
 * click away - and then splits into four sections that each answer one question: how the time-saved
 * model works and what the evidence is, who is ready, where a rollout should start, and who to put
 * in the spending policy.
 *
 * Cowork has no licence of its own: it needs a Microsoft 365 Copilot licence as a prerequisite and is
 * then billed by consumption against Copilot Credits, with access granted by a spending policy scoped
 * to users or groups. So the people section is not "who should we buy something for" - it is "who
 * should we put in that policy", and every affordance there is built around producing that list.
 *
 * The evidence/inference split is the load-bearing idea. Two of the six tiers are observed; four are
 * predictions; and the time saved is a model. Presenting either of the last two as the first would be
 * the most damaging thing this page could do, so the distinction is a column, a badge, a filter and a
 * caption rather than a footnote.
 */
export default function CoworkPanel({
  windowDays,
  summary,
  filterOptions,
  options,
  seatLicenceTypeIds,
  emailDomain,
}: {
  windowDays: number;
  summary: CopilotAdoptionSummary;
  filterOptions: AdoptionFilterOptions | null;
  options: CopilotAdoptionOptions;
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

  const [filters, setFilters] = useState<CoworkFilters>({ ...DEFAULT_FILTERS, emailDomain: emailDomain ?? '' });

  /**
   * Resets the panel's own filters while KEEPING the page-wide email-domain scope.
   *
   * The domain is not one of this panel's filters - it is the population the whole report is
   * describing, and the banner at the top of the page says so. Clearing it here would silently
   * widen the list back to the whole tenant while the page still claimed to be showing one
   * organisation, and the spending-policy CSV built from the same state would follow it - which on
   * this tab means handing an admin a list of people to grant Cowork to who are not in the
   * organisation they were looking at.
   */
  const clearPanelFilters = () => {
    setSearchDraft('');
    setFilters({ ...DEFAULT_FILTERS, emailDomain: emailDomain ?? '' });
  };
  const [searchDraft, setSearchDraft] = useState('');
  const [page, setPage] = useState(0);
  const [data, setData] = useState<CoworkReadinessPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const { isExpanded, toggle: toggleRow, resetRows, expandAll, collapseAll, allExpanded } = useRowExpansion();
  const [section, setSection] = useState<CoworkSection>('timeSaved');
  const timeSaved = useTimeSavedAssumptions(summary);
  // Requests, not flags: each click must act again, including a second click on a section that is
  // already open - which is exactly when a plain setSection() changes nothing the reader can see.
  const [assumptionFocusRequest, setAssumptionFocusRequest] = useState(0);
  const [sectionRevealRequest, setSectionRevealRequest] = useState(0);
  const sectionNavRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (sectionRevealRequest) revealElement(sectionNavRef.current);
  }, [sectionRevealRequest]);

  const available = summary.coworkReadinessAvailable;

  useEffect(() => setPage(0), [filters, windowDays]);

  // Paging or re-filtering replaces the rows under an open detail, so the expander would end up
  // describing whoever happens to land on that line next. "Expand all" survives it: that is a
  // choice about the whole list, not about the rows that happened to be on screen.
  useEffect(() => resetRows(), [filters, windowDays, page, resetRows]);

  useEffect(() => {
    // Nothing to fetch when the analysis did not run: the rows cannot exist, and firing the request
    // anyway would put a pointless round trip behind an explanatory message the user is already
    // reading. The guard lives here rather than around the early return below because hooks run
    // unconditionally - returning early does not stop an effect that has already been declared.
    if (!available) {
      setLoading(false);
      return undefined;
    }

    let cancelled = false;
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchCowork(windowDays, filters, page * PAGE_SIZE, PAGE_SIZE, seatLicenceTypeIds, controller.signal)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((e: any) => {
        if (cancelled || controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : t('copilotAdoptionCowork.error.loadReadinessList'));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
      // These requests poll while the analysis is building, so cleanup has to actually stop them.
      controller.abort();
    };
  }, [available, windowDays, filters, page, seatLicenceTypeIds, reloadKey, t]);

  const sortValue = `${filters.sortBy}:${filters.sortDesc ? 'desc' : 'asc'}`;
  const exportUrl = useMemo(
    () => coworkExportUrl(windowDays, filters, seatLicenceTypeIds),
    [windowDays, filters, seatLicenceTypeIds],
  );

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  // The whole list while a print is being produced; the page on screen the rest of the time. Only
  // while the people section is the one showing: a hidden section is not printed, so its list must
  // not hold the printout up or refuse it for being long.
  const printRows = usePrintAllRows<CoworkReadinessRow>({
    enabled: available && section === 'people' && !loading && data !== null,
    total: data?.total ?? 0,
    loadedRows: data?.rows.length ?? 0,
    loadPage: (skip, take, signal) => fetchCowork(windowDays, filters, skip, take, seatLicenceTypeIds, signal),
  });
  const rows = printRows ?? data?.rows ?? [];
  const selectedTier = summary.coworkTiers.find((tier) => tier.code === filters.tiers[0]);
  const sortOption = SORT_OPTIONS.find((o) => o.value === sortValue);

  const credits = summary.coworkCreditPosition;
  const coworkRegularMinActiveDays = formatNumber(Math.max(1, options.coworkRegularMinActiveDays));
  // The detail row spans every column the header renders. The credit figures live inside the detail
  // panel rather than in a column, so this is now fixed.
  const detailColSpan = 8;

  /**
   * Opens the people section, optionally re-filtered - how a tier card or the headline hands the
   * reader the exact list it just counted, now that the list is no longer directly underneath. The
   * section strip is scrolled into view with it, because the list opens below the headline and would
   * otherwise start below the fold on a laptop screen.
   */
  const showPeople = (next?: Partial<CoworkFilters>) => {
    if (next) {
      setSearchDraft('');
      setFilters((f) => ({ ...f, ...next }));
    }
    setSection('people');
    setSectionRevealRequest((n) => n + 1);
  };

  /** Takes the reader to the editable figures - from any section, including the one already open. */
  const adjustAssumptions = () => {
    setSection('timeSaved');
    setAssumptionFocusRequest((n) => n + 1);
  };

  const selectTier = (tier: CoworkTier) => showPeople({ tiers: [tier], recommendedOnly: false });

  if (!available) {
    // Read from the SUMMARY, not from `data`: the effect above deliberately does not fetch when the
    // analysis is unavailable, so `data` is always null on this branch. The summary is a prop and is
    // always present, which is what makes the diagnosis below reachable at all.
    const coworkWarnings = (summary.warnings ?? []).filter((w) =>
      w.toLowerCase().includes('cowork'),
    );

    return (
      <Card>
        <div className={styles.emptyState}>
          <Text weight="semibold" block>
            {t('copilotAdoptionCowork.unavailable.title')}
          </Text>
          {coworkWarnings.length > 0 ? (
            <div className={styles.warnings}>
              {coworkWarnings.map((warning) => (
                <MessageBar key={warning} intent="warning">
                  <MessageBarBody>{warning}</MessageBarBody>
                </MessageBar>
              ))}
            </div>
          ) : (
            <>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoptionCowork.unavailable.requirements')}
              </Text>
              <Text size={200} block className={styles.muted}>
                {t('copilotAdoptionCowork.unavailable.importInstruction')}
              </Text>
            </>
          )}
        </div>
      </Card>
    );
  }

  return (
    <div>
      {/* ---------- The headline ---------- */}
      <CoworkTimeSavedHero
        summary={summary}
        options={options}
        timeSaved={timeSaved}
        onAdjust={adjustAssumptions}
        onShowPeople={() => showPeople({ recommendedOnly: true, tiers: [] })}
      />

      <div className={styles.sectionNav} data-print="hide" ref={sectionNavRef}>
        <TabList
          selectedValue={section}
          onTabSelect={(_e, d) => setSection(d.value as CoworkSection)}
          aria-label={t('copilotAdoptionCowork.sections.ariaLabel')}
        >
          <Tab value="timeSaved" icon={<Clock20Regular />}>
            {t('copilotAdoptionCowork.sections.timeSaved')}
          </Tab>
          <Tab value="readiness" icon={<DataScatter20Regular />}>
            {t('copilotAdoptionCowork.sections.readiness')}
          </Tab>
          <Tab value="rollout" icon={<ArrowTrendingLines20Regular />}>
            {t('copilotAdoptionCowork.sections.rollout')}
          </Tab>
          <Tab value="people" icon={<PeopleList20Regular />}>
            {summary.coworkRecommendedForPolicy > 0
              ? t('copilotAdoptionCowork.sections.peopleWithCount', {
                  count: formatCount(summary.coworkRecommendedForPolicy),
                })
              : t('copilotAdoptionCowork.sections.people')}
          </Tab>
        </TabList>
      </div>

      {/* ---------- Time saved: the model behind the headline ---------- */}
      <div role="tabpanel" aria-label={t('copilotAdoptionCowork.sections.timeSaved')} hidden={section !== 'timeSaved'}>
        <CoworkTimeSavedModel
          summary={summary}
          options={options}
          timeSaved={timeSaved}
          focusRequest={assumptionFocusRequest}
        />
      </div>

      {/* ---------- Readiness: who is ready, and why ---------- */}
      <div role="tabpanel" aria-label={t('copilotAdoptionCowork.sections.readiness')} hidden={section !== 'readiness'}>
      <Card className={styles.section}>
        <Text weight="semibold" block>
          {t('copilotAdoptionCowork.readiness.title')}
        </Text>
        <Text size={200} className={styles.sectionNote}>
          {tNode('copilotAdoptionCowork.intro.twoAxes', {
            coordinationLoad: <strong>{t('copilotAdoptionCowork.metric.coordinationLoad')}</strong>,
            copilotFluency: <strong>{t('copilotAdoptionCowork.metric.copilotFluency')}</strong>,
          })}
        </Text>

        <div className={styles.tierGrid}>
          {summary.coworkTiers.map((tier) => {
            const active = filters.tiers.length === 1 && filters.tiers[0] === tier.code;
            const label = coworkTierText(t, tier.code, 'label', tier.label, coworkRegularMinActiveDays);
            const description = coworkTierText(t, tier.code, 'description', tier.description, coworkRegularMinActiveDays);
            return (
              <button
                key={tier.code}
                type="button"
                className={`${styles.tierCard} ${active ? styles.tierCardActive : ''}`}
                onClick={() => selectTier(tier.code)}
              >
                <div className={styles.tierTop}>
                  <Text size={200} weight="semibold">
                    {label}
                  </Text>
                  <BasisBadge basis={tier.basis} />
                </div>
                <span className={styles.tierCount}>{formatCount(tier.users)}</span>
                <Text size={100} className={styles.tierDesc}>
                  {description}
                </Text>
              </button>
            );
          })}
        </div>
        <Text size={100} className={styles.muted} data-print="hide">
          {t('copilotAdoptionCowork.tiers.openInstruction')}
        </Text>
      </Card>

      {/* ---------- The quadrant ---------- */}
      <Card className={styles.section}>
        <div className={styles.sectionHeader}>
          <Text weight="semibold">{t('copilotAdoptionCowork.departmentReadiness.title')}</Text>
          <InfoTip
            title={t('copilotAdoptionCowork.departmentReadiness.infoTitle')}
            content={{
              what: t('copilotAdoptionCowork.departmentReadiness.info.what'),
              how: t('copilotAdoptionCowork.departmentReadiness.info.how', {
                load: options.coworkLoadMinScore,
                fluency: options.coworkFluencyMinScore,
                meetings: options.coworkMeetingWeight,
                email: options.coworkEmailWeight,
                messages: options.coworkCollaborationWeight,
                documents: options.coworkDocumentWeight,
                uplift: options.coworkAgentFamiliarityUplift,
              }),
              source: t('copilotAdoptionCowork.departmentReadiness.info.source'),
            }}
          />
        </div>
        <Text size={200} className={styles.sectionNote}>
          {t('copilotAdoptionCowork.departmentReadiness.guidance')}
        </Text>
        <CoworkQuadrant points={summary.coworkQuadrant} options={options} />
      </Card>
      </div>

      {/* ---------- Rollout plan: where to start, and the credits it draws on ---------- */}
      <div role="tabpanel" aria-label={t('copilotAdoptionCowork.sections.rollout')} hidden={section !== 'rollout'}>
      {summary.coworkByDepartment.length === 0 && (
        <Card className={styles.section}>
          <Text weight="semibold" block>
            {t('copilotAdoptionCowork.rollout.title')}
          </Text>
          <Text size={200} className={styles.sectionNote}>
            {t('copilotAdoptionCowork.rollout.empty')}
          </Text>
        </Card>
      )}
      {summary.coworkByDepartment.length > 0 && (
        <Card className={styles.section}>
          <Text weight="semibold" block>
            {t('copilotAdoptionCowork.rollout.title')}
          </Text>
          <Text size={200} className={styles.sectionNote}>
            {tNode('copilotAdoptionCowork.rollout.orderByNumber', {
              number: <strong>{t('copilotAdoptionCowork.rollout.number')}</strong>,
            })}
          </Text>
          <div className={styles.tableWrap}>
            <table className={table.table}>
              <thead>
                <tr>
                  <th className={table.th}>{t('copilotAdoptionCowork.table.department')}</th>
                  <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionCowork.table.copilotSeats')}</th>
                  <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionCowork.table.primeCandidates')}</th>
                  <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionCowork.table.alreadyUsingCowork')}</th>
                  <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionCowork.table.avgCoordinationLoad')}</th>
                  <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionCowork.table.avgCopilotFluency')}</th>
                </tr>
              </thead>
              <tbody>
                {summary.coworkByDepartment.map((row) => (
                  <tr key={row.segment}>
                    <td className={table.td}>{row.segment}</td>
                    <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.licensedUsers)}</td>
                    <td className={`${table.td} ${table.tdNumeric}`}>
                      {formatCount(row.primeCandidates)}
                      <Text size={100} block className={table.tdSub}>
                        {t('copilotAdoptionCowork.table.percentOfSeats', { percent: Math.round(row.primeCandidateRatePct) })}
                      </Text>
                    </td>
                    <td className={`${table.td} ${table.tdNumeric}`}>
                      {formatCount(row.regularCoworkUsers)}
                      <Text size={100} block className={table.tdSub}>
                        {row.coworkAutomationRatioPct === null
                          ? '\u2014'
                          : t('copilotAdoptionCowork.table.percentAutomated', {
                              percent: Math.round(row.coworkAutomationRatioPct),
                            })}
                      </Text>
                    </td>
                    <td className={`${table.td} ${table.tdNumeric}`}>
                      {Math.round(row.averageCoordinationLoad)}
                    </td>
                    <td className={`${table.td} ${table.tdNumeric}`}>{Math.round(row.averageFluency)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}

      {/* ---------- Credit headroom ---------- */}
      {credits?.available && (
        <Card className={styles.section}>
          <Text weight="semibold" block>
            {t('copilotAdoptionCowork.creditsHeadroom.title')}
          </Text>
          <Text size={200} className={styles.sectionNote}>
            {tNode('copilotAdoptionCowork.creditsHeadroom.description', {
              sharedPool: <strong>{t('copilotAdoptionCowork.creditsHeadroom.sharedPool')}</strong>,
            })}
          </Text>
          <div className={styles.creditGrid}>
            {credits.entitled !== null && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  {t('copilotAdoptionCowork.creditsHeadroom.entitled')}
                </Text>
                <span className={styles.creditValue}>{formatCredits(credits.entitled)}</span>
              </div>
            )}
            {credits.consumed !== null && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  {t('copilotAdoptionCowork.creditsHeadroom.consumed')}
                </Text>
                <span className={styles.creditValue}>{formatCredits(credits.consumed)}</span>
              </div>
            )}
            {credits.available_credits !== null && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  {t('copilotAdoptionCowork.creditsHeadroom.available')}
                </Text>
                <span className={styles.creditValue}>{formatCredits(credits.available_credits)}</span>
              </div>
            )}
            {credits.payAsYouGoConsumed !== null && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  {t('copilotAdoptionCowork.creditsHeadroom.payAsYouGoConsumed')}
                </Text>
                <span className={styles.creditValue}>{formatCredits(credits.payAsYouGoConsumed)}</span>
              </div>
            )}
            {credits.status && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  {t('copilotAdoptionCowork.creditsHeadroom.status')}
                </Text>
                <span className={styles.creditValue}>{credits.status}</span>
              </div>
            )}
          </div>
          {credits.snapshotUtc && (
            <Text size={100} className={styles.muted}>
              {t('copilotAdoptionCowork.creditsHeadroom.snapshotTaken', { date: formatDate(credits.snapshotUtc) })}
            </Text>
          )}
        </Card>
      )}
      </div>

      {/* ---------- People: the spending-policy list ---------- */}
      <div role="tabpanel" aria-label={t('copilotAdoptionCowork.sections.people')} hidden={section !== 'people'}>
      <Card>
        <Text weight="semibold" block>
          {t('copilotAdoptionCowork.intro.title')}
        </Text>
        <Text size={200} className={styles.sectionNote}>
          {tNode('copilotAdoptionCowork.intro.policyScope', {
            noLicence: <strong>{t('copilotAdoptionCowork.intro.noLicence')}</strong>,
            policyScope: <strong>{t('copilotAdoptionCowork.intro.policyScopeStrong')}</strong>,
            addTo: <strong>{t('copilotAdoptionCowork.intro.addTo')}</strong>,
          })}
        </Text>

        {/* Chrome: nothing here can be used on paper. What it is set to is printed below instead. */}
        <div className={styles.filters} data-print="hide">
          <Input
            className={styles.grow}
            value={searchDraft}
            placeholder={t('copilotAdoptionCowork.filters.searchPlaceholder')}
            aria-label={t('copilotAdoptionCowork.filters.searchAria')}
            onChange={(_e: any, d: any) => setSearchDraft(d.value)}
            onKeyDown={(e: any) => {
              if (e.key === 'Enter') setFilters((f) => ({ ...f, search: searchDraft }));
            }}
          />
          <Button size="small" onClick={() => setFilters((f) => ({ ...f, search: searchDraft }))}>
            {t('copilotAdoptionCowork.filters.searchButton')}
          </Button>

          {/* The tier cards used to sit directly above this list and were its tier filter. They now
              live in their own section, so the filter has to be here too - or a list narrowed from a
              tier card could not be seen to be narrowed, let alone widened again. */}
          <Select
            value={filters.tiers[0] ?? ''}
            aria-label={t('copilotAdoptionCowork.filters.tierAria')}
            onChange={(_e: any, d: any) =>
              setFilters((f) => ({
                ...f,
                tiers: d.value ? [d.value as CoworkTier] : [],
                recommendedOnly: d.value ? false : f.recommendedOnly,
              }))
            }
          >
            <option value="">{t('copilotAdoptionCowork.filters.allVerdicts')}</option>
            {summary.coworkTiers.map((tier) => (
              <option key={tier.code} value={tier.code}>
                {coworkTierText(t, tier.code, 'label', tier.label, coworkRegularMinActiveDays)}
              </option>
            ))}
          </Select>

          <Select
            value={filters.department}
            aria-label={t('copilotAdoptionCowork.filters.departmentAria')}
            onChange={(_e: any, d: any) => setFilters((f) => ({ ...f, department: d.value }))}
          >
            <option value="">{t('copilotAdoptionCowork.filters.allDepartments')}</option>
            {(filterOptions?.departments ?? []).map((dept) => (
              <option key={dept} value={dept}>
                {dept}
              </option>
            ))}
          </Select>

          <Select
            value={sortValue}
            aria-label={t('copilotAdoptionCowork.filters.sortAria')}
            onChange={(_e: any, d: any) => {
              const [sortBy, direction] = d.value.split(':');
              setFilters((f) => ({ ...f, sortBy, sortDesc: direction === 'desc' }));
            }}
          >
            {SORT_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {t(o.labelKey)}
              </option>
            ))}
          </Select>

          <Tooltip
            content={t('copilotAdoptionCowork.filters.policyListTooltip')}
            relationship="description"
          >
            <Checkbox
              label={t('copilotAdoptionCowork.filters.policyListOnly')}
              checked={filters.recommendedOnly}
              onChange={(_e: any, d: any) =>
                setFilters((f) => ({ ...f, recommendedOnly: !!d.checked, tiers: [] }))
              }
            />
          </Tooltip>
          <Tooltip
            content={t('copilotAdoptionCowork.filters.alreadyUsingTooltip')}
            relationship="description"
          >
            <Checkbox
              label={t('copilotAdoptionCowork.filters.alreadyUsingCowork')}
              checked={filters.coworkUsersOnly}
              onChange={(_e: any, d: any) => setFilters((f) => ({ ...f, coworkUsersOnly: !!d.checked }))}
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
            {t('copilotAdoptionCowork.actions.refresh')}
          </Button>
          <Tooltip
            content={t('copilotAdoptionCowork.actions.exportTooltip')}
            relationship="description"
          >
            <Button size="small" icon={<ArrowDownload16Regular />} as="a" href={exportUrl}>
              {t('copilotAdoptionCowork.actions.exportCsv')}
            </Button>
          </Tooltip>
        </div>

        <PrintedFilters
          filters={[
            printedSearch(t, filters.search),
            {
              label: t('copilotAdoptionCowork.people.verdict'),
              value: selectedTier
                ? coworkTierText(t, selectedTier.code, 'label', selectedTier.label, coworkRegularMinActiveDays)
                : t('copilotAdoptionCowork.filters.allVerdicts'),
            },
            {
              label: t('copilotAdoptionCowork.table.department'),
              value: filters.department || t('copilotAdoptionCowork.filters.allDepartments'),
            },
            sortOption && { label: t('copilotAdoption.shared.printedFilters.sortedBy'), value: t(sortOption.labelKey) },
            filters.recommendedOnly && { value: t('copilotAdoptionCowork.filters.policyListOnly') },
            filters.coworkUsersOnly && { value: t('copilotAdoptionCowork.filters.alreadyUsingCowork') },
          ]}
        />

        {error && (
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}

        {!loading && (data?.warnings ?? []).filter((w) => w.toLowerCase().includes('cowork')).length > 0 && (
          <div className={styles.warnings}>
            {(data?.warnings ?? [])
              .filter((w) => w.toLowerCase().includes('cowork'))
              .map((warning) => (
                <MessageBar key={warning} intent="warning">
                  <MessageBarBody>{warning}</MessageBarBody>
                </MessageBar>
              ))}
          </div>
        )}

        {loading && (
          <div style={{ textAlign: 'center', padding: '28px' }}>
            <Spinner size={56} label={t('copilotAdoptionCowork.loading.assessingReadiness')} />
          </div>
        )}

        {!loading && data && data.rows.length === 0 && (
          <div className={styles.emptyState}>
            <Text weight="semibold" block>
              {t('copilotAdoptionCowork.empty.noSeatHolders')}
            </Text>
            <Button
              size="small"
              onClick={clearPanelFilters}
              data-print="hide"
            >
              {t('copilotAdoptionCowork.actions.clearFilters')}
            </Button>
          </div>
        )}

        {!loading && data && data.rows.length > 0 && (
          <div className={styles.tableWrap}>
            <table className={table.table}>
              <thead>
                <tr>
                  <th className={`${table.th} ${table.stickyLeft}`}>{t('copilotAdoptionCowork.people.user')}</th>
                  <th className={table.th}>{t('copilotAdoptionCowork.table.department')}</th>
                  <th className={table.th}>
                    <span className={styles.thWithInfo}>
                      {t('copilotAdoptionCowork.people.verdict')}
                      <InfoTip
                        title={t('copilotAdoptionCowork.people.verdictInfoTitle')}
                        content={{
                          what: t('copilotAdoptionCowork.people.verdictInfo.what'),
                          how: t('copilotAdoptionCowork.people.verdictInfo.how', {
                            days: options.coworkRegularMinActiveDays,
                            load: options.coworkLoadMinScore,
                            fluency: options.coworkFluencyMinScore,
                          }),
                          source: t('copilotAdoptionCowork.people.verdictInfo.source'),
                        }}
                      />
                    </span>
                  </th>
                  <th className={table.th}>
                    <span className={styles.thWithInfo}>
                      {t('copilotAdoptionCowork.metric.coordinationLoad')}
                      <InfoTip
                        title={t('copilotAdoptionCowork.metric.coordinationLoad')}
                        content={{
                          what: t('copilotAdoptionCowork.metric.coordinationLoadInfo.what'),
                          how: t('copilotAdoptionCowork.metric.coordinationLoadInfo.how', {
                            meetings: options.coworkMeetingWeight,
                            email: options.coworkEmailWeight,
                            messages: options.coworkCollaborationWeight,
                            documents: options.coworkDocumentWeight,
                          }),
                          formula: t('copilotAdoptionCowork.metric.coordinationLoadInfo.formula', {
                            meetingTarget: options.coworkMeetingTarget,
                            emailTarget: options.coworkEmailTarget,
                            collaborationTarget: options.coworkCollaborationTarget,
                            documentTarget: options.coworkDocumentTarget,
                            meetingWeight: options.coworkMeetingWeight,
                            emailWeight: options.coworkEmailWeight,
                            collaborationWeight: options.coworkCollaborationWeight,
                            documentWeight: options.coworkDocumentWeight,
                          }),
                          source: t('copilotAdoptionCowork.metric.coordinationLoadInfo.source'),
                        }}
                      />
                    </span>
                  </th>
                  <th className={table.th}>
                    <span className={styles.thWithInfo}>
                      {t('copilotAdoptionCowork.metric.copilotFluency')}
                      <InfoTip
                        title={t('copilotAdoptionCowork.metric.copilotFluency')}
                        content={{
                          what: t('copilotAdoptionCowork.metric.copilotFluencyInfo.what'),
                          how: t('copilotAdoptionCowork.metric.copilotFluencyInfo.how', {
                            uplift: options.coworkAgentFamiliarityUplift,
                          }),
                          source: t('copilotAdoptionCowork.metric.copilotFluencyInfo.source'),
                        }}
                      />
                    </span>
                  </th>
                  <th className={table.th}>{t('copilotAdoptionCowork.table.coworkUse')}</th>
                  <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionCowork.table.meetingsPerDay')}</th>
                  <th className={`${table.th} ${table.thNumeric}`}>{t('copilotAdoptionCowork.table.emailPerDay')}</th>
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
                        <td className={`${table.td} ${table.tdNoWrap}`}>
                          <span className={styles.upn}>
                            <Text size={200}>{coworkTierLabel(t, row.tier, row.tierLabel)}</Text>
                            <span style={{ marginTop: '3px' }}>
                              <BasisBadge basis={row.basis} />
                            </span>
                          </span>
                        </td>
                        <td className={table.td}>
                          <Tooltip
                            relationship="description"
                            content={t('copilotAdoptionCowork.scoreTooltip.loadBreakdown', {
                              meetings: Math.round(row.meetingScore),
                              email: Math.round(row.emailScore),
                              messages: Math.round(row.collaborationScore),
                              documents: Math.round(row.documentScore),
                            })}
                          >
                            <div>
                              <ScoreBar score={row.coordinationLoadScore} />
                            </div>
                          </Tooltip>
                        </td>
                        <td className={table.td}>
                          <Tooltip
                            relationship="description"
                            content={t(
                              row.agentsUsed > 0
                                ? 'copilotAdoptionCowork.scoreTooltip.engagementWithAgents'
                                : 'copilotAdoptionCowork.scoreTooltip.engagement',
                              { score: Math.round(row.adoptionScore), agents: row.agentsUsed },
                            )}
                          >
                            <div>
                              <ScoreBar score={row.fluencyScore} />
                            </div>
                          </Tooltip>
                        </td>
                        <td className={table.td}>
                          {row.usedCowork ? (
                            <Badge className={styles.evidence} size="small">
                              {row.coworkReportTotalTasks !== null
                                ? t('copilotAdoptionCowork.coworkUseBadge.tasksInDays', {
                                    tasks: formatCount(row.coworkReportTotalTasks),
                                    days: row.coworkReportActiveDays ?? 0,
                                  })
                                : row.coworkReportActiveDays !== null && row.coworkReportActiveDays > 0
                                  ? t('copilotAdoptionCowork.coworkUseBadge.daysReported', {
                                      days: formatCount(row.coworkReportActiveDays),
                                    })
                                  : t('copilotAdoptionCowork.coworkUseBadge.auditInteractionsInDays', {
                                      interactions: formatCount(row.coworkInteractions),
                                      days: row.coworkActiveDays,
                                    })}
                            </Badge>
                          ) : (
                            <Text size={200} className={styles.muted}>
                              {t('copilotAdoptionCowork.coworkUseBadge.notYet')}
                            </Text>
                          )}
                        </td>
                        <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.teamsMeetings)}</td>
                        <td className={`${table.td} ${table.tdNumeric}`}>
                          {formatCount(row.emailsSent + row.emailsRead)}
                        </td>
                      </tr>
                      {open && (
                        <DetailRow colSpan={detailColSpan}>
                          <DetailSections>
                            <DetailSection title={t('copilotAdoptionCowork.detail.coordinationLoadTitle', { score: Math.round(row.coordinationLoadScore) })}>
                              <DetailStats>
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.meetings')}
                                  value={formatCount(row.teamsMeetings)}
                                  sub={t('copilotAdoptionCowork.detail.perDayVsTarget', { score: Math.round(row.meetingScore), target: options.coworkMeetingTarget })}
                                />
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.email')}
                                  value={formatCount(row.emailsSent + row.emailsRead)}
                                  sub={t('copilotAdoptionCowork.detail.emailScoreSentRead', { score: Math.round(row.emailScore), sent: formatCount(row.emailsSent), read: formatCount(row.emailsRead) })}
                                />
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.teamsMessages')}
                                  value={formatCount(row.teamsMessages)}
                                  sub={t('copilotAdoptionCowork.detail.perDayVsTarget', { score: Math.round(row.collaborationScore), target: options.coworkCollaborationTarget })}
                                />
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.files')}
                                  value={formatCount(row.filesViewedOrEdited)}
                                  sub={t('copilotAdoptionCowork.detail.perDayVsTarget', { score: Math.round(row.documentScore), target: options.coworkDocumentTarget })}
                                />
                              </DetailStats>
                            </DetailSection>

                            <DetailSection title={t('copilotAdoptionCowork.detail.copilotFluencyTitle', { score: Math.round(row.fluencyScore) })}>
                              <DetailStats>
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.engagementScore')}
                                  value={Math.round(row.adoptionScore)}
                                  sub={t('copilotAdoptionCowork.detail.fromLicensedUsers')}
                                />
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.agentsUsed')}
                                  value={formatCount(row.agentsUsed)}
                                  sub={
                                    row.agentsUsed > 0
                                      ? t('copilotAdoptionCowork.detail.upToFamiliarity', { uplift: options.coworkAgentFamiliarityUplift })
                                      : t('copilotAdoptionCowork.detail.noFamiliarityUplift')
                                  }
                                />
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.fluencyBar')}
                                  value={options.coworkFluencyMinScore}
                                  sub={
                                    row.fluencyScore >= options.coworkFluencyMinScore
                                      ? t('copilotAdoptionCowork.detail.cleared')
                                      : t('copilotAdoptionCowork.detail.notCleared')
                                  }
                                />
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.loadBar')}
                                  value={options.coworkLoadMinScore}
                                  sub={
                                    row.coordinationLoadScore >= options.coworkLoadMinScore
                                      ? t('copilotAdoptionCowork.detail.cleared')
                                      : t('copilotAdoptionCowork.detail.notCleared')
                                  }
                                />
                              </DetailStats>
                            </DetailSection>

                            <DetailSection title={t('copilotAdoptionCowork.table.coworkUse')}>
                              <DetailStats>
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.auditInteractions')}
                                  value={formatCount(row.coworkInteractions)}
                                  sub={t('copilotAdoptionCowork.detail.onDaysRegularAt', { days: formatCount(row.coworkActiveDays), regular: options.coworkRegularMinActiveDays })}
                                />
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.lastAuditInteraction')}
                                  value={formatDate(row.lastCoworkInteractionUtc)}
                                  sub={t(row.basis === 'evidence' ? 'copilotAdoptionCowork.detail.observed' : 'copilotAdoptionCowork.detail.predictedVerdict')}
                                />
                                {row.coworkReportLastActivityDate !== null && (
                                  <DetailStat
                                    label={t('copilotAdoptionCowork.detail.reportedLastActivity')}
                                    value={formatDate(row.coworkReportLastActivityDate)}
                                    sub={t('copilotAdoptionCowork.detail.fromMicrosoftUsageReport')}
                                  />
                                )}
                                {row.coworkReportTotalTasks !== null && (
                                  <DetailStat
                                    label={t('copilotAdoptionCowork.detail.reportedTasks')}
                                    value={formatCount(row.coworkReportTotalTasks)}
                                    sub={taskSplitLabel(row, t)}
                                  />
                                )}
                                {row.coworkReportActiveDays !== null && (
                                  <DetailStat
                                    label={t('copilotAdoptionCowork.detail.reportedActiveDays')}
                                    value={formatCount(row.coworkReportActiveDays)}
                                  />
                                )}
                                {row.coworkAutomationRatioPct !== null && (
                                  <DetailStat
                                    label={t('copilotAdoptionCowork.detail.automated')}
                                    value={`${Math.round(row.coworkAutomationRatioPct)}%`}
                                    sub={t('copilotAdoptionCowork.detail.scheduledShareOfTasks')}
                                  />
                                )}
                                <DetailStat
                                  label={t('copilotAdoptionCowork.detail.lastM365Activity')}
                                  value={formatDate(row.lastM365ActivityUtc)}
                                />
                              </DetailStats>
                            </DetailSection>

                            {credits?.perUserCreditsAvailable && (
                              <DetailSection
                                title={t('copilotAdoptionCowork.detail.copilotCreditsTitle')}
                                info={{
                                  what: t('copilotAdoptionCowork.detail.copilotCreditsInfo.what'),
                                  how: t('copilotAdoptionCowork.detail.copilotCreditsInfo.how'),
                                  source: t('copilotAdoptionCowork.detail.copilotCreditsInfo.source'),
                                }}
                              >
                                <DetailStats>
                                  <DetailStat
                                    label={t('copilotAdoptionCowork.detail.allCopilotCredits')}
                                    value={
                                      row.totalCopilotCredits === null
                                        ? '\u2014'
                                        : formatCredits(row.totalCopilotCredits)
                                    }
                                    sub={
                                      row.totalCopilotCredits === null
                                        ? t('copilotAdoptionCowork.detail.notAttributableNotZero')
                                        : t('copilotAdoptionCowork.detail.allCreditBilledNotCoworkShare')
                                    }
                                  />
                                  {row.coworkCreditsPerTask !== null && (
                                    <DetailStat
                                      label={t('copilotAdoptionCowork.detail.creditsPerTask')}
                                      value={formatCredits(row.coworkCreditsPerTask)}
                                      sub={t('copilotAdoptionCowork.detail.allCopilotCreditsNotCoworkShare')}
                                    />
                                  )}
                                </DetailStats>
                              </DetailSection>
                            )}
                          </DetailSections>

                          <DetailSection
                            title={t('copilotAdoptionCowork.detail.justificationTitle')}
                            info={{
                              what: t('copilotAdoptionCowork.detail.justificationInfo.what'),
                              how: t('copilotAdoptionCowork.detail.justificationInfo.how'),
                              source: t('copilotAdoptionCowork.detail.justificationInfo.source'),
                            }}
                          >
                            <DetailRationale text={coworkRationaleText(t, row, options)} />
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
              {t('copilotAdoptionCowork.pagination.showingSeatHolders', {
                start: formatCount(printRows ? 1 : data.skip + 1),
                end: formatCount(printRows ? printRows.length : Math.min(data.skip + PAGE_SIZE, data.total)),
                total: formatCount(data.total),
              })}
            </Text>
            {/* A printout holds the whole list, so there is no page to turn to - and no Next button
                on paper to turn it with. */}
            {!printRows && (
              <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }} data-print="hide">
                <Button size="small" disabled={page === 0} onClick={() => setPage((p) => Math.max(0, p - 1))}>
                  {t('copilotAdoptionCowork.pagination.previous')}
                </Button>
                <Text size={200} className={styles.muted}>
                  {t('copilotAdoptionCowork.pagination.pageOf', { page: page + 1, totalPages })}
                </Text>
                <Button
                  size="small"
                  disabled={page + 1 >= totalPages}
                  onClick={() => setPage((p) => p + 1)}
                >
                  {t('copilotAdoptionCowork.pagination.next')}
                </Button>
              </div>
            )}
          </div>
        )}
        {!loading && data && <PartialPrintNote shownRows={rows.length} totalRows={data.total} />}
      </Card>
      </div>
    </div>
  );
}
