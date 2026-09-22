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
import { formatDateParts, formatNumber, useT, type TFunction } from '../i18n';

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
  return d ? formatDateParts(new Date(d), { dateStyle: 'short' }) : '—';
}

/** A titled card with a table of earliest/latest dates for a set of tables. */
function RangeSection({
  title,
  description,
  stats,
  t,
}: {
  title: string;
  description: string;
  stats: DateRangeStat[];
  t: TFunction;
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
            <TableHeaderCell>{t('admin.profiling.rangeSection.columnData')}</TableHeaderCell>
            <TableHeaderCell style={{ width: 150 }}>{t('admin.profiling.rangeSection.columnEarliest')}</TableHeaderCell>
            <TableHeaderCell style={{ width: 150 }}>{t('admin.profiling.rangeSection.columnLatest')}</TableHeaderCell>
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
                <SqlPopover sql={s.sql} title={t('admin.profiling.rangeSection.sqlTitle')} />
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
  const t = useT();

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
      .catch((e: any) => {
        if (!cancelled) setStatusError(e instanceof Error ? e.message : t('admin.profiling.errors.loadStatusFailed'));
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
      .catch((e: any) => {
        if (!cancelled) setTraceError(e instanceof Error ? e.message : t('admin.profiling.errors.loadTraceLogsFailed'));
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
        <Title3>{t('admin.profiling.title')}</Title3>
        <Button appearance="subtle" icon={<ArrowClockwise16Regular />} onClick={refreshAll} disabled={statusLoading && traceLoading}>
          {t('admin.profiling.refresh')}
        </Button>
      </div>
      <Body1 block className={styles.intro}>
        {t('admin.profiling.description')}
      </Body1>

      <Text className={styles.sectionTitle} weight="semibold" size={500} block>
        {t('admin.profiling.dataFreshness')}
      </Text>

      {statusLoading && (
        <div style={{ textAlign: 'center', padding: '32px' }}>
          <Spinner size={64} label={t('admin.profiling.loadingStatus')} />
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
            title={t('admin.profiling.compiledData.title')}
            description={t('admin.profiling.compiledData.description')}
            stats={status.compiledProfiling}
            t={t}
          />
          <RangeSection
            title={t('admin.profiling.sourceActivityData.title')}
            description={t('admin.profiling.sourceActivityData.description')}
            stats={status.activityTables}
            t={t}
          />
        </>
      )}

      <div className={styles.sectionTitle} style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
        <Text weight="semibold" size={500}>
          {t('admin.profiling.traceLogs.title')}
        </Text>
        <SqlPopover sql={TRACE_SQL} title={t('admin.profiling.traceLogs.sqlTitle')} buttonLabel="SQL" />
      </div>
      <Body1 block className={styles.muted} style={{ marginTop: '4px' }}>
        {t('admin.profiling.traceLogs.description')}
      </Body1>

      {traceError && (
        <MessageBar intent="error" style={{ marginTop: '12px' }}>
          <MessageBarBody>{traceError}</MessageBarBody>
        </MessageBar>
      )}
      {!traceError && trace && trace.error && (
        <MessageBar intent="warning" style={{ marginTop: '12px' }}>
          <MessageBarBody>{t('admin.profiling.traceLogs.readFailed', { error: trace.error })}</MessageBarBody>
        </MessageBar>
      )}

      {!traceError && (!trace || !trace.error) && (
        <Card className={styles.card}>
          <div className={styles.toolbar}>
            <Text size={200} className={styles.muted}>
              {traceLoading && !trace
                ? t('admin.profiling.traceLogs.loading')
                : total === 0
                  ? t('admin.profiling.traceLogs.none')
                  : t('admin.profiling.traceLogs.showing', {
                      firstRow: formatNumber(firstRow),
                      lastRow: formatNumber(lastRow),
                      total: formatNumber(total),
                    })}
            </Text>
            <div className={styles.spacer} />
            <Text size={200}>{t('admin.profiling.traceLogs.rowsPerPage')}</Text>
            <Select
              value={String(pageSize)}
              onChange={(_e: any, data: any) => {
                setPageSize(Number(data.value));
                setPage(0);
              }}
              aria-label={t('admin.profiling.traceLogs.rowsPerPage')}
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
              <Spinner size={48} label={t('admin.profiling.traceLogs.loadingTraceLogs')} />
            </div>
          ) : (
            <Table size="small" aria-label={t('admin.profiling.traceLogs.ariaLabel')}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell style={{ width: 200 }}>{t('admin.profiling.traceLogs.columnWhen')}</TableHeaderCell>
                  <TableHeaderCell>{t('admin.profiling.traceLogs.columnMessage')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {trace && trace.rows.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={2}>
                      <Text className={styles.muted}>{t('admin.profiling.traceLogs.noneOnPage')}</Text>
                    </TableCell>
                  </TableRow>
                )}
                {trace?.rows.map((r) => (
                  <TableRow key={r.id}>
                    <TableCell>
                      {formatDateParts(new Date(r.datetime), { dateStyle: 'short', timeStyle: 'medium' })}
                    </TableCell>
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
              {t('admin.profiling.traceLogs.previous')}
            </Button>
            <Button
              appearance="subtle"
              size="small"
              icon={<ChevronRight16Regular />}
              iconPosition="after"
              disabled={!canNext || traceLoading}
              onClick={() => setPage((p) => p + 1)}
            >
              {t('admin.profiling.traceLogs.next')}
            </Button>
          </div>
        </Card>
      )}
    </div>
  );
}
