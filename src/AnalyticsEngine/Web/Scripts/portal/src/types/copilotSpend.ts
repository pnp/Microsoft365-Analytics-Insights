import type { AdoptionResourceTypeRow } from './copilotAdoption';
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
  /** How the period's Studio credits are spread over the people who consumed them; null when not available. */
  distribution?: SpendDistribution | null;
}

/** `recorded`: charges held; `noneRecorded`: within the ledger's stored days, none held; `notReported`: outside them, which is not zero. */
export type SpendDayStatus = 'recorded' | 'noneRecorded' | 'notReported';

/** One calendar day of the window. */
export interface SpendDay {
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
  /** The combined row, which never carries figures: credits and days are null, and people only when one environment is combined. */
  withheld: boolean;
  credits: number | null;
  people: number | null;
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
  /** How the latest readings are spread over the people with consumed credits; null when not available. */
  distribution?: SpendDistribution | null;
}

/**
 * How one source's credits are spread over the people who consumed them. A reader without See PII gets an anonymised
 * copy: no named people, no single person's minimum or maximum, bands merged to at least `minPeople` people, and every
 * figure withheld when fewer than `minPeople` people consumed anything.
 */
export interface SpendDistribution {
  consumers: number;
  minPeople: number;
  /** Balances only: people in view whose latest reading is zero. Null for the Studio ledger, which records only charges. */
  zeroPeople: number | null;
  total: number | null;
  median: number | null;
  mean: number | null;
  /** Nearest-rank: nine in ten consumers are at or below it. */
  p90: number | null;
  min: number | null;
  max: number | null;
  /** 0 when everyone consumed the same; towards 1 when a few consumed nearly everything. */
  gini: number | null;
  /** Share of the total consumed by the tenth (fifth) of consumers with the largest amounts. */
  top10Share: number | null;
  top20Share: number | null;
  /** The fewest people, largest first, who make up at least half the total. Null when that is below `minPeople`. */
  peopleForHalf: number | null;
  halfFromFewerThanMin: boolean;
  /** Largest consumers first, from (0, 0) to (1, 1). */
  pareto: SpendParetoPoint[];
  /** Half-open credit bands [from, to) on a 1-2-5 scale; empty bands keep their place. */
  bands: SpendBand[];
  /** See PII only: the largest consumers, largest first. */
  top: SpendRankedPerson[];
  /** See PII only: the smallest consumers above zero, smallest first. */
  lowest: SpendRankedPerson[];
  withheld: boolean;
}

export interface SpendParetoPoint {
  people: number;
  peopleShare: number;
  creditShare: number;
}

export interface SpendBand {
  from: number;
  to: number;
  people: number;
  /** Null in the anonymised copy. */
  credits: number | null;
}

export interface SpendRankedPerson {
  userId: number;
  userPrincipalName: string | null;
  department: string | null;
  credits: number;
  share: number;
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
  /** Where the person stands among everyone who consumed from each source in the view. */
  ranks?: SpendPersonRanks | null;
  services: SpendPersonService[];
  balances: { date: string; credits: number | null; quality: number; limitExhausted: boolean }[];
  studioEnvironments: StudioEnvironmentUse[];
  /** The person's billed Studio usage per day of the window; null when the ledger is not available or could not be read. */
  studioDays?: SpendDay[] | null;
  /** What the person did with Copilot in the period: metadata only, the product never stores what anyone typed. */
  usage?: SpendPersonUsage | null;
  usageFailureCode?: string | null;
}

/** 1 is the largest consumer and ties share a rank. Null when the person consumed nothing from that source. */
export interface SpendPersonRanks {
  studioRank: number | null;
  studioConsumers: number | null;
  studioShare: number | null;
  balanceRank: number | null;
  balanceConsumers: number | null;
  balanceShare: number | null;
}

/**
 * Each `...Assessed` flag says whether that part could be read at all: false means unknown, so a zero beside it must not
 * be shown as "none".
 */
export interface SpendPersonUsage {
  turnsAssessed: boolean;
  turns: number;
  noAgentTurns: number;
  agentTurns: number;
  coworkTurns: number;
  makerTestTurns: number;
  activeDays: number;
  /** The importer's estimate for priced agent turns: never a bill, and never added to either source. */
  estimateCredits: number | null;
  pricedTurns: number;
  days: SpendUsageDay[];
  apps: SpendAppUse[];
  agents: SpendPersonAgent[];
  promptsAssessed: boolean;
  prompts: SpendPromptTotals | null;
  promptApps: SpendAppUse[];
  categoriesAssessed: boolean;
  categories: SpendPersonCategory[];
  notClassifiedPrompts: number;
  phrasesAssessed: boolean;
  phrases: SpendPhrase[];
  /** Newest first. */
  recent: SpendRecentPrompt[];
  resourcesAssessed: boolean;
  resourceTypes: AdoptionResourceTypeRow[];
}

export interface SpendUsageDay {
  date: string;
  turns: number;
  agentTurns: number;
}

export interface SpendAppUse {
  app: string;
  count: number;
  days: number;
}

export interface SpendPersonAgent {
  agentId: string | null;
  name: string | null;
  /** `customEngine`, `declarative` or `unknown`. */
  kindKey: string;
  /** `customerBuilt`, `microsoft` or `unknown`. */
  originKey: string;
  cowork: boolean;
  turns: number;
  testTurns: number;
  estimateCredits: number | null;
  pricedTurns: number;
  days: number;
  lastDate: string | null;
}

export interface SpendPromptTotals {
  prompts: number;
  conversations: number;
  days: number;
  averageWords: number | null;
  withAttachments: number;
  withLinks: number;
  withMentions: number;
  withContext: number;
  firstUtc: string | null;
  lastUtc: string | null;
}

export interface SpendPersonCategory {
  categoryId: string;
  name: string | null;
  nameKey: string | null;
  humanMode: 'directing' | 'supervising' | null;
  prompts: number;
  lastUtc: string | null;
}

export interface SpendPhrase {
  phrase: string;
  prompts: number;
  lastUtc: string | null;
}

/** One request's metadata. What was typed is never stored, so it is never shown. */
export interface SpendRecentPrompt {
  createdUtc: string;
  appClass: string | null;
  words: number;
  attachments: number;
  links: number;
  mentions: number;
  context: number;
  categoryId: string | null;
  categoryName: string | null;
  categoryNameKey: string | null;
  phrases: string[];
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
