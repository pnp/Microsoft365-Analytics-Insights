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
  /** When a worker last picked the job up; null while it is still waiting to start. */
  startedUtc: string | null;
  finishedUtc: string | null;
  /** How many times a worker has picked it up. More than one means it was resumed after a restart. */
  attempts: number;
  rowsTotal: number;
  rowsApplied: number;
  rowsCleared: number;
  rowsUnknownUpn: number;
  rowsInvalid: number;
  /** A stable key for why the job failed or was cancelled; word it from this, not from `errorMessage`. */
  errorCode: UserOrgImportErrorCode | string | null;
  /** The server's English wording - only a fallback for an `errorCode` the portal does not know. */
  errorMessage: string | null;
}

/** Why a finished job did not succeed. Keep in step with `UserOrgImportErrorCodes` on the server. */
export type UserOrgImportErrorCode =
  | 'failed'
  | 'superseded'
  | 'typeChanged'
  | 'clearExceedsConfirmed'
  | 'interruptedRepeatedly';

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
  /**
   * When the values were last brought up to date from their source, or null if never. For an Entra
   * type, the start of the last user import that read the attribute and applied its values; for a CSV
   * type, when the last import was applied. Cleared when the values are discarded because the source
   * changed.
   */
  lastRefreshedUtc: string | null;
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
  /** What is wrong with the row; word it from this. */
  code: UserOrgCsvRowProblemCode | string;
  /** The server's English wording - only a fallback for a code the portal does not know. */
  reason: string;
}

/** Why a row will not be imported. Keep in step with `UserOrgCsvProblemCodes` on the server. */
export type UserOrgCsvRowProblemCode = 'missingUserColumn' | 'userEmptyOrTooLong' | 'notAValidUpn' | 'unknownUser';

/** One row that will not be imported, for the downloadable list of rows to fix. */
export interface UserOrgCsvUnusableRow {
  lineNumber: number;
  /** The user column as it reads in the file, or null when the row has none. */
  upn: string | null;
  orgValue: string | null;
  code: UserOrgCsvRowProblemCode | string;
}

/** Why a whole file cannot be imported. Keep in step with `UserOrgCsvBlockingCodes` on the server. */
export type UserOrgCsvBlockingCode =
  | 'notUtf8'
  | 'excelWorkbook'
  | 'notText'
  | 'unterminatedQuote'
  | 'rowSpansLines'
  | 'chooseColumns'
  | 'oneColumn'
  | 'tooManyRows'
  | 'noRows'
  | 'noUsableRows';

export interface UserOrgCsvBlocking {
  code: UserOrgCsvBlockingCode | string;
  /** The line the problem starts on, where there is one. */
  line: number | null;
  /** For `rowSpansLines`, the line the run-on row ends on. */
  lastLine: number | null;
  /** For `tooManyRows`, the most rows one file may hold. */
  max: number | null;
}

/** Which columns to read, when the admin overrides what the server detected. 0-based. */
export interface UserOrgCsvColumnChoice {
  userColumn?: number;
  valueColumn?: number;
}

/**
 * A parsed and staged upload. The preview IS the upload: importing commits `draftId`, so the file is
 * read once and exactly what was previewed is what is imported.
 */
export interface UserOrgCsvPreview {
  fileName: string | null;
  /** The staged draft to import, or null when the file cannot be imported (`blocking` says why). */
  draftId: number | null;
  blocking: UserOrgCsvBlocking | null;
  delimiter: string;
  headerDetected: boolean;
  /** The header row's column names, or null when the file has no recognisable header. */
  columns: string[] | null;
  /** How many columns the file has, header or not. */
  columnCount: number;
  /** The 0-based column read as the user, or null when it could not be decided. */
  userColumnIndex: number | null;
  /** The 0-based column read as the value, or null when it could not be decided. */
  valueColumnIndex: number | null;
  upnColumnName: string | null;
  orgColumnName: string | null;
  rows: UserOrgCsvPreviewRow[];
  /** The first few unreadable rows. `unusableRows` has all of them, plus the rows naming nobody. */
  problems: UserOrgCsvProblem[];
  moreRowsExist: boolean;
  /** Usable rows in the whole file, not just the sample. */
  totalRows: number;
  /** Rows in the whole file whose UPN matches nobody. */
  unknownUpnCount: number;
  /**
   * How many users could lose their value if this file were imported with Replace. The number that
   * actually matters before a destructive import, and one a ten-row sample cannot reveal.
   */
  wouldClearCount: number;
  /** How many users would lose their value with Merge: people listed with an empty value who have one. */
  mergeWouldClearCount: number;
  /** How many users hold a value for this org type today. */
  currentlyAssignedCount: number;
  /** How many existing users the file gives a value to. */
  matchedUserCount: number;
  /** Rows whose organisation name is too long for the column and will be stored shortened. */
  truncatedValueCount: number;
  /** The longest organisation name that is stored in full. */
  maxValueLength: number;
  /** Every row that will not be imported, in file order - capped at the first 10,000. */
  unusableRows: UserOrgCsvUnusableRow[];
  /** How many rows will not be imported, however many `unusableRows` lists. */
  unusableRowCount: number;
}

export interface UserOrgImportQueued {
  jobId: number;
  rowsQueued: number;
  rowsInvalid: number;
}

/** One organisation and how many users are in it. The name is tenant data, shown as stored. */
export interface UserOrgValueRow {
  id: number;
  name: string;
  memberCount: number;
}

/** One page of an org type's organisations, largest first. */
export interface UserOrgValuePage {
  orgTypeId: number;
  /** The page actually returned, after the server clamped it. 1-based. */
  page: number;
  pageSize: number;
  /** Organisations matching the search, across every page. */
  total: number;
  items: UserOrgValueRow[];
}

/** One user in an organisation. Every field is tenant data, shown as stored. */
export interface UserOrgMember {
  userId: number;
  userPrincipalName: string;
  department: string | null;
  jobTitle: string | null;
  /** `false` for a disabled account; `null` when the import has not recorded it. */
  accountEnabled: boolean | null;
}

/** One page of the users in an organisation, by user principal name. */
export interface UserOrgMemberPage {
  orgTypeId: number;
  valueId: number;
  valueName: string;
  page: number;
  pageSize: number;
  /** Users matching the search, across every page. */
  total: number;
  items: UserOrgMember[];
}

/** What a browse list asks for. Omitted fields take the server's defaults. */
export interface UserOrgBrowseQuery {
  search?: string;
  page?: number;
  pageSize?: number;
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
