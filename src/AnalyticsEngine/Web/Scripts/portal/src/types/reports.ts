// Mirrors Web/Models/ReportsModels.cs (returned by api/Reports).

/** Which report areas are available, based on the enabled imports. */
export interface ReportAreas {
  copilot: boolean;
  usage: boolean;
  spoAudit: boolean;
  webTraffic: boolean;
  calls: boolean;
  emails: boolean;
  /**
   * Microsoft 365 apps (Word/Excel/PowerPoint/Outlook/OneNote/Teams) and their platforms.
   * Gated by the same Graph usage-report import as `usage`.
   */
  officeApps: boolean;
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

export type ReportChartType = 'timeseries' | 'bar' | 'wordcloud' | 'matrix';

/** One cell of a `matrix` chart. */
export interface ReportMatrixCell {
  row: string;
  column: string;
  value: number;
}

/**
 * A two-dimensional categorical grid, e.g. app x department.
 *
 * `rows` and `columns` are sent explicitly rather than inferred from `cells` so the grid keeps the
 * server's deliberate ordering and so an all-zero row still renders - an app nobody in a department
 * uses is usually the point of looking.
 */
export interface ReportMatrix {
  rowLabel: string;
  columnLabel: string;
  rows: string[];
  columns: string[];
  /** Populated cells only; an absent intersection is zero. */
  cells: ReportMatrixCell[];
  /** Shade each cell against its own row's maximum rather than the whole grid. */
  shadeByRow: boolean;
}

/** A single chart: a weekly `timeseries` (series set), a `bar` / `wordcloud` (categories set), or a `matrix`. */
export interface ReportChart {
  key: string;
  title: string;
  description: string;
  type: ReportChartType;
  valueLabel: string;
  series: ReportSeries[] | null;
  categories: ReportCategory[] | null;
  matrix: ReportMatrix | null;
  /** Show each bar's share of the total. Only meaningful when the bars are parts of one whole. */
  showShare: boolean;
  /** Unit appended to rendered values, e.g. '%'. Null for a plain count. */
  valueSuffix: string | null;
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
  /**
   * Whether Azure AI Language (cognitive services) is configured. Only meaningful on the "copilot"
   * area: when false the API omits the three prompt-insight charts, and the page explains why
   * rather than letting them disappear without a word.
   */
  cognitiveConfigured?: boolean;
}

/** A report area key, as used in the api/Reports/{area} route. */
export type ReportAreaKey =
  | 'copilot'
  | 'copilot-agents'
  | 'usage'
  | 'office-apps'
  | 'spo-audit'
  | 'web-traffic'
  | 'calls'
  | 'emails';
