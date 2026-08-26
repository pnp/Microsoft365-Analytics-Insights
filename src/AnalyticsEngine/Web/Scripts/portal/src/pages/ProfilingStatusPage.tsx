import { useEffect, useState } from 'react';
import {
  Title3,
  Body1,
  Text,
  Card,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
  MessageBar,
  MessageBarBody,
  Button,
  Select,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowClockwise16Regular,
  ChevronLeft16Regular,
  ChevronRight16Regular,
} from '@fluentui/react-icons';
import { fetchProfilingStatus, fetchTraceLogs } from '../api/profilingStatusApi';
import type { DateRangeStat, ProfilingStatus, TraceLogPage } from '../types/profilingStatus';
import Spinner from '../components/Spinner';
import SqlPopover from '../components/SqlPopover';

const PAGE_SIZES = [25, 50, 100];

const useStyles = makeStyles({
  intro: {
    marginTop: '8px',
  },
  sectionTitle: {
    marginTop: '24px',
  },
  card: {
    marginTop: '12px',
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  tableName: {
    color: tokens.colorNeutralForeground3,
    fontFamily: 'Consolas, Menlo, Monaco, "Courier New", monospace',
  },
  message: {
    whiteSpace: 'pre-wrap',
    wordBreak: 'break-word',
  },
  toolbar: {
    display: 'flex',
    alignItems: 'center',
    gap: '12px',
    flexWrap: 'wrap',
  },
  spacer: {
    flexGrow: 1,
  },
  pager: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    marginTop: '12px',
    flexWrap: 'wrap',
  },
});

/** Formats an ISO date string as a local date, or an em dash when there's no data. */
function formatDate(d: string | null): string {
  return d ? new Date(d).toLocaleDateString() : '—';
}

