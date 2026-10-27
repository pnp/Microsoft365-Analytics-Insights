import type { GlobalFilterEcho } from './globalFilter';
import type { UserFilterEcho } from './userFilter';

/**
 * The Copilot investment report, as `api/CopilotInvestment` sends it.
 *
 * Money is only ever MODELLED here: a seat price, a credit price and an hourly cost are the
 * administrator's (or the reader's what-if) assumptions, applied to observed activity and observed
 * balances. Codes are stable identifiers the page maps to its own catalog; nothing in this model is a
 * sentence written by the server.
 */

export type InvestmentScenario = 'conservative' | 'base' | 'upside';

export interface InvestmentRange {
  conservative: number | null;
  base: number | null;
  upside: number | null;
}

export interface InvestmentAssumptions {
  currency: string | null;
  seatPricePerMonth: number | null;
  creditPrice: number | null;
  studioCreditPrice: number | null;
  hourlyCost: number | null;
  minutesPerActiveDay: InvestmentRange;
  realisation: InvestmentRange;
  note: string | null;
}

export type AssumptionStatus = 'complete' | 'partial' | 'missing' | 'unavailable';
export type Provenance = 'tenant' | 'scenario' | 'missing';

export interface InvestmentAssumptionsView {
  status: AssumptionStatus;
  scenarioApplied: boolean;
  version: number;
  updatedUtc: string | null;
  note: string | null;
  values: InvestmentAssumptions | null;
  provenance: Record<string, Provenance>;
  missingForValue: string[];
  storageDurable: boolean;
}

export interface AdoptionSource {
  available: boolean;
  auditAvailable: boolean;
  usageReportAvailable: boolean;
  usageReportDate: string | null;
  figuresIncomplete: boolean;
  licensedUsersCapped: boolean;
  holdersNotAnalysed: number;
  auditSignalHolders: number;
  usageReportSignalHolders: number;
  analysisGeneratedUtc: string | null;
}

export interface CreditSource {
  tablesPresent: boolean;
  collectionEnabled: boolean;
  hasData: boolean;
  firstObservationDate: string | null;
  snapshotDate: string | null;
  snapshotBandFrom: string | null;
  periodStatus: 'verified' | 'unverified' | 'mixed' | null;
  observedUsers: number;
  unresolvedObjects: number | null;
  unresolvedCredits: number | null;
  partialUsers: number;
  staleUsers: number;
  oldestFreshness: string | null;
  newestFreshness: string | null;
  sweepsInWindow: number;
  completedSweepsInWindow: number;
  directoryUsers: number | null;
  collectorObserved: number | null;
  collectorFailed: number | null;
  collectorDenied: number | null;
  collectorPending: number | null;
  collectorPhase: string | null;
  collectorLastCompleted: string | null;
  contextObservedAt: string | null;
  failureCode: string | null;
}

export interface StudioSource {
  hasData: boolean;
  importEnabled: boolean;
  lastImportUtc: string | null;
  lastImportFailed: boolean;
  unresolvedCredits: number | null;
  failureCode: string | null;
}

export interface InvestmentSources {
  adoption: AdoptionSource;
  credits: CreditSource;
  studio: StudioSource;
}

export interface InvestmentSeats {
  holders: number;
  analysedHolders: number;
  /** Holders whose use is known only from Microsoft's usage report, for its own period: counted, not valued. */
  otherPeriodHolders: number;
  /** Holders the adoption analysis did not score (its cap): counted, not valued. */
  notAnalysedHolders: number;
  basis: 'licenceHistory' | 'currentAssignments' | 'mixed' | null;
  seatDays: number;
  monthlyRunRate: number | null;
  windowCost: number | null;
  analysedWindowCost: number | null;
  costPerActiveHolderMonthly: number | null;
}

export interface InvestmentStudioSpend {
  users: number;
  credits: number;
  /** Null unless the ledger covers the whole previous period, so a comparison is like for like. */
  priorCredits: number | null;
  priorUsers: number | null;
  holderCredits: number;
  windowValue: number | null;
  holderWindowValue: number | null;
}

export interface InvestmentCreditBalances {
  observedUsers: number;
  consumingUsers: number;
  credits: number;
  holderCredits: number;
  nonHolderCredits: number;
  balanceValue: number | null;
  periodStatus: 'verified' | 'unverified' | 'mixed' | null;
  otherUnitUsers: number;
  unknownAmountUsers: number;
  limitExhaustedUsers: number;
  nearLimitUsers: number;
  notEntitledUsers: number;
}

