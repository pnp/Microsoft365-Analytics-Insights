/**
 * The Copilot Adoption leadership comparison (#654) and its administration API.
 *
 * Every string field here is a stable KEY the server sends (`LeadershipComparisonStatuses`,
 * `LeadershipComparisonReasons`, `LeadershipCohortRefreshStatuses`, `LeadershipCohortFailureKinds`,
 * `LeadershipCohortErrorCodes`), never a sentence: the SPA maps each to a catalog entry.
 * `src/i18n/lint/serverAuthoredText.test.ts` fails when the two sides drift apart.
 */

export type LeadershipComparisonStatus =
  | 'notConfigured'
  | 'ok'
  | 'suppressed'
  | 'pendingRefresh'
  | 'unavailable'
  | 'stale'
  | 'scopedView';

export type LeadershipComparisonReason =
  | 'groupNotFound'
  | 'permissionMissing'
  | 'tooLarge'
  | 'refreshFailed'
  | 'stateUnavailable'
  | 'membershipChanging'
  | 'complementTooSmall';

/** Aggregates only: the report never receives the group's name, its id or any member. */
export interface LeadershipAdoptionComparison {
  status: LeadershipComparisonStatus | string;
  reason: LeadershipComparisonReason | string | null;
  minimumCohort: number;
  licensedLeaders: number | null;
  activeLeaders: number | null;
  habitualLeaders: number | null;
  leaderAdoptionRatePct: number | null;
  leaderHabitRatePct: number | null;
  leaderAverageScore: number | null;
  tenantAdoptionRatePct: number | null;
  tenantHabitRatePct: number | null;
  tenantAverageScore: number | null;
  adoptionGapPts: number | null;
  habitGapPts: number | null;
  scoreGap: number | null;
  membershipRefreshedUtc: string | null;
  figuresIncomplete: boolean;
}

export type LeadershipCohortRefreshStatus = 'ready' | 'groupNotFound' | 'permissionMissing' | 'tooLarge' | 'failed';

export type LeadershipCohortFailureKind = 'graphError' | 'graphClient' | 'sqlError';

export interface LeadershipCohortRefresh {
  status: LeadershipCohortRefreshStatus | string;
  failureKind: LeadershipCohortFailureKind | string | null;
  httpStatus: number | null;
  /** The Entra group's display name: tenant data, shown as stored and never translated. */
  groupDisplayName: string | null;
  attemptedUtc: string;
  refreshedUtc: string | null;
  directMembers: number;
  matchedUsers: number;
  stale: boolean;
}

export interface LeadershipCohortStatus {
  stateDurable: boolean;
  configured: boolean;
  groupId: string | null;
  updatedUtc: string | null;
  minimumCohort: number;
  maxMembers: number;
  refreshAfterSuccessHours: number;
  staleAfterHours: number;
  refresh: LeadershipCohortRefresh | null;
}

export type LeadershipCohortErrorCode =
  | 'invalidGroupId'
  | 'stateNotDurable'
  | 'stateUnavailable'
  | 'notConfigured'
  | 'refreshInProgress';
