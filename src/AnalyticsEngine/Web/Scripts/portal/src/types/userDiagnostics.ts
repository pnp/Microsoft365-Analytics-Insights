// Shapes returned by the api/UserDataLookup diagnostics endpoints. These mirror the C# models in
// Common/Entities/UserDiagnostics/*.cs and Web/Models/UserDiagnostics/*.cs (kept in sync by hand).
//
// Every value a reader sees as a sentence is a stable key; the catalog in i18n/catalog/*/userDiagnostics.ts
// writes the sentence. Timestamps say what they measure: "metadata last written" is not "licences last checked".

export interface ClassificationExplanation {
  isCopilotSeat: boolean;
  /** One of the classifier's reason keys (seatSkuPrefix, excludedSkuPrefix, ...). */
  reason: string;
  matchedRule: string | null;
}

export interface StoredUser {
  userId: number;
  userPrincipalName: string;
  mail: string | null;
  azureAdId: string | null;
  accountEnabled: boolean | null;
  metadataLastWrittenUtc: string | null;
}

export interface UserScopeDecision {
  decision: 'unfiltered' | 'inScope' | 'excluded' | 'unknown';
  liveComparisonAllowed: boolean;
  resolvedUtc: string | null;
  matchedGroupCount: number | null;
  memberCount: number | null;
  failureKind: string | null;
}

export interface StoredLicence {
  licenceTypeId: number;
  name: string;
  skuPartNumber: string | null;
  classification: ClassificationExplanation;
  catalogueListed: boolean;
  catalogueCarriesCopilotSeat: boolean;
  heldSinceUtc: string | null;
  heldSinceKind: 'observed' | 'beforeTrackingBegan' | 'unknown';
}

export interface LicenceHistoryRow {
  licenceTypeId: number;
  name: string;
  skuPartNumber: string | null;
  validFromUtc: string;
  validFromPreviousRefreshUtc: string | null;
  validToUtc: string | null;
  validToPreviousRefreshUtc: string | null;
  heldBeforeTrackingBegan: boolean;
  /** An administrator's one-person refresh saw the change; when it happened before that is unknown. */
  recordedByUserRefresh?: boolean;
}

export interface LicenceHistory {
  available: boolean;
  trackingBeganUtc: string | null;
  rows: LicenceHistoryRow[];
  truncated: boolean;
}

export interface LicenceFreshness {
  userImportEnabled: boolean | null;
  userImportIntervalHours: number;
  userImportLastCompletedUtc: string | null;
  userImportStampState: string;
  lastTenantLicenceRefreshUtc: string | null;
  userVerification: 'notRecorded' | 'verified';
  userVerifiedUtc: string | null;
}

export interface TenantLicenceType {
  id: number;
  name: string;
  skuPartNumber: string | null;
  assignedUsers: number;
  classification: ClassificationExplanation;
  catalogueListed: boolean;
  catalogueCarriesCopilotSeat: boolean;
  possibleRuleGap: boolean;
}

export interface ClassificationCatalogue {
  ruleSetVersion: string;
  buildLabel: string | null;
  catalogueContentHash: string;
  catalogueSkuCount: number;
  seatSkuPrefixes: string[];
  excludedSkuPrefixes: string[];
  copilotSeatServicePlans: string[];
  tenantLicenceTypes: TenantLicenceType[];
}

export interface DiagnosticFinding {
  key: string;
  severity: 'info' | 'warning';
  values?: Record<string, unknown>;
}

export interface UserDiagnostics {
  lookedUpUpn: string;
  storedUser: StoredUser | null;
  scope: UserScopeDecision;
  licences: StoredLicence[];
  history: LicenceHistory;
  freshness: LicenceFreshness;
  catalogue: ClassificationCatalogue;
  findings: DiagnosticFinding[];
  /** Absent from a server older than the one-person refresh. */
  refresh?: UserRefreshCapability;
  loadedAtUtc: string;
}

export type UserRefreshState = 'queued' | 'running' | 'completed' | 'failed' | 'expired';

