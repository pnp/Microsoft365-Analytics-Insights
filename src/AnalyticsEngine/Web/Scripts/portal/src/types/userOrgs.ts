// Shapes returned by the api/UserOrg endpoints. These mirror the C# models in
// Web/Models/UserOrgs/UserOrgApiModels.cs (kept in sync by hand).

export type UserOrgSource = 'entra' | 'csv';

export type UserOrgImportMode = 'replace' | 'merge';

/**
 * `interrupted` is not a stored status. It is reported when a job is still `running` but its
 * worker has stopped reporting progress - almost always an App Service recycle - because on screen
 * that is otherwise indistinguishable from a very slow import.
 */
export type UserOrgImportStatus =
  | 'pending'
  | 'running'
  | 'succeeded'
  | 'failed'
  | 'cancelled'
  | 'interrupted';

export interface UserOrgImportJob {
  id: number;
  orgTypeId: number;
  mode: UserOrgImportMode;
  status: UserOrgImportStatus;
  fileName: string | null;
  startedBy: string | null;
  queuedUtc: string;
  finishedUtc: string | null;
  rowsTotal: number;
  rowsApplied: number;
  rowsCleared: number;
  rowsUnknownUpn: number;
  rowsInvalid: number;
  errorMessage: string | null;
}

export interface UserOrgType {
  id: number;
  name: string;
  source: UserOrgSource;
  entraAttributeName: string | null;
  isEnabled: boolean;
  assignedUserCount: number;
  distinctValueCount: number;
  createdUtc: string;
  modifiedUtc: string | null;
  lastImport: UserOrgImportJob | null;
}

export interface UserOrgTypeSave {
  name: string;
  source: UserOrgSource;
  entraAttributeName: string | null;
  isEnabled: boolean;
}

export interface UserOrgTestResult {
  succeeded: boolean;
  upn: string | null;
  /** The canonical attribute name, which may differ from what was typed. */
  attributeName: string | null;
  /** The Graph property actually requested in $select. */
  graphProperty: string | null;
  rawValue: string | null;
  /** What would be stored: trimmed and capped at the column width. */
  normalisedValue: string | null;
  wouldTruncate: boolean;
  /** The read worked, but this user has no value for the attribute. */
  hasNoValue: boolean;
  message: string | null;
}

export interface UserOrgCsvPreviewRow {
  lineNumber: number;
  upn: string;
  orgValue: string | null;
  userExists: boolean;
  /** True when the row will clear the user's value rather than set one. */
  clearsValue: boolean;
}

export interface UserOrgCsvProblem {
  lineNumber: number;
  reason: string;
}

export interface UserOrgCsvPreview {
  fileName: string | null;
  delimiter: string;
  headerDetected: boolean;
  upnColumnName: string | null;
  orgColumnName: string | null;
  rows: UserOrgCsvPreviewRow[];
  problems: UserOrgCsvProblem[];
  moreRowsExist: boolean;
}

export interface UserOrgImportQueued {
  jobId: number;
  rowsQueued: number;
  rowsInvalid: number;
}

export interface UserOrgDiscoveredAttribute {
  name: string;
  dataType: string | null;
  isSyncedFromOnPremises: boolean;
}

export interface UserOrgAttributeCatalogue {
  extensionAttributes: string[];
  builtInProperties: string[];
  employeeOrgDataProperties: string[];
  directoryExtensions: UserOrgDiscoveredAttribute[];
  /**
   * Why the directory extension list may be incomplete. Microsoft documents the discovery call as
   * returning nothing at all on tenants with more than 1,000 service principals, so an empty list
   * must never be shown as "this tenant has none".
   */
  discoveryWarning: string | null;
}
