import { useEffect, useMemo, useState } from 'react';
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
import { BandBadge, ScoreBar, scoreColour, SortableTh, useAdoptionTableStyles } from './adoptionShared';
import { formatCount, formatDate, formatPct, weightSharePct } from '../shared/KpiGrid';
import InfoTip from '../shared/InfoTip';
import ActionPlan, { ActionBadge } from './ActionPlan';

const PAGE_SIZE = 50;

/**
 * The default sort. "Least engaged first" because the entire purpose of the list is finding the
 * people who are not getting value from a licence somebody is paying for. Every column is sortable
 * from its own header, so the old sort drop-down was a second way to do the same thing.
 */
const DEFAULT_SORT_BY = 'score';

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
    overflowX: 'auto',
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
  thWithInfo: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '2px',
  },
  legend: {
    marginBottom: '8px',
  },
  upn: {
    display: 'flex',
    flexDirection: 'column',
  },
  disabled: {
    color: tokens.colorPaletteRedForeground1,
  },
});

const DEFAULT_FILTERS: LicensedUserFilters = {
  search: '',
  bands: [],
  actions: [],
  department: '',
  country: '',
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
  filterOptions,
  actionPlan,
  options,
  dataSources,
  seatLicenceTypeIds,
  initialBands,
  initialAction,
}: {
  windowDays: number;
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
}) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();

  const [filters, setFilters] = useState<LicensedUserFilters>({
    ...DEFAULT_FILTERS,
    bands: initialBands ?? [],
    actions: initialAction ? [initialAction] : [],
  });
  const [searchDraft, setSearchDraft] = useState('');
  const [page, setPage] = useState(0);
  const [data, setData] = useState<LicensedUserPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  // Any filter change invalidates the current page number - staying on page 5 of a result set that
  // now has two pages shows an empty table and looks like a bug.
  useEffect(() => setPage(0), [filters, windowDays]);

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchLicensedUsers(windowDays, filters, page * PAGE_SIZE, PAGE_SIZE, seatLicenceTypeIds, controller.signal)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((e) => {
        if (cancelled || controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : 'Failed to load licensed users.');
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
  }, [windowDays, filters, page, seatLicenceTypeIds, reloadKey]);

  /**
   * Applies a column-header sort. The effect above already resets the page whenever `filters`
   * changes, so there is nothing else to do here.
   */
  const applySort = (sortBy: string, sortDesc: boolean) => {
    setFilters((f) => ({ ...f, sortBy, sortDesc }));
  };

  const exportUrl = useMemo(
    () => licensedUsersExportUrl(windowDays, filters, seatLicenceTypeIds),
    [windowDays, filters, seatLicenceTypeIds],
  );

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  const scoreWeights = [options.frequencyWeight, options.depthWeight, options.breadthWeight];
  const weightSum = scoreWeights.reduce((total, w) => total + w, 0);
  const bands = {
    champion: options.championScore,
    established: options.establishedScore,
    developing: options.developingScore,
  };

  // Only the actions that actually appear in the rows on screen. Showing all seven when the filter
  // has narrowed the list to one band would be padding, not explanation.
  const visibleActions = useMemo(() => {
    const present = new Set((data?.rows ?? []).map((r) => r.recommendedActionCode));
    return actionPlan.filter((a) => present.has(a.code));
  }, [data, actionPlan]);

  return (
    <Card>
      <div className={styles.filters}>
        <Input
          className={styles.grow}
          value={searchDraft}
          placeholder="Search name, email, department, job title or manager"
          aria-label="Search licensed Copilot users"
          onChange={(_e, d) => setSearchDraft(d.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') setFilters((f) => ({ ...f, search: searchDraft }));
          }}
        />
        <Button size="small" onClick={() => setFilters((f) => ({ ...f, search: searchDraft }))}>
          Search
        </Button>

        <Select
          value={filters.bands.length === 1 ? String(filters.bands[0]) : ''}
          aria-label="Filter by engagement band"
          onChange={(_e, d) =>
            setFilters((f) => ({ ...f, bands: d.value === '' ? [] : [Number(d.value) as AdoptionBand] }))
          }
        >
          <option value="">All engagement bands</option>
          {(filterOptions?.bands ?? []).map((b) => (
            <option key={b.value} value={b.value}>
              {b.name}
            </option>
          ))}
        </Select>

        <Select
          value={filters.actions.length === 1 ? filters.actions[0] : ''}
          aria-label="Filter by recommended action"
          onChange={(_e, d) => setFilters((f) => ({ ...f, actions: d.value === '' ? [] : [d.value] }))}
        >
          <option value="">All recommended actions</option>
          {actionPlan.map((a) => (
            <option key={a.code} value={a.code}>
              {a.label} ({a.users.toLocaleString()})
            </option>
          ))}
        </Select>

        <Select
          value={filters.reclaimEligibility}
          aria-label="Filter by reclaim eligibility"
          onChange={(_e, d) => setFilters((f) => ({ ...f, reclaimEligibility: d.value }))}
        >
          <option value="">All reclaim tiers</option>
          <option value="certain">Certain reclaim</option>
          <option value="probable">Probable reclaim</option>
          <option value="review">Review before reclaim</option>
          <option value="excluded">Excluded from reclaim</option>
        </Select>

        <Select
          value={filters.department}
          aria-label="Filter by department"
          onChange={(_e, d) => setFilters((f) => ({ ...f, department: d.value }))}
        >
          <option value="">All departments</option>
          {(filterOptions?.departments ?? []).map((dept) => (
            <option key={dept} value={dept}>
              {dept}
            </option>
          ))}
        </Select>

        <Checkbox
          label="Cowork users only"
          checked={filters.coworkOnly}
          onChange={(_e, d) => setFilters((f) => ({ ...f, coworkOnly: !!d.checked }))}
        />
        <Tooltip
          content="Disabled accounts still holding a Copilot licence - the clearest licences to reclaim."
          relationship="description"
        >
          <Checkbox
            label="Disabled accounts only"
            checked={filters.disabledOnly}
            onChange={(_e, d) => setFilters((f) => ({ ...f, disabledOnly: !!d.checked }))}
          />
        </Tooltip>

        <div className={styles.spacer} />

        <Button
          size="small"
          appearance="subtle"
          icon={<ArrowClockwise16Regular />}
          onClick={() => setReloadKey((k) => k + 1)}
        >
          Refresh
        </Button>
        <Button size="small" icon={<ArrowDownload16Regular />} as="a" href={exportUrl}>
          Export CSV
        </Button>
      </div>

      {error && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}

      {loading && (
        <div style={{ textAlign: 'center', padding: '28px' }}>
          <Spinner size={56} label="Loading users..." />
        </div>
      )}

      {!loading && data && data.rows.length === 0 && (
        <Text className={styles.muted}>No licensed users match these filters.</Text>
      )}

      {!loading && data && data.rows.length > 0 && (
        <>
          {visibleActions.length > 0 && (
            <Accordion collapsible className={styles.legend}>
              <AccordionItem value="actions">
                <AccordionHeader>What these actions mean</AccordionHeader>
                <AccordionPanel>
                  <ActionPlan actions={visibleActions} showCounts={false} />
                </AccordionPanel>
              </AccordionItem>
            </Accordion>
          )}

          <div className={styles.tableWrap}>
          <table className={table.table}>
            <thead>
              <tr>
                <SortableTh label="user" sortKey="upn" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort}>
                  User
                </SortableTh>
                <SortableTh label="department" sortKey="department" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort}>
                  Department
                </SortableTh>
                <SortableTh
                  label="engagement score"
                  sortKey="score"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                >
                  <span className={styles.thWithInfo}>
                    Engagement
                    <InfoTip
                      title="Engagement score"
                      content={{
                        what: 'How embedded Copilot is in this person\u2019s working week, from 0 to 100. Not "did they use it" - two people who each used it once score the same on that, and need the same response, which is rarely useful.',
                        how: `Three components: frequency (${formatPct(
                          weightSharePct(options.frequencyWeight, scoreWeights),
                        )}) against a target of ${formatPct(
                          options.frequencyTargetRatio * 100,
                        )} of available working days, depth (${formatPct(
                          weightSharePct(options.depthWeight, scoreWeights),
                        )}) against ${options.depthTargetInteractionsPerActiveDay} interactions per active day, and breadth (${formatPct(
                          weightSharePct(options.breadthWeight, scoreWeights),
                        )}) against ${options.breadthTargetApps} Copilot surfaces. Each component is capped at 100% before weighting, so nothing above target buys extra credit. Depth is additionally scaled down below ${options.depthMinActiveDays} active days, because it divides by a number the user controls - a few prompts in one afternoon would otherwise score full marks. Expected active days is prorated for accounts younger than the reporting period.`,
                        formula:
                          'freq       = min(1, activeDays / expectedActiveDays)\n' +
                          `confidence = min(1, activeDays / ${options.depthMinActiveDays})\n` +
                          'depth      = min(1, interactions / activeDays / depthTarget) x confidence\n' +
                          'breadth    = min(1, appsUsed / breadthTarget)\n' +
                          `score = (freq*${options.frequencyWeight} + depth*${options.depthWeight} + breadth*${options.breadthWeight})\n` +
                          `        / ${weightSum} x 100`,
                        source: 'Hover the bar on any row for that user\u2019s three component scores.',
                      }}
                    />
                  </span>
                </SortableTh>
                <SortableTh
                  label="band"
                  sortKey="band"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                >
                  <span className={styles.thWithInfo}>
                    Band
                    <InfoTip
                      title="Engagement band"
                      content={{
                        what: 'The engagement score turned into a label, so a list of numbers becomes a list of decisions.',
                        how: `Champion at ${options.championScore}+, Established at ${options.establishedScore}+, Developing at ${options.developingScore}+, Trialling below that. Users with no activity in this period are not scored at all: they are split into Dormant (used Copilot at some point in the last ${options.historyDays} days) and Never used. Sorting by this column follows the adoption ladder, not the alphabet.`,
                        source:
                          'Established and above is what the "habitual users" headline counts. Dormant plus Never used is the idle-seat population, which is NOT the same as "reclaimable licences" - that figure is the certain and probable reclaim tiers only, after review, exclusion and window-mismatch hold-backs.',
                      }}
                    />
                  </span>
                </SortableTh>
                <SortableTh label="signal source" sortKey="signalSource" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort}>
                  <span className={styles.thWithInfo}>
                    Signal source
                    <InfoTip
                      title="Signal source and reconciliation"
                      content={{
                        what: "Which source produced this row's engagement score, and whether both source figures are available for comparison.",
                        how: `Audit rows use this product's Copilot audit-log import over the selected D${windowDays} window. usageReport rows use Microsoft's per-user Copilot usage report when the audit import has no signal for that user. Where both sources have signal, the cell shows both figures side by side instead of pretending one corrects the other.`,
                        source: "Microsoft's report uses Microsoft's settled report period and covers licensed users only; the audit log covers the selected period and includes unlicensed Copilot Chat.",
                      }}
                    />
                  </span>
                </SortableTh>
                <SortableTh
                  label="interactions"
                  sortKey="interactions"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  numeric
                  defaultDescending
                >
                  Interactions
                </SortableTh>
                <SortableTh
                  label="active days"
                  sortKey="activeDays"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  numeric
                  defaultDescending
                >
                  <span className={styles.thWithInfo}>
                    Active days
                    <InfoTip
                      title="Active days"
                      content={{
                        what: 'Distinct days this person had at least one Copilot interaction, against the number needed to score full marks for frequency.',
                        how: `The target is ${formatPct(options.frequencyTargetRatio * 100)} of the working days in the period, assuming ${options.workingDaysPerWeek} working days a week. Working days rather than calendar days - against calendar days even a genuinely daily user would cap out around 71% and look like a partial adopter.`,
                        formula: `expectedActiveDays = ${options.windowDays} days x (${options.workingDaysPerWeek}/7) x ${options.frequencyTargetRatio}`,
                      }}
                    />
                  </span>
                </SortableTh>
                <SortableTh label="apps used" sortKey="apps" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort} numeric defaultDescending>
                  Apps
                </SortableTh>
                <SortableTh label="Cowork use" sortKey="cowork" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort} defaultDescending>
                  Cowork
                </SortableTh>
                <SortableTh label="last used" sortKey="lastUse" activeKey={filters.sortBy} descending={filters.sortDesc} onSort={applySort}>
                  Last used
                </SortableTh>
                <SortableTh
                  label="reclaim tier"
                  sortKey="reclaimEligibility"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                >
                  <span className={styles.thWithInfo}>
                    Reclaim tier
                    <InfoTip
                      title="Reclaim eligibility"
                      content={{
                        what: 'Whether this seat is safe to put in the reclaim total. Certain means a disabled account still holds a seat; probable means no observed use beyond the grace period; review means a human must check first; excluded means an admin already reviewed it.',
                        how: `Uses the same row-level key as the headline reclaim counts. A new user inside the ${options.reclaimGraceDays}-day grace period is review-only, and active new users have their expected active days prorated. Sorting by this column runs most-actionable first, not alphabetically.`,
                        source: 'Leave, part-time patterns, service/shared accounts and role-based mailboxes are not detectable from Microsoft 365 usage data, so they must be handled through review or an exclusion.',
                      }}
                    />
                  </span>
                </SortableTh>
                <SortableTh
                  label="recommended action"
                  sortKey="action"
                  activeKey={filters.sortBy}
                  descending={filters.sortDesc}
                  onSort={applySort}
                  className={table.stickyRight}
                >
                  <span className={styles.thWithInfo}>
                    Action
                    <InfoTip
                      title="Recommended action"
                      content={{
                        what: 'The single next step for this person, as a tag. What each tag means is stated once under "What these actions mean" above the table - it is the same sentence for everyone who carries the tag, so repeating it on every row would be noise.',
                        how: 'Derived from the band, and for the middle bands from the breadth score as well: someone with a real habit confined to one Copilot surface needs broadening rather than more coaching.',
                        source:
                          'The CSV export carries both the tag and the full sentence on every row, because a spreadsheet gets sorted and filtered and cannot rely on a legend.',
                      }}
                    />
                  </span>
                </SortableTh>
              </tr>
            </thead>
            <tbody>
              {data.rows.map((row) => (
                <tr key={row.userId}>
                  <td className={table.td}>
                    <span className={styles.upn}>
                      <Text size={200} weight="semibold">
                        {row.userPrincipalName}
                      </Text>
                      <Text size={100} className={row.accountEnabled === false ? styles.disabled : styles.muted}>
                        {row.accountEnabled === false ? 'Account disabled' : row.jobTitle || row.mail || ''}
                      </Text>
                    </span>
                  </td>
                  <td className={table.td}>{row.department || '\u2014'}</td>
                  <td className={table.td}>
                    <Tooltip
                      relationship="description"
                      content={`Frequency ${Math.round(row.frequencyScore)} / Depth ${Math.round(
                        row.depthScore,
                      )} / Breadth ${Math.round(row.breadthScore)}. Active on ${row.activeDays} of ${
                        row.expectedActiveDays
                      } days needed for full marks.`}
                    >
                      <div>
                        <ScoreBar score={row.adoptionScore} colour={scoreColour(row.adoptionScore, bands)} />
                      </div>
                    </Tooltip>
                  </td>
                  <td className={table.td}>
                    <BandBadge band={row.band} name={row.bandName} />
                  </td>
                  <td className={table.td}>
                    <Text size={200}>{sourceLabel(row.signalSource)}</Text>
                    {row.sourceComparisonAvailable && (
                      <Text size={100} block className={table.tdSub}>
                        {sourceComparisonText(row, windowDays, dataSources)}
                      </Text>
                    )}
                  </td>
                  <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.interactions)}</td>
                  <td className={`${table.td} ${table.tdNumeric}`}>
                    {row.activeDays} <span className={styles.muted}>/ {Math.round(row.expectedActiveDays)}</span>
                  </td>
                  <td className={`${table.td} ${table.tdNumeric}`}>{row.appsUsed}</td>
                  <td className={table.td}>
                    {row.usedCowork ? `Yes (${formatCount(row.coworkInteractions)})` : 'No'}
                  </td>
                  <td className={table.td}>
                    {formatDate(row.lastInteractionUtc)}
                    {row.daysSinceLastUse !== null && row.daysSinceLastUse > 0 && (
                      <Text size={100} block className={table.tdSub}>
                        {row.daysSinceLastUse} days ago
                      </Text>
                    )}
                  </td>
                  <td className={table.td}>
                    <Tooltip relationship="description" content={row.reclaimEligibilityReason || 'This active seat is not in a reclaim tier.'}>
                      <Text size={200}>{row.reclaimEligibility || '—'}</Text>
                    </Tooltip>
                    {row.reclaimExclusionExpired && (
                      <Text size={100} block className={table.tdSub}>
                        exclusion expired
                      </Text>
                    )}
                  </td>
                  <td className={`${table.td} ${table.stickyRight}`}>
                    <Tooltip relationship="description" content={row.recommendedAction}>
                      <div>
                        <ActionBadge code={row.recommendedActionCode} label={row.recommendedActionLabel} />
                      </div>
                    </Tooltip>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </div>
        </>
      )}

      {!loading && data && data.total > 0 && (
        <div className={styles.footer}>
          <Text size={200} className={styles.muted}>
            Showing {formatCount(data.skip + 1)}-{formatCount(Math.min(data.skip + PAGE_SIZE, data.total))} of{' '}
            {formatCount(data.total)} licensed users
          </Text>
          <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
            <Button size="small" disabled={page === 0} onClick={() => setPage((p) => Math.max(0, p - 1))}>
              Previous
            </Button>
            <Text size={200} className={styles.muted}>
              Page {page + 1} of {totalPages}
            </Text>
            <Button
              size="small"
              disabled={page + 1 >= totalPages}
              onClick={() => setPage((p) => p + 1)}
            >
              Next
            </Button>
          </div>
        </div>
      )}
    </Card>
  );
}


function sourceLabel(source: string): string {
  return source === 'usageReport' ? 'Microsoft usage report' : source === 'audit' ? 'Audit log' : source;
}

function sourceComparisonText(row: LicensedUserAdoptionRow, windowDays: number, dataSources?: AdoptionDataSources): string {
  const reportPeriod = dataSources?.copilotUsageReportPeriodDays
    ? `D${dataSources.copilotUsageReportPeriodDays}`
    : 'Microsoft window';
  const snapshot = dataSources?.copilotUsageReportDate ? `, ${formatDate(dataSources.copilotUsageReportDate)}` : '';

  return `Audit D${windowDays}: ${formatCount(row.auditInteractions)} interactions, ${formatCount(
    row.auditActiveDays,
  )} days. Microsoft report ${reportPeriod}${snapshot}: ${
    row.reportPrompts === null ? '—' : formatCount(row.reportPrompts)
  } prompts, ${row.reportActiveDays === null ? '—' : formatCount(row.reportActiveDays)} days.`;
}
