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
  LicenceOpportunityPage,
  OpportunityFilters,
} from '../../types/copilotAdoption';
import Spinner from '../Spinner';
import { ScoreBar, useAdoptionTableStyles } from './adoptionShared';
import { formatCount, formatDate } from './KpiGrid';

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
  seatLicenceTypeIds,
}: {
  windowDays: number;
  filterOptions: AdoptionFilterOptions | null;
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
    setLoading(true);
    setError(null);

    fetchOpportunities(windowDays, filters, page * PAGE_SIZE, PAGE_SIZE, seatLicenceTypeIds)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load licence opportunities.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [windowDays, filters, page, seatLicenceTypeIds, reloadKey]);

  const sortValue = `${filters.sortBy}:${filters.sortDesc ? 'desc' : 'asc'}`;
  const exportUrl = useMemo(
    () => opportunitiesExportUrl(windowDays, filters, seatLicenceTypeIds),
    [windowDays, filters, seatLicenceTypeIds],
  );

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

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

      {loading && (
        <div style={{ textAlign: 'center', padding: '28px' }}>
          <Spinner size={56} label="Ranking candidates..." />
        </div>
      )}

      {!loading && data && data.rows.length === 0 && (
        <Text className={styles.muted}>No unlicensed users match these filters.</Text>
      )}

      {!loading && data && data.rows.length > 0 && (
        <div className={styles.tableWrap}>
          <table className={table.table}>
            <thead>
              <tr>
                <th className={table.th}>User</th>
                <th className={table.th}>Department</th>
                <th className={table.th}>Business case</th>
                <th className={table.th}>Already using Copilot</th>
                <th className={`${table.th} ${table.thNumeric}`}>Teams</th>
                <th className={`${table.th} ${table.thNumeric}`}>Email</th>
                <th className={`${table.th} ${table.thNumeric}`}>Files</th>
                <th className={table.th}>Last M365 activity</th>
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
