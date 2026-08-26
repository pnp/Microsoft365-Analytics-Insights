// Mirrors Web/Models/ProfilingStatusModels.cs (returned by api/ProfilingStatus).

/** The earliest/latest date held in one table, plus the SQL behind it. */
export interface DateRangeStat {
  key: string;
  label: string;
  table: string;
  from: string | null;
  to: string | null;
  sql: string;
  /** Set when the query failed (e.g. the profiling schema doesn't exist yet); from/to are null. */
  error: string | null;
}

export interface ProfilingStatus {
  /** Compiled profiling output tables (built by the profiling runbooks). */
  compiledProfiling: DateRangeStat[];
  /** Raw activity-log tables that feed the profiling compile. */
  activityTables: DateRangeStat[];
}

/** One row of profiling.TraceLogs - a trace line written by the profiling runbooks. */
export interface TraceLogEntry {
  id: number;
  datetime: string;
  message: string;
}

/** A page of profiling.TraceLogs rows (newest first) with paging metadata. */
export interface TraceLogPage {
  rows: TraceLogEntry[];
  totalCount: number;
  page: number;
  pageSize: number;
  /** Set when the trace logs couldn't be read (e.g. the profiling schema doesn't exist yet). */
  error: string | null;
}