export interface InvestmentSpend {
  currency: string | null;
  seats: InvestmentSeats;
  studio: InvestmentStudioSpend;
  credits: InvestmentCreditBalances;
  modelledWindowCost: number | null;
  modelledMonthlyCost: number | null;
  /** The two parts of `modelledMonthlyCost`: the analysed holders' seats and their own billed Studio credits. */
  monthlySeatCost: number | null;
  monthlyStudioCost: number | null;
}

/** Seat holders by active days a month. The seat-only break-even is itself an edge, so each bucket is wholly on one side. */
export interface InvestmentDistributionBucket {
  key: 'none' | 'range';
  minDaysPerMonth: number;
  maxDaysPerMonth: number | null;
  holders: number;
  /** Null when there is no break-even (assumptions missing). */
  belowBreakEven: boolean | null;
}

export interface InvestmentEvidence {
  holders: number;
  analysedHolders: number;
  activeHolders: number;
  activeShare: number | null;
  idleHolders: number;
  idleTooNewToJudge: number;
  totalActiveDays: number;
  medianActiveDays: number | null;
  p25ActiveDays: number | null;
  p75ActiveDays: number | null;
  activeDaysPerActiveHolderMonthly: number | null;
  habitShare: number | null;
  multiAppHolders: number;
  agentHolders: number;
  coworkHolders: number;
  bands: { band: string; holders: number }[];
  unlicensedActiveUsers: number;
  unlicensedActiveDays: number;
  recommendedCandidates: number;
  distribution: InvestmentDistributionBucket[];
}

export interface InvestmentScenarioResult {
  scenario: InvestmentScenario;
  minutesPerActiveDay: number;
  realisation: number;
  assistedHours: number;
  realisedHours: number;
  value: number;
  monthlyValue: number;
  roiMultiple: number | null;
  net: number | null;
  holdersAtOrAboveBreakEven: number;
}

export interface InvestmentValue {
  available: boolean;
  /** Why value is not modelled; null when it is. */
  unavailableReason: 'assumptions' | 'noMatchedHolders' | null;
  missing: string[];
  scenarios: InvestmentScenarioResult[];
  valuePerActiveHolderMonthly: number | null;
}

export interface InvestmentBreakEven {
  available: boolean;
  minutesPerActiveDay: number | null;
  marginOfSafety: number | null;
  activeDaysPerMonthPerSeat: number | null;
  minutesPerWeekdayPerSeat: number | null;
  holdersAtOrAbove: number;
  holdersBelow: number;
  holdersNoEvidence: number;
  shareAtOrAbove: number | null;
}

export interface InvestmentSensitivity {
  grid: { minutesScenario: InvestmentScenario; realisationScenario: InvestmentScenario; roiMultiple: number | null; value: number }[];
  roiBaseHourlyMinus20: number | null;
  roiBaseHourlyPlus20: number | null;
  /** A stress floor: each latest balance as an extra month of spend on top of everything else. */
  roiBaseIfBalancesMonthly: number | null;
  roiBaseAfterReclaim: number | null;
}

/** What it would take to break even, and what the seat levers change - all a month, on the base assumptions. */
export interface InvestmentPath {
  available: boolean;
  monthlyCost: number | null;
  monthlyValueBase: number | null;
  /** Positive is a shortfall, negative a surplus. */
  monthlyGap: number | null;
  valuePerActiveDay: number | null;
  extraActiveDaysPerMonth: number | null;
  extraDaysPerActiveHolder: number | null;
  activeHolders: number;
  minutesNeeded: number | null;
  minutesAssumed: number | null;
  reclaimable: number;
  reclaimMonthly: number | null;
  roiAfterReclaim: number | null;
  evidencedWithoutSeat: number;
  reassignable: number;
  netNewSeats: number;
  netNewMonthly: number | null;
}

/** Weekly active seat holders over recent complete weeks. Tenant-wide, so only for an unfiltered view. */
export interface InvestmentTrajectory {
  available: boolean;
  unavailableReason: 'filtered' | 'noAudit' | 'noData' | null;
  seatHolders: number;
  weeks: { weekStart: string; activeHolders: number | null; share: number | null }[];
}

export interface InvestmentCohort {
  band: string;
  holders: number;
  medianActiveDays: number | null;
  activeDays: number;
  shareOfHolders: number | null;
  shareOfActiveDays: number | null;
  cost: number | null;
  valueBase: number | null;
  roiBase: number | null;
  credits: number;
  studioCredits: number;
}

