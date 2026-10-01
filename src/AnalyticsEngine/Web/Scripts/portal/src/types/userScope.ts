export type UserScopeResolutionStatus = 'unfiltered' | 'resolved' | 'unavailable';
export type UserScopeFailureKind = 'directoryRead' | 'budgetExhausted' | 'clientUnavailable' | 'unexpected';

export interface UserScopeGroup {
  id: string;
  displayName: string;
  userMemberCount: number;
  matchedPatterns: string[];
}

export interface UserScopeResolution {
  status: UserScopeResolutionStatus;
  failureKind: UserScopeFailureKind | null;
  httpStatus: number | null;
  resolvedUtc: string | null;
  memberCount: number;
  matchedNoGroup: boolean;
  groups: UserScopeGroup[];
  unmatchedPatterns: string[];
}

export interface UserScopeDatabaseCounts {
  totalUsers: number;
  inScopeUsers: number;
  outOfScopeUsers: number;
}

export type UserScopePurgeUnavailableReason =
  | 'notFiltered'
  | 'scopeUnavailable'
  | 'scopeEmpty'
  | 'nothingToPurge'
  | 'jobActive'
  | 'storageUnavailable';

export interface UserScopeStatus {
  filtered: boolean;
  filterPatterns: string[];
  resolution: UserScopeResolution;
  database: UserScopeDatabaseCounts | null;
  purgeUnavailableReason: UserScopePurgeUnavailableReason | null;
  latestJob: UserScopePurgeJob | null;
  /** Whether purge records survive a web app restart and every instance sees them; false while they are in memory. */
  purgeStateDurable: boolean;
}

export type UserScopePurgeState = 'queued' | 'running' | 'completed' | 'failed' | 'cancelled';
export type UserScopePurgePhase =
  | 'snapshot'
  | 'auditEvents'
  | 'webActivity'
  | 'calls'
  | 'pageComments'
  | 'sentEmails'
  | 'teams'
  | 'usageReports'
  | 'copilotInteractions'
  | 'licencesAndCredits'
  | 'sharedWith'
  | 'managers'
  | 'users'
  | 'done';

export interface UserScopePurgeTableCount {
  table: string;
  rows: number;
}

export interface UserScopePurgeJob {
  id: number;
  state: UserScopePurgeState;
  phase: UserScopePurgePhase;
  stepIndex: number;
  stepCount: number;
  candidateCount: number;
  usersDeleted: number;
  usersSkipped: number;
  rowsAffected: UserScopePurgeTableCount[];
  cancelRequested: boolean;
  requestedBy: string | null;
  createdUtc: string;
  startedUtc: string | null;
  updatedUtc: string;
  completedUtc: string | null;
  errorCode:
    | 'scopeUnavailable'
    | 'scopeEmpty'
    | 'filterChanged'
    | 'filterChangedWhileRunning'
    | 'databaseError'
    | 'stateUnavailable'
    | 'unexpected'
    | null;
}
