// Mirrors Web/Models/ReportsModels.cs (returned by api/Reports).

/** Which report areas are available, based on the enabled imports. */
export interface ReportAreas {
  copilot: boolean;
  usage: boolean;
  spoAudit: boolean;
  webTraffic: boolean;
  calls: boolean;
  emails: boolean;
}

/** One point of a weekly series: the (Monday) week start (ISO date) and its value. A null value
 * means the week's figure is unknown (e.g. its usage report never arrived) rather than zero, and is
 * drawn as a gap in the line. */
export interface ReportTimePoint {
  weekStart: string;
  value: number | null;
}

/** A named line in a time-series chart. */
export interface ReportSeries {
  name: string;
  points: ReportTimePoint[];
}

/** One bar of a categorical chart. */
export interface ReportCategory {
  label: string;
  value: number;
}

export type ReportChartType = 'timeseries' | 'bar' | 'wordcloud';

/** A single chart: a weekly `timeseries` (series set), or a `bar` / `wordcloud` (categories set). */
export interface ReportChart {
  key: string;
  title: string;
  description: string;
  type: ReportChartType;
  valueLabel: string;
  series: ReportSeries[] | null;
  categories: ReportCategory[] | null;
  sql: string;
  error: string | null;
  warning: string | null;
}

/** The set of charts for one report area over the requested window. */
export interface ReportAreaData {
  area: string;
  months: number;
  fromWeek: string;
  charts: ReportChart[];
}

/** A report area key, as used in the api/Reports/{area} route. */
export type ReportAreaKey =
  | 'copilot'
  | 'copilot-agents'
  | 'usage'
  | 'spo-audit'
  | 'web-traffic'
  | 'calls'
  | 'emails';