export interface InvestmentGroupRow {
  label: string | null;
  emptyKey: 'notSet' | 'otherSmallGroups' | null;
  holders: number;
  activeHolders: number;
  idleHolders: number;
  activeShare: number | null;
  activeDaysPerHolderMonthly: number | null;
  cost: number | null;
  valueBase: number | null;
  roiBase: number | null;
  credits: number;
  creditConsumers: number;
  studioCredits: number;
  unlicensedActiveUsers: number;
}

export interface InvestmentOrganisation {
  dimension: string;
  dimensionName: string | null;
  groups: InvestmentGroupRow[];
  foldedGroups: number;
  minGroupSize: number;
  hidden: boolean;
}

export interface InvestmentDistribution {
  population: number;
  topShare: number;
  topFraction: number;
  gini: number;
  lorenz: number[];
}

export interface InvestmentConcentration {
  activeDays: InvestmentDistribution | null;
  credits: InvestmentDistribution | null;
  /** Split at the median balance and at `quadrantDaysPerMonth` active days a month - not the high-spend flag's thresholds. */
  quadrants: { key: string; people: number; credits: number }[];
  quadrantMedianCredits: number | null;
  quadrantDaysPerMonth: number;
}

export interface InvestmentMetered {
  people: number;
  activeUsers: number;
  activeDays: number;
  studioUsers: number;
  studioCredits: number;
  studioWindowValue: number | null;
  creditUsers: number;
  credits: number;
  creditBalanceValue: number | null;
  studioAboveSeatPrice: number | null;
  balanceAboveSeatPriceIfMonthly: number | null;
  evidencedCandidates: number | null;
  recommendedCandidates: number;
}

export interface InvestmentServiceRow {
  category: string;
  unit: string | null;
  consumers: number;
  quantity: number;
  unknownAmountUsers: number;
  limitExhaustedUsers: number;
  notEntitledUsers: number;
  unevaluatedUsers: number;
  policies: string[];
}

export interface InvestmentQuantity {
  kind: 'limited' | 'unlimited' | 'other';
  value: number | null;
  unit: string | null;
}

export interface InvestmentPolicyRow {
  id: string;
  displayName: string | null;
  type: 'allUsers' | 'selectedGroups' | 'other';
  status: string | null;
  billingMethods: string | null;
  periodLimit: InvestmentQuantity | null;
  perUserLimit: InvestmentQuantity | null;
  services: { serviceId: string; state: string }[];
  assignedGroups: number | null;
  modifiedAt: string | null;
}

export interface InvestmentTenantBalanceRow {
  billingMethod: string;
  purchased: number | null;
  consumed: number | null;
  remaining: number | null;
  unit: string | null;
  lastModified: string | null;
}

export interface InvestmentAction {
  code: string;
  people: number;
  monthlyAmount: number | null;
  credits: number | null;
  roiBaseAfter: number | null;
  metric: number | null;
  signal: string | null;
  /** The person flag carrying exactly this action's membership: the drill-down lists the same people the card counts. */
  flag: string | null;
  /** What `monthlyAmount` is - amounts of different kinds are never ranked against each other. */
  effect: string | null;
  assumesMonthlyBalances: boolean;
}

export interface InvestmentFinding {
  code: string;
  tone: 'good' | 'neutral' | 'warning' | 'critical' | 'opportunity';
  values: Record<string, number | null>;
}

export type TrustArea = 'evidence' | 'cost' | 'assumptions' | 'balances';

export interface InvestmentTrustReason {
  code: string;
  impact: 'low' | 'medium' | 'info';
  area: TrustArea;
  values: Record<string, number | null>;
}

export interface InvestmentTrust {
  /** The lowest of the areas that bear on the return (balances stay outside it). */
  level: 'high' | 'medium' | 'low';
  areas: { area: TrustArea; level: 'high' | 'medium' | 'low' }[];
  reasons: InvestmentTrustReason[];
}