/** A titled card with a table of earliest/latest dates for a set of tables. */
function RangeSection({
  title,
  description,
  stats,
}: {
  title: string;
  description: string;
  stats: DateRangeStat[];
}) {
  const styles = useStyles();
  return (
    <Card className={styles.card}>
      <Text weight="semibold" size={400}>
        {title}
      </Text>
      <Text size={200} block className={styles.muted} style={{ marginBottom: '8px' }}>
        {description}
      </Text>
      <Table size="small" aria-label={title}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>Data</TableHeaderCell>
            <TableHeaderCell style={{ width: 150 }}>Earliest</TableHeaderCell>
            <TableHeaderCell style={{ width: 150 }}>Latest</TableHeaderCell>
            <TableHeaderCell style={{ width: 90 }}>SQL</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {stats.map((s) => (
            <TableRow key={s.key}>
              <TableCell>
                <div>
                  <Text weight="semibold">{s.label}</Text>
                  <Text size={200} block className={styles.tableName}>
                    {s.table}
                  </Text>
                </div>
              </TableCell>
              {s.error ? (
                <TableCell colSpan={2}>
                  <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{s.error}</Text>
                </TableCell>
              ) : (
                <>
                  <TableCell>{formatDate(s.from)}</TableCell>
                  <TableCell>{formatDate(s.to)}</TableCell>
                </>
              )}
              <TableCell>
                <SqlPopover sql={s.sql} title="SQL to reproduce these dates" />
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Card>
  );
}

const TRACE_SQL = 'SELECT Id, [Datetime], Message FROM profiling.TraceLogs ORDER BY Id DESC;';

/**
 * Admin page: the current state of the profiling data. Shows how fresh each profiling output table
 * and source activity table is (earliest/latest date), and a paged view of the profiling runbooks'
 * own trace log - so admins can quickly see whether the runbooks have run or hit an error.
 */
export default function ProfilingStatusPage() {
  const styles = useStyles();

  // Re-fetch both sections when the user clicks Refresh.
  const [reloadKey, setReloadKey] = useState(0);

  // Data-freshness section.
  const [status, setStatus] = useState<ProfilingStatus | null>(null);
  const [statusLoading, setStatusLoading] = useState(true);
  const [statusError, setStatusError] = useState<string | null>(null);

  // Trace-log section.
  const [page, setPage] = useState(0);
  const [pageSize, setPageSize] = useState(50);
  const [trace, setTrace] = useState<TraceLogPage | null>(null);
  const [traceLoading, setTraceLoading] = useState(true);
  const [traceError, setTraceError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setStatusLoading(true);
    setStatusError(null);
    fetchProfilingStatus()
      .then((s) => {
        if (!cancelled) setStatus(s);
      })
      .catch((e) => {
        if (!cancelled) setStatusError(e instanceof Error ? e.message : 'Failed to load profiling status.');
      })
      .finally(() => {
        if (!cancelled) setStatusLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [reloadKey]);

  useEffect(() => {
    let cancelled = false;
    setTraceLoading(true);
    setTraceError(null);
    fetchTraceLogs(page, pageSize)
      .then((p) => {
        if (!cancelled) setTrace(p);
      })
      .catch((e) => {
        if (!cancelled) setTraceError(e instanceof Error ? e.message : 'Failed to load trace logs.');
      })
      .finally(() => {
        if (!cancelled) setTraceLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [page, pageSize, reloadKey]);

  const total = trace?.totalCount ?? 0;
  const canPrev = page > 0;
  const canNext = (page + 1) * pageSize < total;
  const firstRow = total === 0 ? 0 : page * pageSize + 1;
  const lastRow = Math.min((page + 1) * pageSize, total);

  const refreshAll = () => setReloadKey((k) => k + 1);

  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '12px', flexWrap: 'wrap' }}>
        <Title3>Profiling</Title3>
        <Button appearance="subtle" icon={<ArrowClockwise16Regular />} onClick={refreshAll} disabled={statusLoading && traceLoading}>
          Refresh
        </Button>
      </div>
      <Body1 block className={styles.intro}>
        The current state of the profiling data: how fresh each table is, and the profiling runbooks'
        own trace log. Use this to check the runbooks have run and that data is up to date.
      </Body1>

      <Text className={styles.sectionTitle} weight="semibold" size={500} block>
        Data freshness
      </Text>

      {statusLoading && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={64} label="Loading profiling status..." />
        </div>
      )}
      {statusError && (
        <MessageBar intent="error">
          <MessageBarBody>{statusError}</MessageBarBody>
        </MessageBar>
      )}
      {!statusLoading && status && (
        <>
          <RangeSection
            title="Compiled profiling data"
            description="Built by the profiling runbooks. If these are empty or stale, the runbooks haven't run (or errored)."
            stats={status.compiledProfiling}
          />
          <RangeSection
            title="Source activity data"
            description="The raw activity-log tables that feed the profiling compile, imported from the Microsoft 365 usage reports."
            stats={status.activityTables}
          />
        </>
      )}

      <div className={styles.sectionTitle} style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
        <Text weight="semibold" size={500}>
          Trace logs
        </Text>
        <SqlPopover sql={TRACE_SQL} title="SQL behind the trace log" buttonLabel="SQL" />
      </div>
      <Body1 block className={styles.muted} style={{ marginTop: '4px' }}>
        Trace output written by the profiling runbooks (<code>profiling.TraceLogs</code>), newest first.
      </Body1>

      {traceError && (
        <MessageBar intent="error" style={{ marginTop: '12px' }}>
          <MessageBarBody>{traceError}</MessageBarBody>
        </MessageBar>
      )}
      {!traceError && trace && trace.error && (
        <MessageBar intent="warning" style={{ marginTop: '12px' }}>
          <MessageBarBody>Couldn't read the profiling trace logs: {trace.error}</MessageBarBody>
        </MessageBar>
      )}

      {!traceError && (!trace || !trace.error) && (
        <Card className={styles.card}>
          <div className={styles.toolbar}>
            <Text size={200} className={styles.muted}>
              {traceLoading && !trace
                ? 'Loading…'
                : total === 0
                  ? 'No trace logs.'
                  : `Showing ${firstRow.toLocaleString()}–${lastRow.toLocaleString()} of ${total.toLocaleString()}`}
            </Text>
            <div className={styles.spacer} />
            <Text size={200}>Rows per page</Text>
            <Select
              value={String(pageSize)}
              onChange={(_e, data) => {
                setPageSize(Number(data.value));
                setPage(0);
              }}
              aria-label="Rows per page"
            >
              {PAGE_SIZES.map((n) => (
                <option key={n} value={n}>
                  {n}
                </option>
              ))}
            </Select>
          </div>

          {traceLoading ? (
            <div style={{ textAlign: 'center', padding: '32px' }}>
              <Spinner size={48} label="Loading trace logs..." />
            </div>
          ) : (
            <Table size="small" aria-label="Profiling trace logs">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell style={{ width: 200 }}>When</TableHeaderCell>
                  <TableHeaderCell>Message</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {trace && trace.rows.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={2}>
                      <Text className={styles.muted}>No trace logs on this page.</Text>
                    </TableCell>
                  </TableRow>
                )}
                {trace?.rows.map((r) => (
                  <TableRow key={r.id}>
                    <TableCell>{new Date(r.datetime).toLocaleString()}</TableCell>
                    <TableCell>
                      <span className={styles.message}>{r.message}</span>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}

          <div className={styles.pager}>
            <Button
              appearance="subtle"
              size="small"
              icon={<ChevronLeft16Regular />}
              disabled={!canPrev || traceLoading}
              onClick={() => setPage((p) => Math.max(0, p - 1))}
            >
              Previous
            </Button>
            <Button
              appearance="subtle"
              size="small"
              icon={<ChevronRight16Regular />}
              iconPosition="after"
              disabled={!canNext || traceLoading}
              onClick={() => setPage((p) => p + 1)}
            >
              Next
            </Button>
          </div>
        </Card>
      )}
    </div>
  );
}
