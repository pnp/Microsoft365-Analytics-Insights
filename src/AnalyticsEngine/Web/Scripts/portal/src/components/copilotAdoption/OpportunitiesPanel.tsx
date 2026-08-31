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
import { fetchOpportunities, opportunitiesExportUrl } from '../../api/copilotAdoptionApi';
import type {
  AdoptionFilterOptions,
  CopilotAdoptionOptions,
  LicenceOpportunityPage,
  OpportunityFilters,
} from '../../types/copilotAdoption';
import Spinner from '../Spinner';
import { ScoreBar, useAdoptionTableStyles } from './adoptionShared';
import { formatCount, formatDate } from './KpiGrid';
import InfoTip from './InfoTip';

const PAGE_SIZE = 50;

const SORT_OPTIONS = [
  { value: 'score:desc', label: 'Strongest case first' },
  { value: 'copilot:desc', label: 'Most unlicensed Copilot use' },
  { value: 'collaboration:desc', label: 'Most Teams activity' },
  { value: 'email:desc', label: 'Most email activity' },
  { value: 'documents:desc', label: 'Most document activity' },
  { value: 'department:asc', label: 'Department (A-Z)' },
  { value: 'upn:asc', label: 'User name (A-Z)' },
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
    maxWidth: '360px',
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
  recommendedOnly: false,
  existingCopilotUsersOnly: false,
  sortBy: 'score',
  sortDesc: true,
};

/**
 * The licence-opportunity list: users with no Copilot seat, ranked by how strong the business case
 * for giving them one is.
 *
 * The "already using Copilot Chat" badge is the single most persuasive thing on this screen - it is
 * evidence of demand rather than an inference from general Microsoft 365 activity - so it is
 * surfaced as its own column and its own filter rather than being buried in the score.
 */
