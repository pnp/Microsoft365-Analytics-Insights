// Mirrors Web/Models/HealthDashboard.cs (returned by api/Health).

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

export type HealthStatusName = 'Healthy' | 'Degraded' | 'Unhealthy' | 'Unknown' | string;

/** "Is it working?" health overview. Every card is best-effort; a *Error field is set if that card failed. */
export interface HealthDashboard {
  buildLabel: string | null;
  loadedAtUtc: string;
  /** False when no App Insights connection string is configured - the AI-backed cards are then unavailable. */
  appInsightsConfigured: boolean;

  /** Single traffic-light rolled up from every card below. */
  overallStatus: HealthStatusName;
  overallReasons: string[];

  componentHealth: ComponentHealthRow[];
  componentHealthError: string | null;

  lastCyclePerJob: ImportCycleRow[];
  lastSectionImports: SectionImportRow[];
  lastHeartbeats: HeartbeatRow[];
  /** Web-tracker pageViews seen in App Insights in the last 24h (0 = tracker not sending / not deployed). */
  pageViewsLast24h: number;
  newestPageViewUtc: string | null;
  livenessError: string | null;

  exceptionsLast24h: number;
  exceptionsPerHour: HourCount[];
  topExceptionTypes: ExceptionTypeRow[];
  /** Count of last-24h exceptions that look like SQL capacity / read-only failures (message text is not surfaced). */
  sqlCapacityExceptions24h: number;
  exceptionsError: string | null;

  /** Row counts are approximate (sys.dm_db_partition_stats) to avoid COUNT(*) scans on huge tenants. */
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
  auditEventsLast24h: number;
  auditEventsLast7d: number;
  hitsLast24h: number;
  hitsLast7d: number;
  newestHitUtc: string | null;
  newestAuditEventUtc: string | null;
  dataError: string | null;

  /** Null = couldn't check; true = DB at this build's latest migration; false = migrations pending (DB behind build). */
  schemaUpToDate: boolean | null;
  pendingMigrations: string[];
  schemaError: string | null;

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
}
