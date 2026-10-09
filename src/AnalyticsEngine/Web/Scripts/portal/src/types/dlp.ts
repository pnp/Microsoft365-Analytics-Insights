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

/**
 * A per-interaction flag as a rate per 10,000 (mirrors DlpGovernanceRate).
 *
 * A flag Microsoft did not report is "not reported", not "clean": such an interaction is in neither
 * count, so the rate is always over `reportedInteractions` and is shown with it.
 */
export interface DlpGovernanceRate {
  /** Numerator: interactions on which the flag was reported true at least once. */
  flaggedInteractions: number;
  /** Denominator: interactions on which the flag was reported at all, true or false. */
  reportedInteractions: number;
  /** Null when nothing reported the flag - unknown, never zero. */
  ratePer10000: number | null;
}

/** The share of the content Copilot used that carried a sensitivity label. */
export interface DlpGovernanceLabelShare {
  labelledResources: number;
  /** Denominator: every resource Copilot used, counted once per interaction. */
  resources: number;
  interactionsWithResources: number;
  /** 0-1; null when Copilot used no resource. */
  share: number | null;
}

/** A model or plugin as Microsoft named it (never translated), and the interactions that used it. */
export interface DlpGovernanceMixRow {
  name: string;
  interactions: number;
  /** Of every interaction in the window, 0-1; null when the window has none. */
  share: number | null;
}

/** The DLP page's governance section (api/Dlp/governance; mirrors DlpGovernanceSummary). */
export interface DlpGovernanceSummary {
  fromUtc: string;
  toUtc: string;
  /** Copilot interactions in the window: the population every figure is drawn from. */
  interactions: number;
  jailbreak: DlpGovernanceRate;
  xpia: DlpGovernanceRate;
  sensitivityLabels: DlpGovernanceLabelShare;
  interactionsWithModel: number;
  models: DlpGovernanceMixRow[];
  interactionsWithPlugin: number;
  plugins: DlpGovernanceMixRow[];
}
