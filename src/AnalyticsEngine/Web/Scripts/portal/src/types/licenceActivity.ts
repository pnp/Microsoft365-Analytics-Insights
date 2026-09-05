// Types for the Licence activity report (api/LicenceActivity), issues #436 / #437.
//
// These mirror the backend DTOs in Common/Entities/LicenceActivity/LicenceActivityModels.cs and
// LicenceActivityQuery.cs (serialised camelCase). Two rules are baked into the model itself:
//   1. UNKNOWN IS NOT ZERO. Every activity distribution carries `zero` (measured: no activity) and
//      `unknown` (not measured) as SEPARATE counts, and each user's per-workload evidence has a
//      `status`/`band` that can be "unknown". The UI must keep them visually distinct.
//   2. NO DISPLAY NAMES. The backend does not import display names, so `LicenceActivityUser` has only
//      a userPrincipalName. Nothing in the UI may synthesise a name.

/** The five workloads whose activity is distributed separately. Matches LicenceActivityQuery.Workloads. */
export type WorkloadKey = 'teams' | 'outlook' | 'onedrive' | 'sharepoint' | 'copilot';

export const WORKLOADS: { key: WorkloadKey; label: string }[] = [
  { key: 'teams', label: 'Teams' },
  { key: 'outlook', label: 'Outlook' },
  { key: 'onedrive', label: 'OneDrive' },
  { key: 'sharepoint', label: 'SharePoint' },
  { key: 'copilot', label: 'Copilot' },
];

/** Sort keys accepted by the users endpoint. Note: no "name" - there are no display names to sort by. */
export type UsersSortKey = 'upn' | 'activity' | 'lastActivity';

export type SortDirection = 'asc' | 'desc';

/** An inclusive UTC date range as `YYYY-MM-DD` strings (the overview `from`/`to` params). */
export interface DateRange {
  from: string;
  to: string;
}

/** The query the backend actually applied, echoed back on each snapshot. */
export interface LicenceActivityQueryEcho {
  from: string;
  to: string;
  departmentId: number | null;
  countryId: number | null;
  licenceTypeId: number | null;
  workload: WorkloadKey;
  search: string;
  sort: UsersSortKey;
  direction: SortDirection;
  top: number;
  page: number;
  pageSize: number;
}

/**
 * A per-workload activity distribution: how many assigned users fall in each engagement band.
 *
 * `zero` (measured inactivity) and `unknown` (no measurement for this user/workload) are deliberately
 * separate, so the UI can render "not measured" differently from "measured, and it was zero".
 */
export interface LicenceActivityDistribution {
  workload: WorkloadKey | string;
  high: number;
  moderate: number;
  low: number;
  zero: number;
  unknown: number;
}

/** One licence SKU with how many hold it and its five workload distributions. */
export interface LicenceActivitySku {
  licenceTypeId: number;
  name: string;
  skuId: string | null;
  assignedUsers: number;
  workloads: LicenceActivityDistribution[];
}

/** A demographic slice (department or country): a filter option that also carries its distributions. */
export interface LicenceActivityDemographic {
  id: number;
  name: string;
  assignedUsers: number;
  workloads: LicenceActivityDistribution[];
}

/**
 * How a single workload's data was sourced for this snapshot, surfaced verbatim so no figure is quoted
 * without its provenance and freshness. `status` is a backend string (rendered by value); the
 * sample/user counts say how completely the source covered the scope.
 */
export interface LicenceActivityCoverage {
  workload: WorkloadKey | string;
  status: string;
  source: string | null;
  measure: string | null;
  granularity: string | null;
  message: string | null;
  effectiveFromUtc: string | null;
  effectiveToUtc: string | null;
  latestImportUtc: string | null;
  lagDays: number;
  reportPeriodDays: number | null;
  expectedSamples: number;
  observedSamples: number;
  unmatchedUsers: number;
  snapshotDates: string[];
}

/** Fields on every snapshot: its id (used for the exact-snapshot export) and its lifetime. */
export interface SnapshotEnvelope {
  snapshotId: string;
  generatedUtc: string;
  /** When the cached snapshot expires; after this the export/users endpoints answer 410. */
  expiresUtc: string;
}

/** Which parts of the tool this deployment / signed-in user can see. */
export interface LicenceActivityAvailability {
  /** False when the licence import (GraphUsersMetadata) is disabled - the tab stays, the report can't run. */
  available: boolean;
  /**
   * True when the signed-in user holds the opt-in `LicenceActivity.ReadUsers` Entra app role. Gates the
   * per-user drill-down in the UI; every signed-in user still gets the aggregates. The server enforces
   * the role on the users/export endpoints regardless of this flag.
   */
  canViewUsers: boolean;
  /** Smallest allowed custom window, in days (backend LicenceActivityQuery.MinimumDays). */
  minimumDays: number;
  /** Largest allowed custom window, in days (backend LicenceActivityQuery.MaximumDays). */
  maximumDays: number;
  messages: string[];
}

/** The executive overview: SKU assignments, five workload distributions and per-workload coverage. */
export interface LicenceActivityOverview extends SnapshotEnvelope {
  query: LicenceActivityQueryEcho;
  distinctAssignedUsers: number;
  licences: LicenceActivitySku[];
  coverage: LicenceActivityCoverage[];
  departments: LicenceActivityDemographic[];
  countries: LicenceActivityDemographic[];
  /** True when the department/country option lists were capped and are not exhaustive. */
  demographicsTruncated: boolean;
  messages: string[];
}

/** One user's activity evidence for a single workload. */
export interface LicenceActivityEvidence {
  workload: WorkloadKey | string;
  /** Backend status string; "unknown" means not measured (distinct from a measured "zero" band). */
  status: string;
  /** Engagement band: high | moderate | low | zero | unknown. */
  band: string;
  source: string | null;
  measure: string | null;
  activeSamples: number;
  observedSamples: number;
  expectedSamples: number;
  /** Average actions per observed sample, or null when unknown - never treated as 0. */
  averageActions: number | null;
  lastActivityUtc: string | null;
}

/** One user in the drill-down. There is no display name in the schema or import path - identify by
 * UPN only, and never synthesise a name from it. */
export interface LicenceActivityUser {
  userId: number;
  userPrincipalName: string;
  department: string | null;
  country: string | null;
  accountEnabled: boolean | null;
  workloads: LicenceActivityEvidence[];
}

/**
 * The users snapshot for one licence. A single request returns the bounded most/least active lists
 * AND the current browse page together, so the Excel export (which references this snapshot id) is
 * exactly what is on screen.
 */
export interface LicenceActivityUsers extends SnapshotEnvelope {
  overviewId: string;
  query: LicenceActivityQueryEcho;
  totalUsers: number;
  rankedUsers: number;
  mostActive: LicenceActivityUser[];
  leastActive: LicenceActivityUser[];
  users: LicenceActivityUser[];
  messages: string[];
}

/** Parameters for the overview request. */
export interface OverviewParams {
  from: string;
  to: string;
  /** A demographic id, or 0 for the "unknown" bucket. Undefined/null means "all". */
  departmentId?: number | null;
  countryId?: number | null;
}

/** Parameters for the users request (one call returns most/least/browse). */
export interface UsersParams {
  overviewId: string;
  licenceTypeId: number;
  workload: WorkloadKey;
  /** Bounds the most/least active lists (1..100). */
  top: number;
  search?: string;
  sort: UsersSortKey;
  direction: SortDirection;
  /** 1-based page of the browse list. */
  page: number;
  pageSize: number;
}
