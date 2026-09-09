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
  hasAzureCostData: boolean;
  copilotStudioCreditsHasRunCleanly: boolean;
  azureCostsHaveRunCleanly: boolean;
  copilotStudioCreditsLastImportUtc: string | null;
  azureCostsLastImportUtc: string | null;
  copilotStudioCreditsLastError: string | null;
  azureCostsLastError: string | null;
  earliestUsageDate: string | null;
  latestUsageDate: string | null;
  messages: string[];
}

/** Azure spend for one billing currency. Never added across currencies. */
export interface AzureCostByCurrency {
  currency: string | null;
  cost: number;
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
  billedCredits: number;
  nonBilledCredits: number;
  distinctAgents: number;
  distinctEnvironments: number;
  daysWithUsage: number;
  /** Busiest single slice. NOT a tenant user total - these counts overlap and cannot be summed. */
  peakDistinctUsersOnASlice: number | null;
  unclassifiedHarnessCredits: number;
  capacity: CopilotCapacitySnapshot | null;
  azureCost: AzureCostByCurrency[];
}

export interface AgentCostDailyPoint {
  date: string;
  billedCredits: number;
  nonBilledCredits: number;
}

export interface AgentCostBreakdownRow {
  key: string | null;
  label: string | null;
  billedCredits: number;
  nonBilledCredits: number;
  activeDays: number;
  peakDistinctUsers: number | null;
}

/** One fully-granular billing row - the deepest view the source data supports. */
export interface AgentCostDetailRow {
  usageDate: string;
  environmentId: string | null;
  environmentName: string | null;
  agentId: string | null;
  agentName: string | null;
  harness: string | null;
  featureName: string | null;
  channelId: string | null;
  llmModel: string | null;
  toolInvoked: string | null;
  knowledgeSources: string | null;
  billedCredits: number;
  nonBilledCredits: number | null;
  distinctUsers: number | null;
}

export interface AgentCostDetailPage {
  rows: AgentCostDetailRow[];
  totalRows: number;
  page: number;
  pageSize: number;
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
  /** The identifier the licensing API reported. Not resolved to a name - nothing verifies its format. */
  userId: string;
  billedCredits: number;
  activeDays: number;
}

export interface AgentCostFilterOptions {
  agents: AgentCostFilterOption[];
  environments: AgentCostFilterOption[];
  harnesses: string[];
  features: string[];
  models: string[];
  tools: string[];
  knowledgeSources: string[];
  channels: string[];
}

/** The billing dimensions credits can be pivoted by. Keep in step with `AgentCostDimensions`. */
export type CreditDimension =
  | 'agent'
  | 'environment'
  | 'harness'
  | 'feature'
  | 'model'
  | 'tool'
  | 'knowledge'
  | 'channel';

/** Keep in step with `AzureCostDimensions`. */
export type AzureDimension = 'meter' | 'service' | 'category' | 'resource' | 'resourcegroup' | 'subscription';

/** The filters applied to every credit query on the page. */
export interface AgentCostFilters {
  from: string;
  to: string;
  agentId?: string;
  environmentId?: string;
  harness?: string;
  feature?: string;
  model?: string;
  tool?: string;
  knowledge?: string;
  channel?: string;
  search?: string;
}
