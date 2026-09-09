// Mirrors Web/Models/Dlp/DlpModels.cs (returned by api/Dlp).

/**
 * Whether this deployment can show DLP data, and what is switched off.
 *
 * Two independent switches on purpose. Copilot DLP blocks are carried inside the Copilot
 * interaction records, so that half needs only the Copilot import and NO DLP permission. The
 * tenant-wide half needs the separate DLP.All import and its own `ActivityFeed.ReadDlp` consent -
 * and can never name an agent.
 */
export interface DlpAvailability {
  copilotDlpAvailable: boolean;
  tenantDlpAvailable: boolean;
  available: boolean;
  reasons: string[];
}

/** One named thing (agent, user, policy, label) ranked by how often DLP affected it. */
export interface DlpImpactRow {
  id: string | null;
  name: string | null;
  /** Matches that actually denied the content. */
  blockedCount: number;
  /** Matches that only audited or notified - nothing was withheld. */
  auditedCount: number;
  /** Distinct users affected; null where the row IS a user. */
  usersAffected: number | null;
  totalCount: number;
  /**
   * For an agent row, the individual policies that affected it. Absent on every other kind of row.
   * Nested so "which policies blocked this agent" needs no client-side join.
   */
  policies?: DlpImpactRow[] | null;
}

export interface DlpTrendPoint {
  date: string;
  blockedCount: number;
  auditedCount: number;
}

export interface DlpSummary {
  fromUtc: string;
  toUtc: string;

  copilotBlockedCount: number;
  copilotAuditedCount: number;
  usersImpacted: number;
  agentsImpacted: number;
  policiesInvolved: number;

  topAgents: DlpImpactRow[];
  topUsers: DlpImpactRow[];
  topPolicies: DlpImpactRow[];
  topSensitivityLabels: DlpImpactRow[];
  trend: DlpTrendPoint[];

  /** Tenant-wide DLP.All activity. Never attributable to an agent - shown separately. */
  tenantTopPolicies: DlpImpactRow[];
  tenantBlockedCount: number;
  tenantAuditedCount: number;
}
