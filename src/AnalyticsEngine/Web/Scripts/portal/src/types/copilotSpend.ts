import type { GlobalFilterEcho } from './globalFilter';
import type { UserFilterEcho } from './userFilter';
import type {
  InvestmentPolicyRow,
  InvestmentServiceRow,
  InvestmentTenantBalanceRow,
  InvestmentTrend,
} from './copilotInvestment';

/**
 * The Copilot spend report, as `api/CopilotSpend` sends it: who consumed what, when, from which source and where in the
 * organisation. Facts only - no prices, value or recommendations.
 *
 * Two sources are kept apart and never added. Copilot Studio billed usage is a daily ledger and sums over the period;
 * Copilot credit balances are each person's latest reading on a date, in a period the source does not identify.
 */
export interface CopilotSpendSummary {
  generatedUtc: string;
  fromUtc: string;
  toUtc: string;
  windowDays: number;
  filtered: boolean;
  userFilter: UserFilterEcho | null;
  globalFilter: GlobalFilterEcho | null;
  studio: SpendStudio;
  balances: SpendBalances;
  organisation: SpendOrganisation;
  peopleCount: number;
  individualDataHidden: boolean;
}

export interface SpendStudio {
  importEnabled: boolean;
  hasData: boolean;
  failureCode: string | null;
  lastImportUtc: string | null;
  lastImportFailed: boolean;
  earliestDate: string | null;
  coversWindow: boolean;
  credits: number;
  people: number;
  /** Of `credits`, charges never matched to a person. Null in a filtered view. */
  unmatchedCredits: number | null;
  days: SpendDay[];
  agents: SpendAgent[];
  breakdownFailureCode: string | null;
}

export interface SpendDay {
  date: string;
  credits: number;
  people: number;
}

export interface SpendAgent {
  agentId: string | null;
  agentName: string | null;
  credits: number;
  people: number;
  days: number;
}

export interface SpendBalances {
  tablesPresent: boolean;
  collectionEnabled: boolean;
  hasData: boolean;
  failureCode: string | null;
  snapshotDate: string | null;
  snapshotBandFrom: string | null;
  firstObservationDate: string | null;
  periodStatus: 'verified' | 'unverified' | 'mixed' | null;
  oldestFreshness: string | null;
  newestFreshness: string | null;
  credits: number;
  observedPeople: number;
  consumingPeople: number;
  partialPeople: number;
  unknownAmountPeople: number;
  otherUnitPeople: number;
  limitExhaustedPeople: number;
  notEntitledPeople: number;
  /** Tenant-wide; null in a filtered view. */
  unresolvedAccounts: number | null;
  unresolvedCredits: number | null;
  sweepsInWindow: number;
  completedSweepsInWindow: number;
  services: InvestmentServiceRow[];
  policies: InvestmentPolicyRow[];
  tenantBalances: InvestmentTenantBalanceRow[];
}

export interface SpendOrganisation {
  dimension: string;
  dimensionName: string | null;
  minGroupSize: number;
  foldedGroups: number;
  hidden: boolean;
  groups: SpendGroupRow[];
}

export interface SpendGroupRow {
  label: string | null;
  emptyKey: 'notSet' | 'otherSmallGroups' | null;
  people: number;
  studioPeople: number;
  studioCredits: number;
  shareOfStudio: number | null;
  balancePeople: number;
  balanceCredits: number;
  shareOfBalances: number | null;
  unknownAmountPeople: number;
}

export interface SpendPersonRow {
  userId: number;
  userPrincipalName: string | null;
  department: string | null;
  jobTitle: string | null;
  company: string | null;
  country: string | null;
  manager: string | null;
  studioCredits: number;
  studioDays: number;
  studioAgents: number;
  /** Null when there is no reading, or none in Copilot credits: absent, not zero. */
  balanceCredits: number | null;
  balanceDate: string | null;
  balancePartial: boolean;
  unknownAmounts: boolean;
  otherUnits: boolean;
  limitExhausted: boolean;
  notEntitled: boolean;
}

export interface SpendPeoplePage {
  total: number;
  rows: SpendPersonRow[];
}

export interface SpendPersonService {
  category: string;
  policyId: string | null;
  consumed: number | null;
  unit: string | null;
  allowed: boolean | null;
  decisionReason: string | null;
  perUserLimit: number | null;
  perUserLimitKind: string | null;
  remaining: number | null;
  lastModified: string | null;
  unevaluatedReason: string | null;
}

export interface SpendPersonDetail {
  person: SpendPersonRow;
  services: SpendPersonService[];
  balances: { date: string; credits: number | null; quality: number; limitExhausted: boolean }[];
  studioAgents: { agentId: string; agentName: string | null; credits: number; activeDays: number }[];
}

export type SpendTrend = InvestmentTrend;

export type SpendPeopleSource = 'all' | 'studio' | 'balances';

export interface SpendPeopleFilters {
  search: string;
  /** Which source a person must appear in: either, billed Studio usage or a credit balance. */
  origin: SpendPeopleSource;
  sortBy: string;
  sortDesc: boolean;
}
