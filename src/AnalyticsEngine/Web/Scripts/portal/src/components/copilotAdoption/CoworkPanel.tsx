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
  Badge,
  MessageBar,
  MessageBarBody,
  Tooltip,
} from '@fluentui/react-components';
import { ArrowDownload16Regular, ArrowClockwise16Regular } from '@fluentui/react-icons';
import { fetchCowork, coworkExportUrl } from '../../api/copilotAdoptionApi';
import type {
  AdoptionFilterOptions,
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  CoworkBasis,
  CoworkFilters,
  CoworkReadinessPage,
  CoworkTier,
} from '../../types/copilotAdoption';
import Spinner from '../Spinner';
import { ScoreBar, useAdoptionTableStyles } from './adoptionShared';
import { formatCount, formatDate } from './KpiGrid';
import InfoTip from './InfoTip';
import CoworkQuadrant from './CoworkQuadrant';

const PAGE_SIZE = 50;

const SORT_OPTIONS = [
  { value: 'load:desc', label: 'Most coordination load' },
  { value: 'fluency:desc', label: 'Most Copilot fluency' },
  { value: 'meetings:desc', label: 'Most meetings' },
  { value: 'coworkActiveDays:desc', label: 'Most Cowork use' },
  { value: 'tier:asc', label: 'Cowork tier' },
  { value: 'department:asc', label: 'Department (A-Z)' },
  { value: 'upn:asc', label: 'User name (A-Z)' },
];

