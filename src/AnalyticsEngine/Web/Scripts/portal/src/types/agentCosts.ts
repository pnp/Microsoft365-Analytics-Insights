// Types for the Agent costs report.
//
// These mirror the shapes returned by AgentCostsAPIController. The nullable fields are deliberate:
// the billing APIs routinely report a dimension as absent (an agent invoked no tool, a harness could
// not be identified), and rendering an absent dimension as an empty string would hide that.

/** Whether each import is on, when it last ran, and anything the admin needs to do about it. */
export interface AgentCostAvailability {
  copilotStudioCreditsEnabled: boolean;
  azureCostsEnabled: boolean;
  hasCopilotStudioCreditData: boolean;
  hasPerUserCreditData: boolean;
  /** No administrator is connected, so consumption is not imported (capacity still is). Informational, not a failure. */
  copilotStudioConnectionRequired: boolean;
  hasAzureCostData: boolean;
  copilotStudioCreditsHasRunCleanly: boolean;
  azureCostsHaveRunCleanly: boolean;
  copilotStudioCreditsLastImportUtc: string | null;
  azureCostsLastImportUtc: string | null;
  copilotStudioCreditsLastError: string | null;
  azureCostsLastError: string | null;
  perUserCreditsLastImportUtc: string | null;
  perUserCreditsLastError: string | null;
  capacityLastError: string | null;
  azureDimensionsWithData: string[];
  /** The credit pivots that have data behind them. Same purpose as `azureDimensionsWithData`. */
  creditDimensionsWithData: string[];
  earliestUsageDate: string | null;
  latestUsageDate: string | null;
  messages: string[];
}

/** Azure spend for one billing currency. Never added across currencies. */
export interface AzureCostByCurrency {
  currency: string | null;
  cost: number;
  /**
   * Metered quantity behind the cost, or null when it would be meaningless.
   *
   * Only populated when a single meter/tag combination contributes - quantities of different meters are
   * different units (credits, GB, hours, operations) and summing them gives a nonsense figure. When it is
   * populated it is the Copilot Credit count, which matters because Cowork does not consume the Copilot
   * Studio entitlement the credit figures on this page come from. Otherwise use the per-tag or per-meter
   * breakdown, where the quantity is scoped to one unit.
   */
  quantity: number | null;
  includesEstimates: boolean;
}

/** The tenant's Copilot Credits entitlement as last read. */
export interface CopilotCapacitySnapshot {
  snapshotUtc: string;
  consumptionAsOf: string | null;
  entitled: number | null;
  consumed: number | null;
  consumptionType: string | null;
  allocated: number | null;
  available: number | null;
  payAsYouGoConsumed: number | null;
  status: string | null;
}

export interface AgentCostSummary {
  /** Null only when `withheld`: never shown as zero. */
  billedCredits: number | null;
  nonBilledCredits: number | null;
  distinctAgents: number | null;
  distinctEnvironments: number | null;
  daysWithUsage: number | null;
  /** Busiest single slice. NOT a tenant user total - these counts overlap and cannot be summed. */
  peakDistinctUsersOnASlice: number | null;
  unclassifiedHarnessCredits: number | null;
  /**
   * For a reader without See PII, the fewest people Microsoft must report on a billed line for it to count towards
   * any Copilot Studio figure on the page (the privacy floor, #844). Null for a reader with See PII.
   */
  minPeoplePerSlice: number | null;
  /** Billed lines of fewer people, or with no count, exist in this selection and are left out of every figure. */
  smallSlicesWithheld: boolean;
  /** Every billed line in this selection is such a line: the credit figures and counts are withheld, not zero. */
  withheld: boolean;
  capacity: CopilotCapacitySnapshot | null;
  azureCost: AzureCostByCurrency[];
}

export interface AgentCostDailyPoint {
  date: string;
  billedCredits: number;
  nonBilledCredits: number;
}

export interface AgentCostBreakdownRow {
  /** Null when Microsoft reported no value for the dimension - and on the `withheld` row, which names nothing. */
  key: string | null;
  label: string | null;
  /** Null only on the `withheld` row. */
  billedCredits: number | null;
  nonBilledCredits: number | null;
  activeDays: number | null;
  peakDistinctUsers: number | null;
  /** On the `withheld` row, how many agents, environments, harnesses or features it stands for; 0 otherwise. */
  folded: number;
  /**
   * The one row, last, a reader without See PII is given for the values none of whose billed lines had enough people:
   * no key, name or figures, and their credits are in no other figure on the page.
   */
  withheld: boolean;
}

/**
 * One fully-granular billing row - the deepest view the source data supports.
 *
 * The LLM model, tool invoked, knowledge sources and channel are deliberately absent: a live capture of
 * Microsoft's per-agent credit response carried only the agent name, a non-billable quantity and a user
 * count, so those columns could only ever have rendered as a dash on every row.
 */
export interface AgentCostDetailRow {
  usageDate: string;
  environmentId: string | null;
  environmentName: string | null;
  agentId: string | null;
  agentName: string | null;
  harness: string | null;
  featureName: string | null;
  billedCredits: number;
  nonBilledCredits: number | null;
  distinctUsers: number | null;
}

export interface AgentCostDetailPage {
  rows: AgentCostDetailRow[];
  totalRows: number;
  page: number;
  pageSize: number;
  /** As `AgentCostSummary.minPeoplePerSlice`. */
  minPeoplePerSlice: number | null;
  /** Billed lines of fewer people, or with no count, are not listed, counted or exported. */
  smallSlicesWithheld: boolean;
}

export interface AzureCostBreakdownRow {
  key: string | null;
  label: string | null;
  currency: string | null;
  cost: number;
  quantity: number | null;
  includesEstimates: boolean;
}

export interface AgentCostFilterOption {
  id: string;
  label: string;
}

/** One user's billed Copilot Studio credits over the window, straight from Microsoft. */
export interface AgentCostUserRow {
  /** The resolved users-table key, or null when the identifier could not be matched to a person. */
  userId: number | null;
  /** The identifier the licensing API reported - an Entra object id. Always present. */
  entraObjectId: string | null;
  /** The person's UPN, or null when they are not in the users table. */
  userPrincipalName: string | null;
  billedCredits: number;
  activeDays: number;
}

export interface AgentCostFilterOptions {
  agents: AgentCostFilterOption[];
  environments: AgentCostFilterOption[];
  harnesses: string[];
  features: string[];
}

/** The billing dimensions credits can be pivoted by. Keep in step with `AgentCostDimensions`. */
export type CreditDimension =
  | 'agent'
  | 'environment'
  | 'harness'
  | 'feature';

/** Keep in step with `AzureCostDimensions`. */
export type AzureDimension =
  | 'meter'
  | 'service'
  | 'category'
  | 'resource'
  | 'resourcegroup'
  | 'subscription'
  | 'tag';

/** The filters applied to every credit query on the page. */
export interface AgentCostFilters {
  from: string;
  to: string;
  agentId?: string;
  environmentId?: string;
  harness?: string;
  feature?: string;
  search?: string;
}
