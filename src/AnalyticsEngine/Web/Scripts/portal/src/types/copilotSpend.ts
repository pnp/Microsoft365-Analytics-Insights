import type { GlobalFilterEcho } from './globalFilter';
import type { UserFilterEcho } from './userFilter';
import type {
  InvestmentPolicyRow,
  InvestmentServiceRow,
  InvestmentTenantBalanceRow,
  InvestmentTrend,
  StudioEnvironmentUse,
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
  /** The ledger's latest stored day: how far it reaches, not proof every earlier day was imported. */
  latestDate: string | null;
  /** False when the ledger is off with no history, or unreadable: every Studio figure is then null, never zero. */
  available: boolean;
  credits: number | null;
  people: number | null;
  /** Of `credits`, charges never matched to a person. Null in a filtered view. */
  unmatchedCredits: number | null;
  days: SpendDay[];
  /** Per-person ledger by Power Platform environment: adds up to `credits` with the days. */
  environments: SpendEnvironment[];
  /** The tenant agent ledger by agent: a separate route, never added to `credits`. Null in a filtered view or when not read. */
  agents: SpendAgent[] | null;
  /** The agent ledger's own total; null in a filtered view. */
  agentLedgerCredits: number | null;
  breakdownFailureCode: string | null;
}

/** `recorded`: charges held; `noneRecorded`: within the ledger's stored days, none held; `notReported`: outside them, which is not zero. */ type SpendDayStatus = 'recorded' | 'noneRecorded' | 'notReported';

/** One calendar day of the window. */ interface SpendDay {
  date: string;
  status: SpendDayStatus;
  credits: number | null;
  people: number | null;
}

export interface SpendEnvironment {
  environmentId: string | null;
  environmentName: string | null;
  /** `otherEnvironments` on the row combining environments too small to show to a reader without See PII. */
  emptyKey: 'otherEnvironments' | null;
  folded: number;
  /** A combined row still too small to show figures for: credits and days are null. */
  withheld: boolean;
  credits: number | null;
  people: number;
  days: number | null;
}

export interface SpendAgent {
  agentId: string | null;
  agentName: string | null;
  credits: number;
  days: number;
}

export interface SpendBalances {
  tablesPresent: boolean;
  collectionEnabled: boolean;
  hasData: boolean;
  failureCode: string | null;
  snapshotDate: string | null;
  /** The newest reading predates the selected period: it is the latest reading, not this period's consumption. */
  snapshotBeforeWindow: boolean;
  /** False when no readings could be used: balance figures are then null, never zero. */
  available: boolean;
  snapshotBandFrom: string | null;
  firstObservationDate: string | null;
  periodStatus: 'verified' | 'unverified' | 'mixed' | null;
  oldestFreshness: string | null;
  newestFreshness: string | null;
  credits: number | null;
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
  /** Groups beyond the most the page lists, combined into one row here and listed one by one in the Excel export. */
  moreGroups: number;
  /** Why the people cannot be grouped at all; groups are then empty. */
  unavailableCode: 'directoryUnavailable' | 'dimensionUnknown' | null;
  hidden: boolean;
  groups: SpendGroupRow[];
}

export interface SpendGroupRow {
  label: string | null;
  emptyKey: 'notSet' | 'otherSmallGroups' | 'moreGroups' | null;
  /** How many groups a combined row holds. */
  folded: number;
  /** A combined row with fewer people than the smallest group shown: its figures are null. */
  withheld: boolean;
  people: number;
  /** Null when the source is not available, or the row is withheld: not zero. */
  studioPeople: number | null;
  studioCredits: number | null;
  shareOfStudio: number | null;
  balancePeople: number | null;
  balanceCredits: number | null;
  shareOfBalances: number | null;
  unknownAmountPeople: number | null;
}

export interface SpendPersonRow {
  userId: number;
  userPrincipalName: string | null;
  department: string | null;
  jobTitle: string | null;
  company: string | null;
  country: string | null;
  manager: string | null;
  /** Null when the Studio ledger is not available: unknown, not zero. */
  studioCredits: number | null;
  studioDays: number | null;
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
  studioEnvironments: StudioEnvironmentUse[];
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
