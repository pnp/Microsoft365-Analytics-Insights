import { useEffect, useMemo, useRef, useState } from 'react';
import {
  makeStyles,
  tokens,
  Card,
  Text,
  Input,
  Select,
  Button,
  Spinner,
  MessageBar,
  MessageBarBody,
} from '@fluentui/react-components';
import { ArrowClockwise16Regular } from '@fluentui/react-icons';
import {
  WORKLOADS,
  type LicenceActivityCoverage,
  type LicenceActivitySku,
  type SortDirection,
  type UsersParams,
  type UsersSortKey,
  type WorkloadKey,
} from '../../types/licenceActivity';
import UsersTable from './UsersTable';
import ApiErrorBar, { describeError } from './ApiErrorBar';
import { useUsersQuery } from './useUsersQuery';
import { statusMeta } from './statuses';
import { formatCount } from './format';

const PAGE_SIZE = 50;
const MIN_TOP = 1;
const MAX_TOP = 100;
// Must match LicenceActivityQuery.Create's server-side limit, which throws (=> HTTP 400) above it.
const MAX_SEARCH = 100;

const BROWSE_SORTS: { value: string; label: string }[] = [
  { value: 'activity:desc', label: 'Most active first' },
  { value: 'activity:asc', label: 'Least active first' },
  { value: 'lastActivity:desc', label: 'Most recently active' },
  { value: 'lastActivity:asc', label: 'Longest since active' },
  { value: 'upn:asc', label: 'UPN (A\u2013Z)' },
  { value: 'upn:desc', label: 'UPN (Z\u2013A)' },
];

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '12px',
  },
  head: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  headText: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  controls: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
  },
  field: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
  },
  topInput: {
    width: '84px',
  },
  twoUp: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
    gap: '16px',
  },
  panel: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  panelHead: {
    display: 'flex',
    alignItems: 'baseline',
    gap: '8px',
  },
  browse: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
  },
  browseControls: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    flexWrap: 'wrap',
  },
  grow: {
    flexGrow: 1,
    minWidth: '200px',
  },
  footer: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  center: {
    textAlign: 'center',
    padding: '24px',
  },
});

function clampTop(n: number): number {
  if (!Number.isFinite(n)) return MIN_TOP;
  return Math.max(MIN_TOP, Math.min(MAX_TOP, Math.round(n)));
}

/** A 1..100 count field with commit-on-blur/Enter, so the bounded lists refetch once per change. */
function TopCountInput({ value, onCommit, disabled }: { value: number; onCommit: (n: number) => void; disabled?: boolean }) {
  const styles = useStyles();
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);

  const commit = (): void => {
    const n = clampTop(Number(text));
    onCommit(n);
    setText(String(n));
  };

  return (
    <Input
      className={styles.topInput}
      type="number"
      min={MIN_TOP}
      max={MAX_TOP}
      value={text}
      disabled={disabled}
      aria-label="Number of users in each list"
      onChange={(_e, d) => setText(d.value)}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          commit();
        }
      }}
    />
  );
}

interface UsersDrillDownProps {
  overviewId: string;
  licence: LicenceActivitySku;
  /** The overview's per-workload coverage, used to explain why a workload can't be ranked. */
  coverage: LicenceActivityCoverage[];
  /** Reports the snapshot id of the loaded users list, for the exact-snapshot export. Null while
   *  loading / on error. Should be a stable reference. */
  onUsersSnapshot: (usersId: string | null) => void;
  /** Reloads the overview - the correct fix when a users request fails because the snapshot expired. */
  onRefreshOverview: () => void;
  /** Bumped by the parent to force a fresh users fetch (re-mint) even when the params are otherwise
   *  unchanged - e.g. after an export 410, when the overview is still cached under the same id so the
   *  params never change on their own. A same-key reload keeps the current rows on screen while the
   *  new snapshot loads. Undefined/0 means "no forced refresh yet". */
  refreshToken?: number;
}

/**
 * The per-licence drill-down. One workload at a time; a single request returns the top-N most and
 * least active users AND the current browse page together, so their shared snapshot id is an
 * unambiguous "current bounded rows" for the export. All fetching is cancellable and stale-safe.
 */
