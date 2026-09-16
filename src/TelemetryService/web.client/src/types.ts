// Shapes returned by /api/Telemetry/stats and /api/Telemetry/clients.
// Kept in one place so the tabs share a single source of truth.

export interface TableTotal {
    tableName: string;
    schemaName: string | null;
    displayName: string;
    rows: number;
    totalSpaceMB: number;
    clientCount: number;
}

export interface SchemaTotal {
    schemaName: string;
    rows: number;
    totalSpaceMB: number;
    tableCount: number;
}

export interface VersionAdoption {
    buildVersionLabel: string;
    clientCount: number;
    lastSeen: string | null;
}

export interface FeatureAdoption {
    name: string;
    enabledCount: number;
    disabledCount: number;
    reportingClients: number;
}

export interface FreshnessBuckets {
    last24Hours: number;
    last7Days: number;
    last30Days: number;
    stale: number;
}

export interface SizeDistribution {
    avgRowsPerClient: number;
    medianRowsPerClient: number;
    maxRowsPerClient: number;
    avgSpaceMBPerClient: number;
    medianSpaceMBPerClient: number;
    maxSpaceMBPerClient: number;
    avgTablesPerClient: number;
}

export interface SkuPopularity {
    skuPartNumber: string;
    clientCount: number;
    assignedUsers: number;
}

export interface CoverageStatusTotal {
    workload: string;
    status: string;
    clientCount: number;
}

/**
 * Cross-client Copilot / licence adoption roll-up.
 *
 * Counts are bucketed by the reporting client before they are sent, so sums and medians here are
 * approximate by design. Every figure is over `clientsReporting`, never over all clients.
 */
export interface AdoptionInsights {
    clientsReporting: number;
    clientsSuppressed: number;
    clientsWithCopilotFigures: number;
    totalLicensedUsers: number;
    totalActiveUsers: number;
    medianLicensedUsersPerClient: number;
    medianActiveUsersPerClient: number;
    medianAdoptionRatePct: number;
    lowerQuartileAdoptionRatePct: number;
    upperQuartileAdoptionRatePct: number;
    medianHabitRatePct: number;
    clientsWithCustomAgents: number;
    freshness: FreshnessBuckets;
    dataSources: FeatureAdoption[];
    skus: SkuPopularity[];
    coverage: CoverageStatusTotal[];
}

export interface DashboardStats {
    clientCount: number;
    totalRows: number;
    totalSpaceMB: number;
    lastUpdated: string | null;
    distinctTableCount: number;
    aiDataPointsTotal: number;
    clientsReportingAi: number;
    tableTotals: TableTotal[];
    schemaTotals: SchemaTotal[];
    versions: VersionAdoption[];
    importFeatures: FeatureAdoption[];
    freshness: FreshnessBuckets;
    sizeDistribution: SizeDistribution;
    adoption: AdoptionInsights;
}

export interface ClientSummary {
    anonClientId: string;
    generated: string | null;
    buildVersionLabel: string | null;
    configuredImportsEnabledDescription: string | null;
    configuredSolutionsEnabledDescription: string | null;
    dataPointsFromAITotal: number | null;
    rows: number;
    totalSpaceMB: number;
    tableCount: number;
    enabledImports: string[];
    /** Maintainer-entered, never reported by the client. Null when nobody has identified it. */
    annotationDisplayName: string | null;
    annotationNotes: string | null;
    adoptionGeneratedUtc: string | null;
    adoptionSuppressed: boolean;
    copilotLicensedUsers: number | null;
    copilotActiveUsers: number | null;
    copilotAdoptionRatePct: number | null;
}

/** What a maintainer can set when identifying a client that asked to be recognised. */
export interface ClientAnnotationUpdate {
    displayName: string | null;
    notes: string | null;
}
