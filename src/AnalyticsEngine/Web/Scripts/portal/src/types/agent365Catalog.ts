export interface Agent365CatalogPackage {
  packageId: string;
  displayName: string | null;
  packageType: string | null;
  platform: string | null;
  publisher: string | null;
  manifestId: string | null;
  version: string | null;
  isBlocked: boolean | null;
  lastModifiedUtc: string | null;
  lastUsedUtc: string | null;
  lastUsedDateTimeProvided: boolean;
  knownNeverUsed: boolean;
  activeUsers: number | null;
  totalSessions: number | null;
  totalRunTimeHours: number | null;
  exceptionRate: number | null;
  supportedHosts: string[];
  elements: Array<{ elementType: string | null; elementId: string | null }>;
}

export interface Agent365CatalogResponse {
  importEnabled: boolean;
  lastAttemptUtc: string | null;
  lastAttemptCompletedUtc: string | null;
  lastAttemptSucceeded: boolean | null;
  lastAttemptError: string | null;
  lastSuccessfulImportUtc: string | null;
  totalCount: number;
  offset: number;
  pageSize: number;
  neverUsedOnly: boolean;
  packageCount: number;
  neverUsedCount: number;
  packages: Agent365CatalogPackage[];
}
