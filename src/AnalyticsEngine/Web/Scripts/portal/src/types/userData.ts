// Shapes returned by the api/UserDataLookup endpoints. These mirror the C# models in
// Web/Models/UserDataLookup*.cs (kept in sync by hand).

export interface UserLicense {
  name: string;
  skuId: string | null;
}

export interface UserProfile {
  userId: number;
  userPrincipalName: string;
  mail: string | null;
  azureAdId: string | null;
  accountEnabled: boolean | null;
  lastUpdated: string | null;
  department: string | null;
  jobTitle: string | null;
  companyName: string | null;
  countryOrRegion: string | null;
  officeLocation: string | null;
  usageLocation: string | null;
  stateOrProvince: string | null;
  postalCode: string | null;
  managerUserPrincipalName: string | null;
  licenses: UserLicense[];
}

export interface UserDataCategory {
  /** Stable key used when requesting drill-down detail. */
  key: string;
  /** Human-friendly label. */
  label: string;
  /** One-line description of what the category contains. */
  description: string;
  count: number;
  /** Whether the detail endpoint can return recent rows for this category. */
  supportsDetail: boolean;
  /** A SQL COUNT query the admin can run to reproduce this count themselves. */
  sqlQuery: string;
  /** Display names of the import workloads that feed this category. */
  workloads: string[];
  /** Whether at least one feeding workload is enabled (if false, a 0 count is expected). */
  workloadsEnabled: boolean;
}

/** An import workload (job) and whether it is enabled for this deployment. */
export interface Workload {
  name: string;
  enabled: boolean;
  description: string;
}

export interface UserDataSummary {
  profile: UserProfile;
  categories: UserDataCategory[];
  workloads: Workload[];
}

export interface UserDataDetailRow {
  /** Primary timestamp for the row, if any (ISO string). */
  timestamp: string | null;
  /** Short title / primary descriptor. */
  title: string | null;
  /** Secondary descriptor (operation, recipient, url, etc.). */
  detail: string | null;
}

export interface UserDataDetailResponse {
  category: string;
  label: string;
  totalCount: number;
  returnedCount: number;
  rows: UserDataDetailRow[];
}
