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

/** "Is it working?" health overview. Every card is best-effort; a *Error field is set if that card failed. */
export interface HealthDashboard {
  buildLabel: string | null;
  loadedAtUtc: string;
  /** False when no App Insights connection string is configured - the AI-backed cards are then unavailable. */
  appInsightsConfigured: boolean;

  componentHealth: ComponentHealthRow[];
  componentHealthError: string | null;

  lastCyclePerJob: ImportCycleRow[];
  lastSectionImports: SectionImportRow[];
  lastHeartbeats: HeartbeatRow[];
  livenessError: string | null;

  exceptionsLast24h: number;
  exceptionsPerHour: HourCount[];
  topExceptionTypes: ExceptionTypeRow[];
  exceptionsError: string | null;

  hitCount: number;
  activityCount: number;
  teamsCount: number;
  teamsBeingTrackedCount: number;
  newestHitUtc: string | null;
  newestAuditEventUtc: string | null;
  dataError: string | null;
}
