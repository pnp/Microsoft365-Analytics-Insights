// Mirrors Web/Controllers/ActivityAnalysisAPIController.cs (returned by api/ActivityAnalysis).
//
// The page replicates the Power BI "Activity Analysis" and "Filter Settings" views over the weekly
// profiling roll-up (`profiling.ActivitiesWeeklyColumns`). Every key, unit and reason below is a
// stable contract key: the server reports facts and the portal writes the sentences.

import type { UserFilterEcho } from './userFilter';

/** The metric categories, in the order the slicer offers them. */
export type ActivityAnalysisCategory = 'teams' | 'outlook' | 'onedrive' | 'sharepoint' | 'copilot' | 'vivaEngage';

/** `count` for a number of things; `seconds` for a duration, which the portal shows in hours. */
export type ActivityAnalysisUnit = 'count' | 'seconds';

/** Why the page cannot be shown: the profiling tables are missing, or hold no rows yet. */
export type ActivityAnalysisUnavailableReason = 'notInstalled' | 'noData';

/** One metric the page can analyse - a column of `profiling.ActivitiesWeeklyColumns`. */
export interface ActivityAnalysisMetric {
  /** Stable key, e.g. `teams.calls`. Never renamed. */
  key: string;
  category: ActivityAnalysisCategory | string;
  unit: ActivityAnalysisUnit | string;
  /** Offered in the filter panel's activity ranges by default. */
  core: boolean;
  /** False when this installation's profiling table has no column for it. */
  available: boolean;
  /** English fallback only - the portal translates the key, and shows this for a key it does not know. */
  label: string;
}

/** `GET api/ActivityAnalysis/availability`. */
export interface ActivityAnalysisAvailability {
  available: boolean;
  reason: ActivityAnalysisUnavailableReason | string | null;
  /** The Monday of the earliest and latest week with data (`yyyy-MM-dd`), or null when there is none. */
  earliestWeek: string | null;
  latestWeek: string | null;
  /** The default period: the latest 52 weeks, clamped to the earliest week. */
  defaultFrom: string | null;
  defaultTo: string | null;
  /** The longest period, in weeks, the report accepts. */
  maximumWeeks: number;
  categories: string[];
  metrics: ActivityAnalysisMetric[];
}

/** One selected metric's weekly figures, aligned with `weekStarts`. */
export interface ActivityAnalysisSeries {
  metric: string;
  /** Total activity per week (raw seconds for a duration). */
  sum: number[];
  /** People with activity in the metric that week. */
  activePeople: number[];
}

/**
 * A company or department in a breakdown. `name: null, other: false` is "not set"; `other: true` is
 * the roll-up of groups too small to show on their own, or beyond the largest 50.
 */
export interface ActivityAnalysisGroupRow {
  name: string | null;
  other: boolean;
  activePeople: number;
}

export interface ActivityAnalysisGroupBreakdown {
  rows: ActivityAnalysisGroupRow[];
  /** How many groups were folded into the `other` row. */
  otherGroups: number;
}

/** A metric's figures for a group: its total, and how many people have any of it. */
export interface ActivityAnalysisMetricValue {
  metric: string;
  sum: number;
  unique: number;
}

/** One row of the results matrix. */
export interface ActivityAnalysisDepartmentRow {
  name: string | null;
  other: boolean;
  /** Matching people in the department. */
  people: number;
  values: ActivityAnalysisMetricValue[];
}

export interface ActivityAnalysisTotal {
  people: number;
  values: ActivityAnalysisMetricValue[];
}

/** A licence held by someone in the population, for the filter panel's licence picker. */
export interface ActivityAnalysisLicence {
  id: number;
  name: string;
  skuId: string | null;
  /** Holders among the population. */
  people: number;
}

/** The largest per-person total of a metric over the population: the activity ranges' upper bound. */
export interface ActivityAnalysisRangeMaximum {
  metric: string;
  max: number;
}

/** `GET api/ActivityAnalysis/report`. */
export interface ActivityAnalysisReport {
  generatedUtc: string;
  from: string;
  to: string;
  /** Every Monday in the period (`yyyy-MM-dd`). */
  weekStarts: string[];
  /** The selected metrics, echoed in the order requested. */
  metrics: string[];
  populationPeople: number;
  matchingPeople: number;
  activePeople: number;
  /** True when a reader without See PII would see 1-4 people: every figure is then empty. */
  suppressed: boolean;
  series: ActivityAnalysisSeries[];
  byCompany: ActivityAnalysisGroupBreakdown;
  byDepartment: ActivityAnalysisGroupBreakdown;
  departments: ActivityAnalysisDepartmentRow[];
  otherDepartments: number;
  total: ActivityAnalysisTotal;
  licences: ActivityAnalysisLicence[];
  /** Empty for a reader without See PII: each maximum is one person's total. */
  rangeMaxima: ActivityAnalysisRangeMaximum[];
  userFilter: UserFilterEcho | null;
}

export interface ActivityAnalysisPersonValue {
  metric: string;
  sum: number;
}

export interface ActivityAnalysisPerson {
  userPrincipalName: string;
  department: string | null;
  values: ActivityAnalysisPersonValue[];
}

/** `GET api/ActivityAnalysis/people` - needs See PII. */
export interface ActivityAnalysisPeople {
  department: string | null;
  noDepartment: boolean;
  totalPeople: number;
  truncated: boolean;
  people: ActivityAnalysisPerson[];
}

/**
 * An inclusive range on a person's total of one metric over the period, in the API's units (seconds
 * for a duration). A null bound is open.
 */
export interface ActivityAnalysisRange {
  metric: string;
  min: number | null;
  max: number | null;
}

/** The parameters every report and people request carries. */
export interface ActivityAnalysisQuery {
  /** The Monday the period starts and ends on (`yyyy-MM-dd`). */
  from: string;
  to: string;
  /** Selected metric keys, 1..58. */
  metrics: string[];
  /** The serialised user filter (`serializeUserFilter`), or null for everyone. */
  userFilter?: string | null;
  /** Licence type ids: people holding ANY of them. */
  licences?: number[];
  ranges?: ActivityAnalysisRange[];
}

/** A people request: the report's query, narrowed to a department - or none, for the top people. */
export interface ActivityAnalysisPeopleQuery extends ActivityAnalysisQuery {
  /** The department's name. Omit for everyone. */
  department?: string | null;
  /** The "not set" department. */
  noDepartment?: boolean;
  /** A selected metric to rank by; the server defaults to the first selected metric. */
  sort?: string;
  /** 1..500; the server defaults to 100. */
  top?: number;
}