export default function OpportunitiesPanel({
  windowDays,
  filterOptions,
  options,
  seatLicenceTypeIds,
}: {
  windowDays: number;
  filterOptions: AdoptionFilterOptions | null;
  /** The weights and targets actually used, so the score explanation quotes them rather than guessing. */
  options: CopilotAdoptionOptions;
  seatLicenceTypeIds?: number[];
}) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();

  const [filters, setFilters] = useState<OpportunityFilters>(DEFAULT_FILTERS);
  const [searchDraft, setSearchDraft] = useState('');
  const [page, setPage] = useState(0);
  const [data, setData] = useState<LicenceOpportunityPage | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => setPage(0), [filters, windowDays]);

  useEffect(() => {
    let cancelled = false;
    const controller = new AbortController();
    setLoading(true);
    setError(null);

    fetchOpportunities(windowDays, filters, page * PAGE_SIZE, PAGE_SIZE, seatLicenceTypeIds, controller.signal)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((e) => {
        if (cancelled || controller.signal.aborted) return;
        setError(e instanceof Error ? e.message : 'Failed to load licence opportunities.');
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

  const sortValue = `${filters.sortBy}:${filters.sortDesc ? 'desc' : 'asc'}`;
  const exportUrl = useMemo(
    () => opportunitiesExportUrl(windowDays, filters, seatLicenceTypeIds),
    [windowDays, filters, seatLicenceTypeIds],
  );

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  const filtersActive =
    filters.search !== '' ||
    filters.department !== '' ||
    filters.country !== '' ||
    filters.recommendedOnly ||
    filters.existingCopilotUsersOnly;

  // Only the warnings that explain an empty or thin candidate list. The page header already carries
  // the full set, and repeating all of them here would bury the one that answers "why is this empty?".
  const relevantWarnings = (data?.warnings ?? []).filter(
    (w) => w.toLowerCase().includes('licence opportunit') || w.toLowerCase().includes('usage report'),
  );

  return (
    <Card>
      <div className={styles.filters}>
        <Input
          className={styles.grow}
          value={searchDraft}
          placeholder="Search name, email, department, job title or manager"
          aria-label="Search licence candidates"
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
          aria-label="Filter candidates by department"
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
          aria-label="Sort licence candidates"
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

        <Checkbox
          label="Recommended only"
          checked={filters.recommendedOnly}
          onChange={(_e, d) => setFilters((f) => ({ ...f, recommendedOnly: !!d.checked }))}
        />
        <Tooltip
          content="People already using Copilot Chat without a licence - proven demand, not an inference."
          relationship="description"
        >
          <Checkbox
            label="Already using Copilot"
            checked={filters.existingCopilotUsersOnly}
            onChange={(_e, d) => setFilters((f) => ({ ...f, existingCopilotUsersOnly: !!d.checked }))}
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

      {!loading && relevantWarnings.length > 0 && (
        <div className={styles.warnings}>
          {relevantWarnings.map((warning) => (
            <MessageBar key={warning} intent="warning">
              <MessageBarBody>{warning}</MessageBarBody>
            </MessageBar>
          ))}
        </div>
      )}

      {loading && (
        <div style={{ textAlign: 'center', padding: '28px' }}>
          <Spinner size={56} label="Ranking candidates..." />
        </div>
      )}

      {!loading && data && data.rows.length === 0 && (
        <div className={styles.emptyState}>
          {filtersActive ? (
            <>
              <Text weight="semibold" block>
                No licence candidates match these filters.
              </Text>
              <Text size={200} block className={styles.muted}>
                {data.total === 0
                  ? 'Clear the filters to see every candidate found in this period.'
                  : `${formatCount(data.total)} candidates were found in this period, but none of them match.`}
              </Text>
              <Button size="small" onClick={() => { setSearchDraft(''); setFilters(DEFAULT_FILTERS); }}>
                Clear filters
              </Button>
            </>
          ) : (
            <>
              <Text weight="semibold" block>
                Nobody in this tenant qualifies as a licence candidate for the selected period.
              </Text>
              <Text size={200} block className={styles.muted}>
                A user is listed here when <strong>all</strong> of the following are true. This is a
                shortlist for a paying seat, not a directory listing, so users with no recorded activity
                at all are deliberately left out - there is no business case to make for them.
              </Text>
              <ul className={styles.emptyList}>
                <li>
                  <Text size={200}>They hold none of the SKUs classified as a Copilot licence.</Text>
                </li>
                <li>
                  <Text size={200}>Their Entra ID account is enabled - a disabled account cannot use a seat.</Text>
                </li>
                <li>
                  <Text size={200}>
                    They show up in this period in <strong>either</strong> the Copilot audit log (they used
                    Copilot Chat without a licence) <strong>or</strong> the Microsoft 365 usage reports for
                    Teams, Outlook, SharePoint or OneDrive.
                  </Text>
                </li>
              </ul>
              <Text size={200} block className={styles.muted}>
                An empty list almost always means the third point: the Microsoft 365 usage reports have not
                been imported, so there is nothing to rank. Turn on the usage-report import in the installer
                and check the Health page, then widen the period at the top of this page. Small test tenants
                legitimately produce an empty list because hardly anyone is active in them.
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
                <th className={table.th}>User</th>
                <th className={table.th}>Department</th>
                <th className={table.th}>
                  <span className={styles.thWithInfo}>
                    Business case
                    <InfoTip
                      title="Business case score"
                      content={{
                        what: `How strong the case for giving this person a Copilot licence is, from 0 to 100. Anyone at ${options.opportunityRecommendScore} or above is counted in the "recommended for a licence" headline.`,
                        how: `Four weighted signals, weighted so evidence beats inference. Already using Copilot Chat without a licence is worth ${options.opportunityUnlicensedCopilotWeight} points because it proves demand for Copilot itself; Teams collaboration is worth ${options.opportunityCollaborationWeight}, email ${options.opportunityEmailWeight} and document work ${options.opportunityDocumentWeight}, and those three only infer it from general Microsoft 365 activity. Each signal is a ratio against its own target and is capped at 1, so no single very heavy workload can carry someone over the line on its own.`,
                        formula:
                          `copilot     = min(1, unlicensedCopilotInteractions / ${options.opportunityCopilotTarget})\n` +
                          `collab      = min(1, (teamsMessages + teamsMeetings) / ${options.opportunityCollaborationTarget})\n` +
                          `email       = min(1, (emailsSent + emailsRead) / ${options.opportunityEmailTarget})\n` +
                          `documents   = min(1, filesViewedOrEdited / ${options.opportunityDocumentTarget})\n` +
                          `score = copilot*${options.opportunityUnlicensedCopilotWeight} + collab*${options.opportunityCollaborationWeight} + email*${options.opportunityEmailWeight} + documents*${options.opportunityDocumentWeight}`,
                        source:
                          'Copilot use comes from the Copilot audit import and covers this period exactly. The Teams, email and document figures are a per-active-day average across this same period, taken from Microsoft\u2019s daily usage reports - a day the user did not appear in the report at all does not drag the average down. Hover any row for its four component scores.',
                      }}
                    />
                  </span>
                </th>
                <th className={table.th}>
                  <span className={styles.thWithInfo}>
                    Already using Copilot
                    <InfoTip
                      title="Already using Copilot"
                      content={{
                        what: 'Copilot interactions this person made in the selected period despite holding no Microsoft 365 Copilot licence - almost always Copilot Chat, which is available without one.',
                        how: 'Counted from the Copilot audit log for users who hold none of the SKUs classified as a Copilot licence. Shown as interactions and the number of distinct days they happened on, because ten interactions across ten days is a habit and ten in one afternoon is an experiment.',
                        source:
                          'Invisible in Microsoft\u2019s own Copilot usage report, which only covers licensed users. It needs the Copilot audit import to be enabled.',
                      }}
                    />
                  </span>
                </th>
                <th className={`${table.th} ${table.thNumeric}`}>Teams (per day)</th>
                <th className={`${table.th} ${table.thNumeric}`}>Email (per day)</th>
                <th className={`${table.th} ${table.thNumeric}`}>Files (per day)</th>
                <th className={table.th}>Last M365 activity</th>
                <th className={table.th}>
                  <span className={styles.thWithInfo}>
                    Justification
                    <InfoTip
                      title="Justification"
                      content={{
                        what: 'The score restated in plain English, naming the specific signals that produced it for this person.',
                        how: 'Written per user rather than per band - unlike the licensed-user list, no two candidates reach the same score by the same route, so this genuinely differs from row to row.',
                        source: 'Safe to paste directly into a licence request. It is also in the CSV export.',
                      }}
                    />
                  </span>
                </th>
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
                    <Tooltip
                      relationship="description"
                      content={`Copilot demand ${Math.round(row.copilotDemandScore)} / Collaboration ${Math.round(
                        row.collaborationScore,
                      )} / Email ${Math.round(row.emailScore)} / Documents ${Math.round(row.documentScore)}`}
                    >
                      <div>
                        <ScoreBar score={row.opportunityScore} />
                      </div>
                    </Tooltip>
                  </td>
                  <td className={table.td}>
                    {row.unlicensedCopilotInteractions > 0 ? (
                      <Badge className={styles.evidence} size="small">
                        {formatCount(row.unlicensedCopilotInteractions)} in {row.unlicensedCopilotActiveDays}d
                      </Badge>
                    ) : (
                      <Text size={200} className={styles.muted}>
                        Not yet
                      </Text>
                    )}
                  </td>
                  <td className={`${table.td} ${table.tdNumeric}`}>
                    {formatCount(row.teamsMessages)}
                    <Text size={100} block className={styles.muted}>
                      {formatCount(row.teamsMeetings)} mtgs
                    </Text>
                  </td>
                  <td className={`${table.td} ${table.tdNumeric}`}>
                    {formatCount(row.emailsSent)}
                    <Text size={100} block className={styles.muted}>
                      {formatCount(row.emailsRead)} read
                    </Text>
                  </td>
                  <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(row.filesViewedOrEdited)}</td>
                  <td className={table.td}>{formatDate(row.lastM365ActivityUtc)}</td>
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
            Showing {formatCount(data.skip + 1)}-{formatCount(Math.min(data.skip + PAGE_SIZE, data.total))} of{' '}
            {formatCount(data.total)} candidates
          </Text>
          <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
            <Button size="small" disabled={page === 0} onClick={() => setPage((p) => Math.max(0, p - 1))}>
              Previous
            </Button>
            <Text size={200} className={styles.muted}>
              Page {page + 1} of {totalPages}
            </Text>
            <Button size="small" disabled={page + 1 >= totalPages} onClick={() => setPage((p) => p + 1)}>
              Next
            </Button>
          </div>
        </div>
      )}
    </Card>
  );
}