/** One administrator's request to re-read one person's profile and licences, as the importer recorded it. */
export interface UserRefreshRequest {
  id: string;
  userPrincipalName: string;
  userId: number;
  requestedBy: string | null;
  requestedUtc: string;
  expiresUtc: string;
  state: UserRefreshState;
  attempts: number;
  startedUtc: string | null;
  completedUtc: string | null;
  observedUtc: string | null;
  /** A stable key (updated, unchanged, partial, scopeExcluded, ...); null until finished. */
  outcome: string | null;
  /** Licence names exactly as stored - product names, never translated. */
  added: string[];
  removed: string[];
  heldBack: string[];
  /** Stable profile-field keys (department, jobTitle, ...). */
  profileChanges: string[];
  notes: string[];
}

export interface UserRefreshCapability {
  available: boolean;
  /** Why not, when unavailable: storageNotConfigured, importDisabled, notStored, scopeExcluded, scopeUnknown, storageUnavailable. */
  reason: string | null;
  active: UserRefreshRequest | null;
  recent: UserRefreshRequest[];
}

export interface UserRefreshSubmitResult {
  accepted: boolean;
  rejection: string | null;
  request: UserRefreshRequest | null;
}

export interface LiveSku {
  skuId: string;
  skuPartNumber: string | null;
  catalogueName: string | null;
  assignmentPaths: string[];
  state: string | null;
  error: string | null;
  copilotSeatPlans: 'enabled' | 'disabled' | 'pending' | 'notIncluded' | 'unknown';
}

export interface LiveLicenceRow {
  licenceTypeId: number | null;
  name: string | null;
  skuPartNumber: string | null;
  stored: boolean;
  live: boolean;
  liveSkus: LiveSku[];
  mapping: 'unique' | 'collapsed' | 'unmapped' | 'catalogueDrift' | 'notApplicable';
  comparison: 'match' | 'storedOnly' | 'liveOnly' | 'indeterminate';
  classification: ClassificationExplanation;
}

export interface LiveLicenceComparison {
  status: 'ok' | 'notAllowed' | 'failed';
  reason: string | null;
  httpStatus: number | null;
  observedAtUtc: string | null;
  directory: {
    objectId: string | null;
    userPrincipalName: string | null;
    accountEnabled: boolean | null;
    objectIdMatchesStored: boolean | null;
    userPrincipalNameDiffersFromStored: boolean;
  } | null;
  complete: boolean;
  partNumberSource: string | null;
  rows: LiveLicenceRow[];
}

export interface VerdictLicenceType {
  id: number;
  name: string;
  skuPartNumber: string | null;
  classification: ClassificationExplanation;
}

export interface VerdictEvidenceRow {
  licenceTypeId: number;
  name: string | null;
  skuPartNumber: string | null;
  validFromUtc: string | null;
  validToUtc: string | null;
  heldBeforeTrackingBegan: boolean;
}

export type VerdictReport = 'copilotAdoption' | 'licenceActivity';

export interface ReportLicenceVerdict {
  report: VerdictReport;
  verdict: 'licensed' | 'unlicensed';
  basis: 'currentAssignments' | 'licenceHistory';
  fromUtc: string | null;
  toExclusiveUtc: string | null;
  windowDays: number | null;
  usesExplicitDates: boolean;
  historyTrackingBeganUtc: string | null;
  seatSelection: 'automatic' | 'reportOverride';
  countedLicenceTypes: VerdictLicenceType[];
  decidingRows: VerdictEvidenceRow[];
  notes: string[];
  isRecomputation: boolean;
  recomputedAtUtc: string;
  sql: string | null;
}

export interface CoverageCategory {
  key: string;
  workloads: string[];
  workloadsEnabled: boolean;
  latestUtc: string | null;
  latestKind: 'eventTime' | 'reportDate' | 'observedDate';
  lastActivityDateUtc: string | null;
  sourceStampState: string | null;
  sourceLastSuccessUtc: string | null;
  perUserReplay: 'perUserPossible' | 'sourceWideOnly' | 'notReconstructable';
  error: boolean;
}

export interface UserCoverage {
  categories: CoverageCategory[];
  loadedAtUtc: string;
}

/**
 * The report context a "Troubleshoot this user" link carries into User lookup through router state - never the
 * URL, which would put a person's UPN in browser history and server logs.
 */
export interface UserDiagnosticsContext {
  upn: string;
  report: VerdictReport;
  windowDays?: number;
  from?: string;
  to?: string;
  licenceTypeIds?: number[];
}
