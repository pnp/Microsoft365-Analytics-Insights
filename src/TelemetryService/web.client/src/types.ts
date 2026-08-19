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
}