export default function UsersDrillDown({
  overviewId,
  licence,
  coverage,
  onUsersSnapshot,
  onRefreshOverview,
  refreshToken,
}: UsersDrillDownProps) {
  const styles = useStyles();

  const [workload, setWorkload] = useState<WorkloadKey>('teams');
  const [top, setTop] = useState(10);
  const [searchDraft, setSearchDraft] = useState('');
  const [search, setSearch] = useState('');

  // Never let the box hold a term the server is guaranteed to reject. LicenceActivityQuery.Create
  // throws (=> HTTP 400) for a search over MAX_SEARCH characters or containing ANY control character,
  // and a single-line <input> only strips CR/LF for us - the other C0/C1 controls survive a paste.
  //
  // Two functions, deliberately: trimming WHILE EDITING would make the space bar unusable, because
  // `searchDraft` is fed straight back into the controlled input, so " " typed after "ada" would be
  // erased before the next keystroke and "ada smith" could never be typed. So editing only removes
  // what is genuinely un-submittable (control characters, over-length), and trimming happens once, at
  // commit.
  const sanitiseDraft = (value: string) =>
    // eslint-disable-next-line no-control-regex
    value.replace(/[\u0000-\u001f\u007f]+/g, ' ').slice(0, MAX_SEARCH);
  const commitSearch = (value: string) => setSearch(sanitiseDraft(value).trim());
  const [sort, setSort] = useState<UsersSortKey>('activity');
  const [direction, setDirection] = useState<SortDirection>('desc');
  const [page, setPage] = useState(1);

  const workloadLabel = WORKLOADS.find((w) => w.key === workload)?.label ?? workload;
  const workloadCoverage = coverage.find((c) => c.workload === workload) ?? null;
  const coverageIncomplete = workloadCoverage != null && workloadCoverage.status !== 'available';
  const rankEmptyText = coverageIncomplete
    ? `${workloadLabel} activity is not fully measured, so users can't be ranked here - see the note above.`
    : 'No users to rank for this workload.';

  // Reset the page atomically when the scope (licence / workload / browse filter) changes, DURING
  // render - so `params` never briefly pairs a new scope with the old page, which would fire a wasted
  // SQL load before the page-1 reset. This is the React "adjust state during render" pattern.
  const scopeKey = `${overviewId}\n${licence.licenceTypeId}\n${workload}\n${search}\n${sort}\n${direction}`;
  const [scope, setScope] = useState(scopeKey);
  if (scope !== scopeKey) {
    setScope(scopeKey);
    setPage(1);
  }

  const params = useMemo<UsersParams>(
    () => ({
      overviewId,
      licenceTypeId: licence.licenceTypeId,
      workload,
      top,
      search,
      sort,
      direction,
      page,
      pageSize: PAGE_SIZE,
    }),
    [overviewId, licence.licenceTypeId, workload, top, search, sort, direction, page],
  );

  const { data, loading, error, reload } = useUsersQuery(params);

  // A parent-driven forced refresh (e.g. after an export 410). Re-mint the current view's snapshot
  // without changing any params, so the licence/workload/page/filters the admin is looking at are all
  // preserved. Skip the initial mount (token 0/undefined) so this never double-fetches on first load.
  const firstRefreshToken = useRef(refreshToken);
  useEffect(() => {
    if (refreshToken === firstRefreshToken.current) return;
    firstRefreshToken.current = refreshToken;
    reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refreshToken]);

  // Keep the page's export in step with the list currently on screen.
  useEffect(() => {
    onUsersSnapshot(data?.snapshotId ?? null);
  }, [data?.snapshotId, onUsersSnapshot]);

  const totalPages = data ? Math.max(1, Math.ceil(data.totalUsers / PAGE_SIZE)) : 1;
  const retry = describeError(error, '').kind === 'expired' ? onRefreshOverview : reload;

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <div className={styles.headText}>
          <Text weight="semibold" size={400}>
            {licence.name}
          </Text>
          <Text size={200} className={styles.muted}>
            {formatCount(licence.assignedUsers)} users hold this licence. Choose a workload to rank them by its
            activity.
          </Text>
        </div>
        <div className={styles.controls}>
          <div className={styles.field}>
            <Text size={200} className={styles.muted}>
              Workload
            </Text>
            <Select value={workload} aria-label="Workload" onChange={(_e, d) => setWorkload(d.value as WorkloadKey)}>
              {WORKLOADS.map((w) => (
                <option key={w.key} value={w.key}>
                  {w.label}
                </option>
              ))}
            </Select>
          </div>
          <div className={styles.field}>
            <Text size={200} className={styles.muted}>
              Show top
            </Text>
            <TopCountInput value={top} onCommit={setTop} />
            <Text size={200} className={styles.muted}>
              of each
            </Text>
          </div>
          <Button
            size="small"
            appearance="subtle"
            icon={<ArrowClockwise16Regular />}
            onClick={reload}
            aria-label="Refresh users"
          >
            Refresh
          </Button>
        </div>
      </div>

      {error != null && <ApiErrorBar error={error} fallback="Couldn't load the users." onRetry={retry} />}

      {/* A failed load unmounts the browse controls below (they live inside `data && ...`), which
          would otherwise strand an admin whose SEARCH TERM caused the failure: every surviving
          control preserves `search`, so retrying just reproduces it. Sanitising the input makes a
          search-induced 400 unreachable, and this keeps a guaranteed escape route for any other
          cause. */}
      {error != null && !data && search !== '' && (
        <div className={styles.center}>
          <Button
            size="small"
            onClick={() => {
              setSearchDraft('');
              setSearch('');
            }}
          >
            Clear search and try again
          </Button>
        </div>
      )}

      {coverageIncomplete && workloadCoverage && (
        <MessageBar intent="warning">
          <MessageBarBody>
            <strong>
              {workloadLabel}: {statusMeta(workloadCoverage.status).label}.
            </strong>{' '}
            {statusMeta(workloadCoverage.status).explanation}
            {workloadCoverage.message ? ` ${workloadCoverage.message}` : ''} Users with positive evidence still appear
            as most active; nobody is ranked least active for this workload.
          </MessageBarBody>
        </MessageBar>
      )}

      {data && data.messages.length > 0 && (
        <MessageBar intent="info">
          <MessageBarBody>
            <ul style={{ margin: 0, paddingInlineStart: '20px' }}>
              {data.messages.map((m) => (
                <li key={m}>{m}</li>
              ))}
            </ul>
          </MessageBarBody>
        </MessageBar>
      )}

      {loading && !data && (
        <div className={styles.center}>
          <Spinner size="small" label="Loading users..." />
        </div>
      )}

      {data && (
        <>
          <div className={styles.twoUp}>
            <div className={styles.panel}>
              <div className={styles.panelHead}>
                <Text weight="semibold" size={300}>
                  Most active
                </Text>
                <Text size={200} className={styles.muted}>
                  top {top}
                </Text>
              </div>
              <UsersTable
                rows={data.mostActive}
                workload={workload}
                workloadLabel={workloadLabel}
                showRank
                emptyText={rankEmptyText}
              />
            </div>
            <div className={styles.panel}>
              <div className={styles.panelHead}>
                <Text weight="semibold" size={300}>
                  Least active
                </Text>
                <Text size={200} className={styles.muted}>
                  bottom {top}
                </Text>
              </div>
              <UsersTable
                rows={data.leastActive}
                workload={workload}
                workloadLabel={workloadLabel}
                showRank
                emptyText={rankEmptyText}
              />
            </div>
          </div>

          <div className={styles.browse}>
            <div className={styles.panelHead}>
              <Text weight="semibold" size={300}>
                Browse all
              </Text>
              <Text size={200} className={styles.muted}>
                {formatCount(data.totalUsers)} users
              </Text>
            </div>

            <Text size={100} className={styles.muted}>
              User display names are not imported; search uses UPN or email. This is a deliberate limitation, not a
              missing name.
            </Text>

            <div className={styles.browseControls}>
              <Input
                className={styles.grow}
                value={searchDraft}
                maxLength={MAX_SEARCH}
                placeholder="Search UPN or email"
                aria-label="Search users"
                onChange={(_e, d) => setSearchDraft(sanitiseDraft(d.value))}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') commitSearch(searchDraft);
                }}
              />
              <Button size="small" onClick={() => commitSearch(searchDraft)}>
                Search
              </Button>
              <Select
                value={`${sort}:${direction}`}
                aria-label="Sort users"
                onChange={(_e, d) => {
                  const [nextSort, nextDir] = d.value.split(':');
                  setSort(nextSort as UsersSortKey);
                  setDirection(nextDir as SortDirection);
                }}
              >
                {BROWSE_SORTS.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </Select>
            </div>

            <UsersTable
              rows={data.users}
              workload={workload}
              workloadLabel={workloadLabel}
              startRank={(data.query.page - 1) * data.query.pageSize + 1}
              showRank
              emptyText={search ? 'No users match your search.' : 'No users to show for this selection.'}
            />

            {data.totalUsers > 0 && (
              <div className={styles.footer}>
                <Text size={200} className={styles.muted}>
                  Showing {formatCount((data.query.page - 1) * data.query.pageSize + 1)}&ndash;
                  {formatCount(Math.min(data.query.page * data.query.pageSize, data.totalUsers))} of{' '}
                  {formatCount(data.totalUsers)} users
                </Text>
                <div style={{ display: 'flex', gap: '8px', alignItems: 'center' }}>
                  <Button size="small" disabled={page <= 1} onClick={() => setPage((p) => Math.max(1, p - 1))}>
                    Previous
                  </Button>
                  <Text size={200} className={styles.muted}>
                    Page {data.query.page} of {totalPages}
                  </Text>
                  <Button size="small" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>
                    Next
                  </Button>
                </div>
              </div>
            )}
          </div>
        </>
      )}
    </Card>
  );
}
