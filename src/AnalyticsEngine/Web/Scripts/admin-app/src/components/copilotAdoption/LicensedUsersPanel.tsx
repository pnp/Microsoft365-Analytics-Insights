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
} from '@fluentui/react-components';
import { ArrowDownload16Regular, ArrowClockwise16Regular } from '@fluentui/react-icons';
import { fetchLicensedUsers, licensedUsersExportUrl } from '../../api/copilotAdoptionApi';
import { AdoptionBand } from '../../types/copilotAdoption';
import type {
  AdoptionFilterOptions,
  LicensedUserFilters,
  LicensedUserPage,
} from '../../types/copilotAdoption';
import Spinner from '../Spinner';
import { BandBadge, ScoreBar, useAdoptionTableStyles } from './adoptionShared';
import { formatCount, formatDate } from './KpiGrid';

const PAGE_SIZE = 50;

/**
 * Sort options, phrased as the question the admin is actually asking rather than as column names.
 * "Least engaged first" is the default because the entire purpose of the list is finding the people
 * who are not getting value from a licence somebody is paying for.
 */
const SORT_OPTIONS = [
  { value: 'score:asc', label: 'Least engaged first' },
  { value: 'score:desc', label: 'Most engaged first' },
  { value: 'lastUse:asc', label: 'Longest since last use' },
  { value: 'interactions:desc', label: 'Most interactions' },
  { value: 'activeDays:desc', label: 'Most active days' },
  { value: 'cowork:desc', label: 'Most Cowork use' },
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
  action: {
    maxWidth: '340px',
    color: tokens.colorNeutralForeground2,
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
  department: '',
  country: '',
  coworkOnly: false,
  disabledOnly: false,
  sortBy: 'score',
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
  seatLicenceTypeIds,
  initialBands,
}: {
  windowDays: number;
  filterOptions: AdoptionFilterOptions | null;
  seatLicenceTypeIds?: number[];
  initialBands?: AdoptionBand[];
}) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();

  const [filters, setFilters] = useState<LicensedUserFilters>({
    ...DEFAULT_FILTERS,
    bands: initialBands ?? [],
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
    setLoading(true);
    setError(null);

    fetchLicensedUsers(windowDays, filters, page * PAGE_SIZE, PAGE_SIZE, seatLicenceTypeIds)
      .then((result) => {
        if (!cancelled) setData(result);
      })
      .catch((e) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Failed to load licensed users.');
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
    () => licensedUsersExportUrl(windowDays, filters, seatLicenceTypeIds),
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

        <Select
          value={sortValue}
          aria-label="Sort licensed users"
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
          label="Cowork users only"
          checked={filters.coworkOnly}
          onChange={(_e, d) => setFilters((f) => ({ ...f, coworkOnly: !!d.checked }))}
        />
        <Tooltip
          content="Disabled accounts still holding a Copilot licence - the clearest seats to reclaim."
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
        <div className={styles.tableWrap}>
          <table className={table.table}>
            <thead>
              <tr>
                <th className={table.th}>User</th>
                <th className={table.th}>Department</th>
                <th className={table.th}>Engagement</th>
                <th className={table.th}>Band</th>
                <th className={`${table.th} ${table.thNumeric}`}>Interactions</th>
                <th className={`${table.th} ${table.thNumeric}`}>Active days</th>
                <th className={`${table.th} ${table.thNumeric}`}>Apps</th>
                <th className={table.th}>Cowork</th>
                <th className={table.th}>Last used</th>
                <th className={table.th}>Recommended action</th>
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
                        <ScoreBar score={row.adoptionScore} />
                      </div>
                    </Tooltip>
                  </td>
                  <td className={table.td}>
                    <BandBadge band={row.band} name={row.bandName} />
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
                      <Text size={100} block className={styles.muted}>
                        {row.daysSinceLastUse} days ago
                      </Text>
                    )}
                  </td>
                  <td className={table.td}>
                    <Text size={200} className={styles.action}>
                      {row.recommendedAction}
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