export interface CopilotInvestmentSummary {
  generatedUtc: string;
  fromUtc: string;
  toUtc: string;
  windowDays: number;
  weekdays: number;
  monthsInWindow: number;
  filtered: boolean;
  userFilter: UserFilterEcho | null;
  globalFilter: GlobalFilterEcho | null;
  assumptions: InvestmentAssumptionsView;
  sources: InvestmentSources;
  investment: InvestmentSpend;
  evidence: InvestmentEvidence;
  value: InvestmentValue;
  breakEven: InvestmentBreakEven;
  sensitivity: InvestmentSensitivity;
  path: InvestmentPath;
  trajectory: InvestmentTrajectory;
  cohorts: InvestmentCohort[];
  organisation: InvestmentOrganisation;
  concentration: InvestmentConcentration;
  metered: InvestmentMetered;
  services: InvestmentServiceRow[];
  policies: InvestmentPolicyRow[];
  tenantBalances: InvestmentTenantBalanceRow[];
  actions: InvestmentAction[];
  findings: InvestmentFinding[];
  trust: InvestmentTrust;
  individualDataHidden: boolean;
}

export interface InvestmentPersonRow {
  userId: number;
  userPrincipalName: string | null;
  department: string | null;
  jobTitle: string | null;
  country: string | null;
  company: string | null;
  manager: string | null;
  licensed: boolean;
  seatLicences: string | null;
  seatDays: number | null;
  seatBasis: string | null;
  band: string | null;
  adoptionScore: number | null;
  signalSource: string | null;
  activeDays: number;
  activeDaysPerMonth: number;
  habitShare: number | null;
  appsUsed: number;
  agentsUsed: number;
  coworkInteractions: number;
  interactions: number;
  lastActivityUtc: string | null;
  tooNewToJudge: boolean;
  reclaimEligibility: string | null;
  accountEnabled: boolean | null;
  recommendedForLicence: boolean;
  credits: number | null;
  creditsValue: number | null;
  creditObservationDate: string | null;
  studioCredits: number;
  studioCost: number | null;
  seatCost: number | null;
  cost: number | null;
  hoursBase: number | null;
  valueConservative: number | null;
  valueBase: number | null;
  valueUpside: number | null;
  valueToCostBase: number | null;
  /** Active days a month this person's own cost (seat plus their Studio credits) needs on the base assumptions. */
  breakEvenDaysPerMonth: number | null;
  breakEvenGapDaysPerMonth: number | null;
  signal: string;
  flags: string[];
}

export interface InvestmentPeoplePage {
  total: number;
  rows: InvestmentPersonRow[];
  currency: string | null;
  valueAvailable: boolean;
}

export interface InvestmentPersonDetail {
  person: InvestmentPersonRow;
  currency: string | null;
  activityAssessed: boolean;
  apps: { app: string; turns: number; days: number }[];
  weeks: { weekStart: string; activeDays: number; turns: number }[];
  agentTurns: number;
  coworkTurns: number;
  totalTurns: number;
  activeWeeks: number;
  promptsAssessed: boolean;
  prompts: number;
  responses: number;
  promptApps: { app: string; turns: number; days: number }[];
  promptCategories: { categoryId: string; humanMode: string | null; prompts: number }[];
  creditServices: {
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
  }[];
  balances: { date: string; credits: number | null; quality: number; limitExhausted: boolean }[];
  studioAgents: { agentId: string; agentName: string | null; credits: number; activeDays: number }[];
  evidence: string[];
}

export interface InvestmentTrendPoint {
  date: string;
  observedUsers: number;
  consumingUsers: number;
  credits: number | null;
  limitExhaustedUsers: number;
}

export interface InvestmentTrend {
  points: InvestmentTrendPoint[];
  snapshotDate: string | null;
  failureCode: string | null;
}

export interface CopilotInvestmentAvailability {
  adoptionAvailable: boolean;
  studioImportEnabled: boolean;
  creditCollectionEnabled: boolean;
  notes: string[];
}

/** A reader's own what-if figures. Kept in the browser and sent with each request; never saved. */
export interface InvestmentScenarioOverrides {
  currency?: string;
  seatPrice?: number;
  creditPrice?: number;
  studioCreditPrice?: number;
  hourlyCost?: number;
  minutesLow?: number;
  minutesBase?: number;
  minutesHigh?: number;
  realisationLow?: number;
  realisationBase?: number;
  realisationHigh?: number;
}

export interface InvestmentPeopleFilters {
  search: string;
  signals: string[];
  flag: string | null;
  seat: 'all' | 'licensed' | 'unlicensed';
  sortBy: string;
  sortDesc: boolean;
}

export interface InvestmentAssumptionsChange {
  version: number;
  changedBy: string | null;
  changedUtc: string;
  changes: { field: string; oldValue: string | null; newValue: string | null }[];
}
