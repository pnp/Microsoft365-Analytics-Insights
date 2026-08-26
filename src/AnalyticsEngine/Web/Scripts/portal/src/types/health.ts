// Mirrors Web/Models/Health/HealthModels.cs. The Health page is split into independently-fetched
// sub-sections (see api/healthApi.ts); each has its own endpoint + payload.

export type HealthStatusName = 'Healthy' | 'Degraded' | 'Unhealthy' | 'Unknown' | string;

/** Common to every sub-section payload. `status`/`reasons` are that section's own traffic-light. */
export interface HealthSectionBase {
  status: HealthStatusName;
  reasons: string[];
  loadedAtUtc: string;
}

// --- Shared row shapes ---

export interface ComponentHealthRow {
  component: string | null;
  status: string | null;
  detail: string | null;
  daysToExpiry: number | null;
  lastSeenUtc: string | null;
}

export interface ImportCycleRow {
  jobName: string | null;
  lastCycleUtc: string | null;
  duration: string | null;
}

export interface SectionImportRow {
  sectionName: string | null;
  lastRunUtc: string | null;
  detail: string | null;
  jobName: string | null;
}

export interface HeartbeatRow {
  jobName: string | null;
  lastBeatUtc: string | null;
  lastCycleUtc: string | null;
  lastCycleDurationSeconds: string | null;
}

export interface HourCount {
  hourUtc: string | null;
  count: number;
}

export interface ExceptionTypeRow {
  type: string | null;
  problemId: string | null;
  count: number;
}

// --- Overview (api/Health/summary) ---

/** One entry in the Overview at-a-glance grid. */
export interface SectionStatus {
  key: string;
  label: string;
  status: HealthStatusName;
  reasons: string[];
}

/** Lightweight overview: the overall traffic-light + per-section grid. Skips the heavy SQL scans. */
export interface HealthSummary {
  buildLabel: string | null;
  loadedAtUtc: string;
  /** False when no App Insights connection string is configured - the AI-backed sections are then unavailable. */
  appInsightsConfigured: boolean;
  overallStatus: HealthStatusName;
  overallReasons: string[];
  sections: SectionStatus[];
}

// --- Data overview (api/Health/data) ---

/** Row counts are approximate (sys.dm_db_partition_stats). The 24h/7d volume + freshness are null when their bounded scan didn't complete. */
export interface DataOverviewSection extends HealthSectionBase {
  countsAreApproximate: boolean;
  hitCount: number;
  activityCount: number;
  teamsCount: number;
  sentEmailCount: number;
  callRecordCount: number;
  copilotChatCount: number;
  userCount: number;
  teamsBeingTrackedCount: number;
  databaseSizeMb: number;
  auditEventsLast24h: number | null;
  auditEventsLast7d: number | null;
  hitsLast24h: number | null;
  hitsLast7d: number | null;
  newestHitUtc: string | null;
  newestAuditEventUtc: string | null;
  /** Cheap DMV counts / DB size couldn't be read (e.g. no VIEW DATABASE STATE). */
  countsError: string | null;
  /** The bounded 24h/7d volume + freshness scans failed or timed out (expected on very large tenants). */
  recentVolumeError: string | null;
  /** Only set on a hard failure (e.g. the database is unreachable). */
  dataError: string | null;
}

// --- Import liveness (api/Health/liveness) ---

export interface LivenessSection extends HealthSectionBase {
  appInsightsConfigured: boolean;
  lastCyclePerJob: ImportCycleRow[];
  lastSectionImports: SectionImportRow[];
  lastHeartbeats: HeartbeatRow[];
  /** Web-tracker pageViews seen in App Insights in the last 24h (0 = tracker not sending / not deployed). */
  pageViewsLast24h: number;
  newestPageViewUtc: string | null;
  livenessError: string | null;
}

// --- Exceptions overview (api/Health/exceptions) ---

export interface ExceptionsSection extends HealthSectionBase {
  appInsightsConfigured: boolean;
  exceptionsLast24h: number;
  exceptionsPerHour: HourCount[];
  topExceptionTypes: ExceptionTypeRow[];
  /** Count of last-24h exceptions that look like SQL capacity / read-only failures (message text is not surfaced). */
  sqlCapacityExceptions24h: number;
  exceptionsError: string | null;
}

// --- Component health (api/Health/components) ---

export interface ComponentsSection extends HealthSectionBase {
  appInsightsConfigured: boolean;
  componentHealth: ComponentHealthRow[];
  componentHealthError: string | null;
}

// --- Configuration (api/Health/config) ---

export interface ConfigSection extends HealthSectionBase {
  enabledImports: string[];
  sqlServer: string | null;
  redisHost: string | null;
  serviceBusEndpoint: string | null;
  cognitiveEndpoint: string | null;
  webAppUrl: string | null;
  callsImportEnabled: boolean;
  webhookState: string | null;
  webhookExpiryUtc: string | null;
  webhookDetail: string | null;
  configError: string | null;
  /** Null = couldn't check; true = DB at this build's latest migration; false = migrations pending (DB behind build). */
  schemaUpToDate: boolean | null;
  pendingMigrations: string[];
  schemaError: string | null;
}