const useStyles = makeStyles({
  section: {
    marginBottom: '16px',
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
  rationale: {
    maxWidth: '340px',
    color: tokens.colorNeutralForeground2,
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
  estimateRange: {
    fontSize: '26px',
    fontWeight: 700,
    fontVariantNumeric: 'tabular-nums',
  },
  assumptionList: {
    margin: '8px 0 0',
    paddingLeft: '20px',
    display: 'flex',
    flexDirection: 'column',
    gap: '3px',
    color: tokens.colorNeutralForeground3,
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
  recommendedOnly: false,
  coworkUsersOnly: false,
  sortBy: 'load',
  sortDesc: true,
};

function BasisBadge({ basis }: { basis: CoworkBasis }) {
  const styles = useStyles();

  // Spelled out rather than shortened to "Observed"/"Predicted" alone, because this badge is the one
  // thing stopping a reader treating a forecast as a fact.
  return basis === 'evidence' ? (
    <Tooltip relationship="description" content="Observed: this person has actually used Cowork.">
      <Badge className={styles.evidence} size="small">
        Observed
      </Badge>
    </Tooltip>
  ) : (
    <Tooltip
      relationship="description"
      content="Predicted from workload and Copilot use. Nobody has observed this person using Cowork."
    >
      <Badge className={styles.inference} size="small" appearance="outline" color="informative">
        Predicted
      </Badge>
    </Tooltip>
  );
}

/**
 * The Cowork readiness tab.
 *
 * Cowork has no licence of its own: it needs a Microsoft 365 Copilot licence as a prerequisite and is
 * then billed by consumption against Copilot Credits, with access granted by a spending policy scoped
 * to users or groups. So this tab is not "who should we buy something for" - it is "who should we put
 * in that policy", and every affordance here is built around producing that list.
 *
 * The evidence/inference split is the load-bearing idea. Two of the six tiers are observed; four are
 * predictions. Presenting the second kind as the first would be the most damaging thing this page
 * could do, so the distinction is a column, a badge, a filter and a caption rather than a footnote.
 */
export default function CoworkPanel({
  windowDays,
  summary,
  filterOptions,
  options,
  seatLicenceTypeIds,
}: {
  windowDays: number;
  summary: CopilotAdoptionSummary;
  filterOptions: AdoptionFilterOptions | null;
  options: CopilotAdoptionOptions;
  seatLicenceTypeIds?: number[];
}) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();

  const [filters, setFilters] = useState<CoworkFilters>(DEFAULT_FILTERS);
  const [searchDraft, setSearchDraft] = useState('');
  const [page, setPage] = useState(0);
  const [data, setData] = useState<CoworkReadinessPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  const available = summary.coworkReadinessAvailable;

  useEffect(() => setPage(0), [filters, windowDays]);

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
      .catch((e) => {
        if (cancelled || controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : 'Failed to load the Cowork readiness list.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
      // These requests poll while the analysis is building, so cleanup has to actually stop them.
      controller.abort();
    };
  }, [available, windowDays, filters, page, seatLicenceTypeIds, reloadKey]);

  const sortValue = `${filters.sortBy}:${filters.sortDesc ? 'desc' : 'asc'}`;
  const exportUrl = useMemo(
    () => coworkExportUrl(windowDays, filters, seatLicenceTypeIds),
    [windowDays, filters, seatLicenceTypeIds],
  );

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;
  const estimate = summary.coworkValueEstimate;
  const credits = summary.coworkCreditPosition;

  const toggleTier = (tier: CoworkTier) =>
    setFilters((f) => ({
      ...f,
      tiers: f.tiers.includes(tier) ? f.tiers.filter((t) => t !== tier) : [tier],
      recommendedOnly: false,
    }));

  if (!available) {
    return (
      <Card>
        <div className={styles.emptyState}>
          <Text weight="semibold" block>
            Cowork readiness could not be assessed for this period.
          </Text>
          <Text size={200} block className={styles.muted}>
            This needs two things: at least one Microsoft 365 Copilot licence assigned in the tenant, and
            Microsoft&#8217;s daily usage reports for Teams, Outlook, SharePoint or OneDrive. The usage
            reports are what measure coordination load - how much delegable, multi-step work each person
            carries - and without them there is nothing to rank against.
          </Text>
          <Text size={200} block className={styles.muted}>
            Turn on the Microsoft 365 usage report import in the installer and check the Health page for
            when it last succeeded. This is deliberately shown instead of an empty list: &#8220;no
            candidates&#8221; is a finding, and this is a missing import.
          </Text>
        </div>
      </Card>
    );
  }

  return (
    <div>
      {/* ---------- What this is ---------- */}
      <Card className={styles.section}>
        <Text weight="semibold" block>
          Who should we turn Cowork on for?
        </Text>
        <Text size={200} className={styles.sectionNote}>
          Cowork has <strong>no licence of its own</strong>. It requires a Microsoft 365 Copilot licence as
          a prerequisite, and is then billed by usage against Copilot Credits with access granted by a{' '}
          <strong>spending policy scoped to users or groups</strong>. So this is not a list of licences to
          buy - it is a list of people to <strong>add to</strong> that policy. Merge the CSV into your
          existing policy scope; never replace the scope with it. This product observes usage, not policy
          assignments, so someone already scoped who simply had no Cowork activity in the selected period
          will not appear here, and replacing the scope would revoke their access.
        </Text>
        <Text size={200} className={styles.sectionNote}>
          Two things have to be true before enabling someone is worthwhile: they must carry real{' '}
          <strong>coordination load</strong> (meetings, mail and document churn - the multi-step work Cowork
          absorbs), and they must have enough <strong>Copilot fluency</strong> to trust an agent with it.
          Neither is sufficient alone, which is why this tab scores them as two separate axes rather than
          blending them into one number that could not tell the two failure modes apart.
        </Text>

        <div className={styles.tierGrid}>
          {summary.coworkTiers.map((tier) => {
            const active = filters.tiers.length === 1 && filters.tiers[0] === tier.code;
            return (
              <button
                key={tier.code}
                type="button"
                className={`${styles.tierCard} ${active ? styles.tierCardActive : ''}`}
                onClick={() => toggleTier(tier.code)}
              >
                <div className={styles.tierTop}>
                  <Text size={200} weight="semibold">
                    {tier.label}
                  </Text>
                  <BasisBadge basis={tier.basis} />
                </div>
                <span className={styles.tierCount}>{formatCount(tier.users)}</span>
                <Text size={100} className={styles.tierDesc}>
                  {tier.description}
                </Text>
              </button>
            );
          })}
        </div>
        <Text size={100} className={styles.muted}>
          Click a tier to filter the list below to exactly the people counted in it.
        </Text>
      </Card>

      {/* ---------- The quadrant ---------- */}
      <Card className={styles.section}>
        <div className={styles.sectionHeader}>
          <Text weight="semibold">Readiness by department</Text>
          <InfoTip
            title="The readiness quadrant"
            content={{
              what: 'Each bubble is a department, placed by how much delegable coordination work its people carry (across) against how fluent they are with Copilot (up). Bubble size is the number of Copilot seats.',
              how: `A department is in the top-right "ready" corner when its average coordination load reaches ${options.coworkLoadMinScore} and its average Copilot fluency reaches ${options.coworkFluencyMinScore}. Coordination load is a weighted blend of meetings (${options.coworkMeetingWeight}), email (${options.coworkEmailWeight}), Teams messages (${options.coworkCollaborationWeight}) and document work (${options.coworkDocumentWeight}), each capped so one heavy signal cannot carry the score. Fluency is the Copilot engagement score plus up to ${options.coworkAgentFamiliarityUplift} points for having already used an agent - the nearest existing behaviour to delegating to Cowork.`,
              source:
                'Coordination load comes from Microsoft\u2019s daily usage reports as a per-active-day average. Fluency is the same engagement score the Licensed users tab shows - it is joined in rather than recalculated, so the two tabs can never disagree. A department\u2019s POSITION is a prediction; only the "already using Cowork" figure in each tooltip is observed.',
            }}
          />
        </div>
        <Text size={200} className={styles.sectionNote}>
          Start top-right and work left. A department full of Copilot experts with no coordination load has
          nothing for Cowork to absorb; a department drowning in meetings that has never formed a Copilot
          habit will not delegate to an agent just because you switched one on.
        </Text>
        <CoworkQuadrant points={summary.coworkQuadrant} options={options} />
      </Card>

      {/* ---------- Rollout order ---------- */}
      {summary.coworkByDepartment.length > 0 && (
        <Card className={styles.section}>
          <Text weight="semibold" block>
            Suggested rollout order
          </Text>
          <Text size={200} className={styles.sectionNote}>
            Ordered by the <strong>number</strong> of prime candidates, not the rate. A three-person team
            where everyone qualifies is a 100% rate and not where a rollout should start.
          </Text>
          <div className={styles.tableWrap}>
            <table className={table.table}>
              <thead>
                <tr>
                  <th className={table.th}>Department</th>
                  <th className={`${table.th} ${table.thNumeric}`}>Copilot seats</th>
                  <th className={`${table.th} ${table.thNumeric}`}>Prime candidates</th>
                  <th className={`${table.th} ${table.thNumeric}`}>Already using Cowork</th>
                  <th className={`${table.th} ${table.thNumeric}`}>Avg coordination load</th>
                  <th className={`${table.th} ${table.thNumeric}`}>Avg Copilot fluency</th>
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
                        {Math.round(row.primeCandidateRatePct)}% of seats
                      </Text>
                    </td>
                    <td className={`${table.td} ${table.tdNumeric}`}>
                      {formatCount(row.regularCoworkUsers)}
                      <Text size={100} block className={table.tdSub}>
                        {Math.round(row.coworkAdoptionPct)}%
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
            Copilot Credit headroom
          </Text>
          <Text size={200} className={styles.sectionNote}>
            Cowork is billed by consumption against Copilot Credits, so this is what a rollout draws on.{' '}
            <strong>
              These are the shared Copilot Credits pool, not Cowork-only spend
            </strong>{' '}
            - Copilot Studio and other credit-billed workloads draw on the same pool, and Microsoft
            publishes no way to separate them. Treat it as headroom, never as a Cowork bill.
          </Text>
          <div className={styles.creditGrid}>
            {credits.entitled !== null && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  Entitled
                </Text>
                <span className={styles.creditValue}>{formatCount(credits.entitled)}</span>
              </div>
            )}
            {credits.consumed !== null && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  Consumed
                </Text>
                <span className={styles.creditValue}>{formatCount(credits.consumed)}</span>
              </div>
            )}
            {credits.available_credits !== null && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  Available
                </Text>
                <span className={styles.creditValue}>{formatCount(credits.available_credits)}</span>
              </div>
            )}
            {credits.payAsYouGoConsumed !== null && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  Pay-as-you-go consumed
                </Text>
                <span className={styles.creditValue}>{formatCount(credits.payAsYouGoConsumed)}</span>
              </div>
            )}
            {credits.status && (
              <div className={styles.creditCell}>
                <Text size={200} className={styles.muted} block>
                  Status
                </Text>
                <span className={styles.creditValue}>{credits.status}</span>
              </div>
            )}
          </div>
          {credits.snapshotUtc && (
            <Text size={100} className={styles.muted}>
              Snapshot taken {formatDate(credits.snapshotUtc)}. This is the latest reading regardless of the
              period selected above, because it is a point-in-time tenant total.
            </Text>
          )}
        </Card>
      )}

      {/* ---------- The modelled estimate ---------- */}
      {estimate && estimate.cohortUsers > 0 && (
        <Card className={styles.section}>
          <div className={styles.sectionHeader}>
            <Text weight="semibold">Potential time saved - a model, not a measurement</Text>
          </div>
          <MessageBar intent="info">
            <MessageBarBody>
              This product does not and cannot measure time saved. The volumes below are observed; the hours
              are those volumes multiplied by an editable assumption. Use this to size a rollout, not to
              report a result - and never quote the figure without the assumptions underneath it.
            </MessageBarBody>
          </MessageBar>

          <div style={{ marginTop: '12px' }}>
            <span className={styles.estimateRange}>
              {formatCount(estimate.hoursPerMonthLow)}-{formatCount(estimate.hoursPerMonthHigh)} hours
            </span>
            <Text size={200} className={styles.muted}>
              {' '}
              a month across {formatCount(estimate.cohortUsers)} recommended users
            </Text>
            {estimate.currencyPerMonthHigh !== null && estimate.currencyPerMonthLow !== null && (
              <Text size={300} block style={{ marginTop: '4px' }}>
                {formatCount(estimate.currencyPerMonthLow)}-{formatCount(estimate.currencyPerMonthHigh)}{' '}
                {estimate.currencyCode ?? ''} a month at the configured loaded hourly cost
              </Text>
            )}
          </div>

          <Text size={200} block style={{ marginTop: '10px' }}>
            Observed addressable work a month: {formatCount(estimate.addressableMeetings)} meetings,{' '}
            {formatCount(estimate.addressableMailThreads)} emails,{' '}
            {formatCount(estimate.addressableDocuments)} document touches.
          </Text>

          <ul className={styles.assumptionList}>
            {estimate.assumptions.map((assumption) => (
              <li key={assumption}>
                <Text size={100}>{assumption}</Text>
              </li>
            ))}
          </ul>
        </Card>
      )}

      {/* ---------- The people ---------- */}
      <Card>
        <div className={styles.filters}>
          <Input
            className={styles.grow}
            value={searchDraft}
            placeholder="Search name, email, department, job title or manager"
            aria-label="Search Cowork candidates"
            onChange={(_e, d) => setSearchDraft(d.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') setFilters((f) => ({ ...f, search: searchDraft }));
            }}
          />
          <Button size="small" onClick={() => setFilters((f) => ({ ...f, search: searchDraft }))}>
            Search
          </Button>

          <Select
            value={filters.department}
            aria-label="Filter Cowork candidates by department"
            onChange={(_e, d) => setFilters((f) => ({ ...f, department: d.value }))}
          >
            <option value="">All departments</option>
            {(filterOptions?.departments ?? []).map((dept) => (
              <option key={dept} value={dept}>
                {dept}
              </option>
            ))}
          </Select>

          <Select
            value={sortValue}
            aria-label="Sort Cowork candidates"
            onChange={(_e, d) => {
              const [sortBy, direction] = d.value.split(':');
              setFilters((f) => ({ ...f, sortBy, sortDesc: direction === 'desc' }));
            }}
          >
            {SORT_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </Select>

          <Tooltip
            content="Prime candidates plus everyone already using Cowork. Existing users must stay in the policy or they lose access."
            relationship="description"
          >
            <Checkbox
              label="Policy list only"
              checked={filters.recommendedOnly}
              onChange={(_e, d) =>
                setFilters((f) => ({ ...f, recommendedOnly: !!d.checked, tiers: [] }))
              }
            />
          </Tooltip>
          <Tooltip
            content="Only people who have actually used Cowork - observed, not predicted."
            relationship="description"
          >
            <Checkbox
              label="Already using Cowork"
              checked={filters.coworkUsersOnly}
              onChange={(_e, d) => setFilters((f) => ({ ...f, coworkUsersOnly: !!d.checked }))}
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
          <Tooltip
            content="A spending-policy scoping list: UPN first, with each person's justification next to it. It exports the rows matching the filters above, so tick 'Recommended only' first if you want just the rollout cohort - and merge the result into your existing policy scope rather than replacing it."
            relationship="description"
          >
            <Button size="small" icon={<ArrowDownload16Regular />} as="a" href={exportUrl}>
              Export CSV
            </Button>
          </Tooltip>
        </div>

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
            <Spinner size={56} label="Assessing Cowork readiness..." />
          </div>
        )}

        {!loading && data && data.rows.length === 0 && (
          <div className={styles.emptyState}>
            <Text weight="semibold" block>
              No Copilot seat holders match these filters.
            </Text>
            <Button
              size="small"
              onClick={() => {
                setSearchDraft('');
                setFilters(DEFAULT_FILTERS);
              }}
            >
              Clear filters
            </Button>
          </div>
        )}

        {!loading && data && data.rows.length > 0 && (
          <div className={styles.tableWrap}>
            <table className={table.table}>
              <thead>
                <tr>
                  <th className={table.th}>User</th>
                  <th className={table.th}>Department</th>
                  <th className={table.th}>
                    <span className={styles.thWithInfo}>
                      Verdict
                      <InfoTip
                        title="Cowork verdict"
                        content={{
                          what: 'Which of the six Cowork populations this person is in, and - critically - whether that verdict was observed or predicted.',
                          how: `Observed Cowork use is tested first and wins outright: ${options.coworkRegularMinActiveDays} or more separate days of use is "Established", any use below that is "Trialling". Only if there is no Cowork use at all does the prediction apply, using the coordination-load bar (${options.coworkLoadMinScore}) and the fluency bar (${options.coworkFluencyMinScore}).`,
                          source:
                            'Cowork use comes from the Copilot audit log. The two predicted axes come from Microsoft\u2019s usage reports and the Copilot engagement score. A "Predicted" badge means nobody has seen this person use Cowork - do not read it as a measurement.',
                        }}
                      />
                    </span>
                  </th>
                  <th className={table.th}>
                    <span className={styles.thWithInfo}>
                      Coordination load
                      <InfoTip
                        title="Coordination load"
                        content={{
                          what: 'How much delegable, multi-step coordination work this person carries, from 0 to 100. This is the work Cowork would take on.',
                          how: `Four weighted signals, each a ratio against its own per-active-day target and capped at 1 so no single heavy signal can carry the score. Meetings are weighted highest (${options.coworkMeetingWeight}) because a meeting implies preparation, notes and follow-ups - a chain of delegable tasks - rather than a single message. Email is ${options.coworkEmailWeight}, Teams messages ${options.coworkCollaborationWeight}, document work ${options.coworkDocumentWeight}.`,
                          formula:
                            `meetings  = min(1, meetingsPerActiveDay / ${options.coworkMeetingTarget})\n` +
                            `email     = min(1, (sent + read) / ${options.coworkEmailTarget})\n` +
                            `messages  = min(1, teamsMessages / ${options.coworkCollaborationTarget})\n` +
                            `documents = min(1, filesViewedOrEdited / ${options.coworkDocumentTarget})\n` +
                            `load = meetings*${options.coworkMeetingWeight} + email*${options.coworkEmailWeight} + messages*${options.coworkCollaborationWeight} + documents*${options.coworkDocumentWeight}`,
                          source:
                            'A per-active-day average across the selected period, from Microsoft\u2019s daily usage reports - a day the person did not appear in the report at all does not drag the average down.',
                        }}
                      />
                    </span>
                  </th>
                  <th className={table.th}>
                    <span className={styles.thWithInfo}>
                      Copilot fluency
                      <InfoTip
                        title="Copilot fluency"
                        content={{
                          what: 'Whether this person is practised enough with Copilot to hand a multi-step task to an agent. Cowork is a step up from Copilot, not an entry point.',
                          how: `The Copilot engagement score from the Licensed users tab, plus up to ${options.coworkAgentFamiliarityUplift} points if they have already used a Copilot agent - the nearest existing behaviour to delegating to Cowork. The uplift is capped so it can promote a borderline user but never carry an inactive one over the bar.`,
                          source:
                            'The engagement score is joined in from the licensed-user analysis rather than recalculated here, so this tab and the Licensed users tab can never disagree about whether someone is fluent.',
                        }}
                      />
                    </span>
                  </th>
                  <th className={table.th}>Cowork use</th>
                  <th className={`${table.th} ${table.thNumeric}`}>Meetings (per day)</th>
                  <th className={`${table.th} ${table.thNumeric}`}>Email (per day)</th>
                  {credits?.perUserCreditsAvailable && (
                    <th className={`${table.th} ${table.thNumeric}`}>
                      <span className={styles.thWithInfo}>
                        All Copilot Credits
                        <InfoTip
                          title="All Copilot Credits"
                          content={{
                            what: 'Every Copilot Credit billed to this person in the period, across all credit-billed Copilot workloads. NOT Cowork\u2019s share.',
                            how: 'Microsoft meters Cowork against the shared Copilot Credits pool and exposes no per-row workload discriminator, so a Cowork-only per-user figure does not exist and is not invented here.',
                            source:
                              'The Copilot Studio per-user credit import. A dash means the credits could not be attributed to this person - not that they cost nothing.',
                          }}
                        />
                      </span>
                    </th>
                  )}
                  <th className={table.th}>Justification</th>
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
                        <Text size={100} className={styles.muted}>
                          {row.jobTitle || row.mail || ''}
                        </Text>
                      </span>
                    </td>
                    <td className={table.td}>{row.department || '\u2014'}</td>
                    <td className={table.td}>
                      <span className={styles.upn}>
                        <Text size={200}>{row.tierLabel}</Text>
                        <span style={{ marginTop: '3px' }}>
                          <BasisBadge basis={row.basis} />
                        </span>
                      </span>
                    </td>
                    <td className={table.td}>
                      <Tooltip
                        relationship="description"
                        content={`Meetings ${Math.round(row.meetingScore)} / Email ${Math.round(
                          row.emailScore,
                        )} / Messages ${Math.round(row.collaborationScore)} / Documents ${Math.round(
                          row.documentScore,
                        )}`}
                      >
                        <div>
                          <ScoreBar score={row.coordinationLoadScore} />
                        </div>
                      </Tooltip>
                    </td>
                    <td className={table.td}>
                      <Tooltip
                        relationship="description"
                        content={`Engagement ${Math.round(row.adoptionScore)}${
                          row.agentsUsed > 0 ? ` + agent familiarity (${row.agentsUsed} agent(s))` : ''
                        }`}
                      >
                        <div>
                          <ScoreBar score={row.fluencyScore} />
                        </div>
                      </Tooltip>
                    </td>
                    <td className={table.td}>
                      {row.usedCowork ? (
                        <Badge className={styles.evidence} size="small">
                          {formatCount(row.coworkInteractions)} in {row.coworkActiveDays}d
                        </Badge>
                      ) : (
                        <Text size={200} className={styles.muted}>
                          Not yet
                        </Text>
                      )}
                    </td>
                    <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.teamsMeetings)}</td>
                    <td className={`${table.td} ${table.tdNumeric}`}>
                      {formatCount(row.emailsSent + row.emailsRead)}
                    </td>
                    {credits?.perUserCreditsAvailable && (
                      <td className={`${table.td} ${table.tdNumeric}`}>
                        {row.totalCopilotCredits === null ? (
                          <Tooltip
                            relationship="description"
                            content="Not attributable - no per-user credit rows for this person. This is not zero."
                          >
                            <Text size={200} className={styles.muted}>
                              &#8212;
                            </Text>
                          </Tooltip>
                        ) : (
                          formatCount(row.totalCopilotCredits)
                        )}
                      </td>
                    )}
                    <td className={table.td}>
                      <Text size={200} className={styles.rationale}>
                        {row.rationale}
                      </Text>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {!loading && data && data.total > 0 && (
          <div className={styles.footer}>
            <Text size={200} className={styles.muted}>
              Showing {formatCount(data.skip + 1)}-
              {formatCount(Math.min(data.skip + PAGE_SIZE, data.total))} of {formatCount(data.total)} seat
              holders
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
    </div>
  );
}
