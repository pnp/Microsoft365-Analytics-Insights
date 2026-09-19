// Mirrors Common/Entities/CopilotAdoption/CopilotAdoptionModels.cs (returned by api/CopilotAdoption).
//
// The chart shapes (AdoptionSeries / AdoptionCategory) are deliberately identical to the Reports
// area's ReportSeries / ReportCategory so the existing TimeSeriesChart and CategoryBarChart
// components render them with no new charting code.

import type { ReportCategory, ReportSeries } from './reports';

/** Which parts of the adoption tool this deployment can show. */
export interface CopilotAdoptionAvailability {
  available: boolean;
  copilotAuditImportEnabled: boolean;
  copilotUsageReportImportEnabled: boolean;
  userMetadataImportEnabled: boolean;
  m365UsageReportImportEnabled: boolean;
  messages: string[];
}

/** A licence type and whether the tool counted it as a Microsoft 365 Copilot seat. */
export interface LicenceTypeClassification {
  id: number;
  name: string;
  skuPartNumber: string;
  assignedUsers: number;
  purchasedUnits: number | null;
  unassignedUnits: number | null;
  assignedIdleUsers: number;
  purchasedUnitsRefreshedUtc: string | null;
  isCopilotSeat: boolean;
}

/**
 * How embedded Copilot is in a licensed user's working week. Numeric values match the C#
 * AdoptionBand enum, worst first, so a distribution reads left to right as a maturity curve.
 */
export enum AdoptionBand {
  NeverUsed = 0,
  Dormant = 1,
  Trialling = 2,
  Developing = 3,
  Established = 4,
  Champion = 5,
}

/**
 * What a Copilot audit record's `AccessedResources[].Type` value actually describes. Numeric values
 * match the C# CopilotResourceTypeKind enum.
 *
 * Microsoft publishes no enumeration for that field - the Purview docs describe it as carrying
 * "values like the filetype extension (pptx, docx, etc.) or ... the type of resource (for
 * non-SharePoint resources)" - so a value this version does not recognise is Unclassified rather
 * than being folded into one of the other buckets. See issue #468.
 */
export enum CopilotResourceTypeKind {
  Unclassified = 0,
  TenantContent = 1,
  UsageRole = 2,
  ExternalGrounding = 3,
}

/** One row of the "what Copilot referenced" breakdown: a raw audit Type value and what it means. */
export interface AdoptionResourceTypeRow {
  label: string;
  value: number;
  kind: CopilotResourceTypeKind;
}

/** Which imports supplied the data, so no headline number is quoted without its caveats. */
export interface AdoptionDataSources {
  auditAvailable: boolean;
  copilotUsageReportAvailable: boolean;
  coworkUsageReportAvailable: boolean;
  m365UsageReportsAvailable: boolean;
  userMetadataAvailable: boolean;
  copilotUsageReportDate: string | null;
  copilotUsageReportPeriodDays: number;
  coworkUsageReportDate: string | null;
  coworkUsageReportPeriodDays: number;
  m365UsageReportDate: string | null;
  copilotUsageReportObfuscated: boolean;
}

/** Adoption for one slice of the organisation (a department, a country). */
export interface AdoptionSegmentRow {
  segment: string;
  licensedUsers: number;
  activeUsers: number;
  habitualUsers: number;
  neverUsedUsers: number;
  adoptionRatePct: number;
  averageAdoptionScore: number;
}

/**
 * Adoption for one email domain - one of the organisations sharing this tenant.
 *
 * Richer than a plain segment because a domain is usually a whole company rather than a function
 * within one. A domain with idle seats AND unlicensed Copilot Chat use is a seat-allocation problem
 * inside one business; a domain with strong adoption and a queue of licence candidates is a
 * business case. Neither is visible when the same people are split across departments that span
 * every company in the tenant.
 */
export interface AdoptionDomainRow extends AdoptionSegmentRow {
  /** Seats in this domain that look reclaimable, on the same confidence tiering as the headline. */
  reclaimableSeats: number;
  /** Audit interactions per licensed user, normalised to a month. Idle seats are in the denominator. */
  interactionsPerLicensedUser: number;
  /** People here using Copilot Chat in the window with no seat assigned. */
  unlicensedActiveUsers: number;
  /** People here the licence-opportunity ranking recommends buying a seat for. */
  recommendedForLicence: number;
  /** Seat holders here scored as prime Cowork candidates. */
  coworkPrimeCandidates: number;
  /**
   * True when this domain is external guests rather than members of the tenant - a partner being
   * collaborated with, not a part of the business that can be sent on a training course.
   */
  external: boolean;
}

/** Every threshold and weight the adoption maths used, echoed back so a figure can be traced to its rule. */
export interface CopilotAdoptionOptions {
  guidanceCatalogueVersion?: string;
  windowDays: number;
  historyDays: number;
  workingDaysPerWeek: number;
  frequencyTargetRatio: number;
  depthTargetInteractionsPerActiveDay: number;
  depthMinActiveDays: number;
  breadthTargetApps: number;
  frequencyWeight: number;
  depthWeight: number;
  breadthWeight: number;
  championScore: number;
  establishedScore: number;
  developingScore: number;

  habitBucketNormalisationDays: number;
  habitModerateMinDays: number;
  habitFrequentMinDays: number;
  habitDailyMinDays: number;

  agentReviewInactiveDays: number;
  agentRetireInactiveDays: number;
  agentNewDays: number;
  reclaimGraceDays: number;
  activationWindowDays: number;
  agentMinUsers: number;
  agentHistoryDays: number;

  opportunityUnlicensedCopilotWeight: number;
  opportunityCollaborationWeight: number;
  opportunityEmailWeight: number;
  opportunityDocumentWeight: number;
  opportunityCopilotTarget: number;
  opportunityCopilotTargetBasisDays: number;
  opportunityCollaborationTarget: number;
  opportunityEmailTarget: number;
  opportunityDocumentTarget: number;
  opportunityRecommendScore: number;
  opportunityProvenDemandMinActiveDays: number;

  coworkCollaborationWeight: number;
  coworkMeetingWeight: number;
  coworkEmailWeight: number;
  coworkDocumentWeight: number;
  coworkCollaborationTarget: number;
  coworkMeetingTarget: number;
  coworkEmailTarget: number;
  coworkDocumentTarget: number;
  coworkLoadMinScore: number;
  coworkFluencyMinScore: number;
  coworkRegularMinActiveDays: number;
  coworkAgentFamiliarityUplift: number;

  coworkMinutesSavedPerMeeting: number;
  coworkMinutesSavedPerMailThread: number;
  coworkMinutesSavedPerDocument: number;
  coworkEstimateLowerBoundRatio: number;
  /** Null means no monetary figure is produced at all - there is no defensible default. */

  usageReportLagDays: number;
  topSegments: number;
  minSeatsPerSegment: number;
  accountabilityDimension: string;
  maxLicensedUsersScored: number;
  maxOpportunityCandidates: number;
  maxAgents: number;
  maxUnlicensedUsersScored: number;
  maxCoworkUsersScored: number;
}

/** One Microsoft-published resource attached to an adoption action. */
export interface AdoptionGuidanceLink {
  actionCode: string;
  title: string;
  url: string;
  expectedTitle: string;
  audience: string;
  publisher: string;
  catalogueVersion: string;
}

export interface CopilotAdoptionPeriodRun {
  periodEnd: string;
  periodDays: number;
  optionsHash: string;
  auditAvailable: boolean;
  reportObfuscated: boolean;
  reportPeriodDays: number;
  licensedUsers: number;
  scoredUsers: number;
  publishedUtc: string;
  dataCutoffUtc: string;
  coverageStatus: string;
}

export interface CopilotAdoptionPeriodComparisonGate {
  left: CopilotAdoptionPeriodRun | null;
  right: CopilotAdoptionPeriodRun | null;
  optionsComparable: boolean;
  message: string;
}

export interface CopilotAdoptionCohortSummary {
  earlierPopulation: number;
  currentPopulation: number;
  newlyAssigned: number;
  earlierPopulationTransitionTotal: number;
  transitionsSumToEarlierPopulation: boolean;
  reclaimCaveat: string;
  warnings: string[];
}

export interface CopilotAdoptionCohortTransitionSummary {
  code: string;
  label: string;
  description: string;
  users: number;
  shareOfEarlierPopulationPct: number;
}

export interface CopilotAdoptionCohortFlowSummary {
  fromBand: string;
  toBand: string;
  transition: string;
  users: number;
}

export interface CopilotAdoptionActivationDistributionBucket {
  label: string;
  users: number;
  sharePct: number;
}

export interface CopilotAdoptionActivationSegment {
  segment: string;
  newSeatsAssignedInPeriod: number;
  activatedWithinWindow: number;
  activationRatePct: number | null;
  neverActivatedUsers: number;
  seatDateUnknownUsers: number;
}

export interface CopilotAdoptionActivationSummary {
  activationWindowDays: number;
  knownSeatStartUsers: number;
  seatDateUnknownUsers: number;
  assignedBeforeHistoryUsers: number;
  newSeatsAssignedInPeriod: number;
  activatedWithinWindow: number;
  activationRatePct: number | null;
  neverActivatedUsers: number;
  tooNewToJudgeUsers: number;
  medianDaysToFirstUse: number | null;
  distribution: CopilotAdoptionActivationDistributionBucket[];
  byDepartment: CopilotAdoptionActivationSegment[];
  caveat: string;
}

export interface CopilotAdoptionCohortComparison {
  gate: CopilotAdoptionPeriodComparisonGate;
  summary: CopilotAdoptionCohortSummary;
  transitions: CopilotAdoptionCohortTransitionSummary[];
  flows: CopilotAdoptionCohortFlowSummary[];
  activation: CopilotAdoptionActivationSummary;
}

export interface CopilotAdoptionCohortUserRow {
  userId: number;
  userPrincipalName: string;
  mail: string | null;
  department: string | null;
  jobTitle: string | null;
  manager: string | null;
  accountEnabled: boolean | null;
  existedInEarlierPeriod: boolean;
  existsInCurrentPeriod: boolean;
  activeInEarlierPeriod: boolean;
  activeInCurrentPeriod: boolean;
  fromBand: string;
  toBand: string;
  transition: string;
  transitionLabel: string;
  reclaimInterpretation: string | null;
  seatFirstObservedUtc: string | null;
  firstInteractionUtc: string | null;
  daysToFirstUse: number | null;
  activationState: string | null;
}

export interface CopilotAdoptionCohortUserPage {
  total: number;
  skip: number;
  take: number;
  rows: CopilotAdoptionCohortUserRow[];
  warnings: string[];
}

/** One active-day habit bucket (Infrequent / Moderate / Frequent / Daily). */
export interface AdoptionHabitBucket {
  label: string;
  rangeLabel: string;
  users: number;
  sharePct: number;
}

/** A department plotted as frequency (active days a month) against intensity (actions per active day). */
export interface AdoptionIntensityPoint {
  segment: string;
  licensedUsers: number;
  activeUsers: number;
  activeDaysPerUser: number;
  actionsPerActiveDay: number;
  activeUserAverageScore: number;
}

/** One recommended action and how many licensed users need it. */
export interface AdoptionActionSummary {
  code: string;
  label: string;
  description: string;
  users: number;
  sharePct: number;
  guidanceLinks?: AdoptionGuidanceLink[];
}

/** What to do about an agent. Numeric values match the C# AgentHealth enum, worst first. */
export enum AgentHealth {
  Retire = 0,
  Review = 1,
  New = 2,
  Keep = 3,
}

/** One Copilot agent with the figures an inventory review needs, and the verdict on it. */
export interface AgentUsageRow {
  agentId: number;
  name: string;
  agentKey: string | null;
  isCustomAgent: boolean;
  interactions: number;
  users: number;
  licensedUsers: number;
  activeDays: number;
  appsUsed: number;
  interactionsPerUser: number;
  firstUsedUtc: string | null;
  lastUsedUtc: string | null;
  daysSinceLastUse: number | null;
  health: AgentHealth;
  healthName: string;
  healthReason: string;
}

/** The agent estate at a glance. */
export interface AgentEstateSummary {
  historyDays: number;
  activeAgents: number;
  knownAgents: number;
  customAgents: number;
  agentUsers: number;
  licensedAgentUsers: number;
  agentInteractions: number;
  interactionsPerAgentUser: number;
  mostPopularAgent: string | null;
  mostVersatileAgent: string | null;
  healthBreakdown: ReportCategory[];
  usageByDepartment: ReportCategory[];
  usageByAgent: ReportCategory[];
  agents: AgentUsageRow[];
}

/** Unlicensed Copilot Chat as a population in its own right. */
export interface UnlicensedPopulationSummary {
  activeUsers: number;
  interactions: number;
  interactionsPerUserPerMonth: number;
  agentUsers: number;
  habitBuckets: AdoptionHabitBucket[];
  usageByApp: ReportCategory[];
  usageByDepartment: ReportCategory[];
  truncated: boolean;
}

/** The average shape of engagement for a group of users - frequency, depth and breadth on one scale. */
export interface AdoptionScoreProfile {
  label: string;
  users: number;
  frequencyScore: number;
  depthScore: number;
  breadthScore: number;
}

/** How much of all Copilot activity one cohort of users accounts for. */
export interface AdoptionConcentrationBand {
  label: string;
  users: number;
  interactions: number;
  sharePct: number;
  interactionsPerUser: number;
}

/** Licensed and unlicensed Copilot use for one department, side by side. */
export interface AdoptionCombinedSegmentRow {
  segment: string;
  licensedUsers: number;
  licensedActiveUsers: number;
  interactionsPerLicensedUser: number;
  licensedAgentUserPct: number;
  unlicensedActiveUsers: number;
  interactionsPerUnlicensedUser: number;
  unlicensedAgentUserPct: number;
}

/** Adoption, reclaim and next-action counts for one accountable unit. */
export interface AccountabilityRollupRow extends AdoptionSegmentRow {
  reclaimableSeats: number;
  reclaimCertainSeats: number;
  reclaimProbableSeats: number;
  reclaimReviewSeats: number;
  reclaimExcludedUsers: number;
  reclaimUsers: number;
  reengageUsers: number;
  coachUsers: number;
  broadenUsers: number;
  growUsers: number;
  sustainUsers: number;
  advocateUsers: number;
  reviewUsers: number;
  excludedUsers: number;
  opportunityUsers: number;
}

export interface CopilotAdoptionMetricDelta {
  metric: string;
  label: string;
  currentValue: number;
  priorValue: number;
  change: number;
  unit: 'count' | 'percent' | string;
  denominatorCurrent: number | null;
  denominatorPrior: number | null;
  denominatorChange: number | null;
}

export interface CopilotAdoptionPeriodMovement {
  mode: 'previousPeriod' | 'samePeriodLastQuarter' | string;
  available: boolean;
  comparable: boolean;
  currentPeriodEnd: string | null;
  priorPeriodEnd: string | null;
  periodDays: number;
  comparisonLabel: string | null;
  message: string | null;
  deltas: CopilotAdoptionMetricDelta[];
}

export interface CopilotAdoptionTarget {
  id: number;
  metric: string;
  label: string | null;
  scopeType: string;
  scopeValue: string | null;
  targetValue: number;
  owner: string;
  baselinePeriodEnd: string;
  baselinePeriodDays: number;
  baselineValue: number;
  baselineOptionsHash: string;
  baselineScoringOptionsHash: string;
  targetDate: string;
  createdUtc: string;
  createdBy: string | null;
  currentValue: number | null;
  progressPct: number | null;
  comparable: boolean;
  message: string | null;
}

/** The executive view. */
export interface CopilotAdoptionSummary {
  generatedUtc: string;
  windowDays: number;
  fromUtc: string;
  toUtc: string;
  dataSources: AdoptionDataSources;
  seatLicenceTypes: LicenceTypeClassification[];

  licensedUsers: number;
  scoredUsers: number;
  activeUsers: number;
  neverUsedUsers: number;
  dormantUsers: number;
  adoptionRatePct: number;
  habitualUsers: number;
  habitRatePct: number;
  reclaimableSeats: number;
  disabledLicensedUsers: number;
  purchasedCopilotSeats: number | null;
  unassignedCopilotSeats: number | null;
  subscribedSkusAvailable: boolean;
  reclaimCertainSeats: number;
  reclaimProbableSeats: number;
  reclaimReviewSeats: number;
  reclaimExcludedUsers: number;
  expiredReclaimExclusions: number;
  tooNewToJudgeUsers: number;
  reclaimCaveat: string | null;
  reclaimSeatsHeldBackForWindowMismatch: number;
  reclaimSeatsHeldBackForReview: number;
  reclaimSeatsFromActiveBands: number;
  usageReportSourcedUsers: number;
  usageReportSourcedUserPct: number;
  usageReportWindowMismatch: boolean;
  averageAdoptionScore: number;
  medianAdoptionScore: number;
  totalInteractions: number;

  coworkUsers: number;
  coworkAdoptionPct: number | null;
  coworkEligibilityKnown: boolean;
  coworkEligibleUsers: number | null;
  coworkAuditUsers: number;
  coworkInteractions: number;
  coworkReportUsers: number;
  coworkReportTotalTasks: number;
  coworkReportScheduledTasks: number;
  coworkReportUserInitiatedTasks: number;
  coworkAutomationRatioPct: number | null;
  coworkTasksPerActiveUser: number | null;
  coworkReportRetainedUsers: number | null;
  coworkReportRetentionPct: number | null;
  coworkDetected: boolean;

  /**
   * False when the Cowork readiness step did not run or produced nothing. The tab must say so rather
   * than render an empty quadrant, which reads as "nobody is a candidate" - a finding, not a fault.
   */
  coworkReadinessAvailable: boolean;
  coworkScoredUsers: number;
  coworkEstablishedUsers: number;
  coworkTriallingUsers: number;
  coworkPrimeCandidates: number;
  coworkBuildFluencyFirst: number;
  coworkRecommendedForPolicy: number;
  coworkAverageCoordinationLoad: number;
  coworkAverageFluency: number;
  coworkTiers: CoworkTierSummary[];
  coworkQuadrant: CoworkQuadrantPoint[];
  coworkByDepartment: CoworkSegmentRow[];
  coworkCreditPosition: CoworkCreditPosition;
  coworkValueEstimate: CoworkValueEstimate;

  unlicensedActiveUsers: number;
  recommendedForLicence: number;

  funnel: ReportCategory[];
  bandBreakdown: ReportCategory[];
  habitBuckets: AdoptionHabitBucket[];
  intensityByDepartment: AdoptionIntensityPoint[];
  actionPlan: AdoptionActionSummary[];
  guidanceCatalogueVersion?: string;
  guidanceLinks?: AdoptionGuidanceLink[];
  adoptionByDepartment: AdoptionSegmentRow[];
  habitByDepartment: AdoptionSegmentRow[];
  adoptionByCountry: AdoptionSegmentRow[];

  /** Adoption by email domain - i.e. by the organisations that share this tenant. */
  emailDomains: AdoptionDomainRow[];

  /**
   * The email domain every figure above describes, or null for the whole tenant. Echoed back by the
   * server rather than assumed from the request, so the page can state which population it is
   * showing - a dashboard silently scoped to one subsidiary is how a licence decision goes wrong.
   */
  scopedEmailDomain: string | null;

  /**
   * Sections that stayed tenant-wide while `scopedEmailDomain` is set, because they come from
   * aggregate queries carrying no per-user identity. Values are the constants in
   * {@link UNSCOPED_SECTIONS}; render each as a "tenant-wide" badge rather than hiding it.
   */
  unscopedSections: string[];
  accountabilityDimension: string | null;
  accountabilityDimensionLabel: string | null;
  accountabilityRollup: AccountabilityRollupRow[];
  usageByApp: ReportCategory[];
  opportunityByDepartment: ReportCategory[];
  weeklyTrend: ReportSeries[];
  weeklyVolumeTrend: ReportSeries[];
  scoreProfiles: AdoptionScoreProfile[];
  concentration: AdoptionConcentrationBand[];
  combinedByDepartment: AdoptionCombinedSegmentRow[];
  topResourceTypes: AdoptionResourceTypeRow[];
  agents: AgentEstateSummary;
  unlicensed: UnlicensedPopulationSummary;
  periodMovement: CopilotAdoptionPeriodMovement;
  targets: CopilotAdoptionTarget[];

  options: CopilotAdoptionOptions;
  warnings: string[];

  /**
   * True when a query the headline figures are DERIVED FROM failed, so everything below describes an
   * incomplete population. Distinct from `warnings`, which mean "one chart is missing" - this means the
   * licensed-user population itself could not be loaded, and the rates, funnel and segments were computed
   * from whatever did load. See issue #360.
   */
  figuresIncomplete: boolean;

  /** Which datasets could not be loaded, for the message shown in place of the figures. */
  incompleteReasons: string[];
}

/** One licensed user with the adoption maths applied. */
export interface LicensedUserAdoptionRow {
  userId: number;
  userPrincipalName: string;
  mail: string | null;
  department: string | null;
  jobTitle: string | null;
  country: string | null;
  officeLocation: string | null;
  companyName: string | null;
  manager: string | null;
  accountEnabled: boolean | null;
  accountCreatedUtc: string | null;
  tenureStartUtc: string | null;
  tenureBasis: string | null;
  daysSinceTenureStart: number | null;
  tooNewToJudge: boolean;
  reclaimEligibility: string | null;
  reclaimEligibilityReason: string | null;
  reclaimExclusionReason: string | null;
  reclaimExclusionNote: string | null;
  reclaimExcludedBy: string | null;
  reclaimExcludedUtc: string | null;
  reclaimExclusionReviewAfterUtc: string | null;
  reclaimExclusionExpired: boolean;
  seatLicences: string | null;

  interactions: number;
  activeDays: number;
  auditInteractions: number;
  auditActiveDays: number;
  auditAppsUsed: number;
  sourceComparisonAvailable: boolean;
  expectedActiveDays: number;
  appsUsed: number;
  agentsUsed: number;
  coworkInteractions: number;
  coworkReportTotalTasks: number | null;
  coworkReportScheduledTasks: number | null;
  coworkReportUserInitiatedTasks: number | null;
  coworkReportActiveDays: number | null;
  coworkReportLastActivityDate: string | null;
  coworkReportRetainedUser: boolean | null;
  coworkAutomationRatioPct: number | null;
  coworkCreditsPerTask: number | null;
  usedCowork: boolean;

  firstInteractionUtc: string | null;
  lastInteractionUtc: string | null;
  daysSinceLastUse: number | null;

  reportPrompts: number | null;
  reportActiveDays: number | null;
  reportLastActivityUtc: string | null;

  adoptionScore: number;
  frequencyScore: number;
  depthScore: number;
  breadthScore: number;
  band: AdoptionBand;
  bandName: string;
  signalSource: string;
  recommendedAction: string;
  recommendedActionCode: string;
  recommendedActionLabel: string;
}

/** An unlicensed user ranked as a candidate for a Copilot seat. */
export interface LicenceOpportunityRow {
  userId: number;
  userPrincipalName: string;
  mail: string | null;
  department: string | null;
  jobTitle: string | null;
  country: string | null;
  officeLocation: string | null;
  companyName: string | null;
  manager: string | null;

  unlicensedCopilotInteractions: number;
  unlicensedCopilotActiveDays: number;
  lastCopilotInteractionUtc: string | null;

  teamsMessages: number;
  teamsMeetings: number;
  emailsSent: number;
  emailsRead: number;
  filesViewedOrEdited: number;
  lastM365ActivityUtc: string | null;

  opportunityScore: number;
  copilotDemandScore: number;
  collaborationScore: number;
  emailScore: number;
  documentScore: number;
  recommended: boolean;

  /**
   * Which route qualified this person - proven demand, workload inferred, or neither - decided by
   * the scorer and sent down as a label. Never re-derive it in the client: the scorer floors the
   * proven-demand threshold with `Math.Max(1, ...)`, so a client-side comparison against the raw
   * configured option disagrees with the server whenever that option is set to zero or less.
   */
  qualificationTier: string | null;
  qualificationTierLabel: string | null;
  rationale: string;
}

export interface LicensedUserPage {
  total: number;
  skip: number;
  take: number;
  rows: LicensedUserAdoptionRow[];
  warnings: string[];
}

export interface LicenceOpportunityPage {
  total: number;
  skip: number;
  take: number;
  rows: LicenceOpportunityRow[];
  warnings: string[];
}

/** Distinct values for the filter drop-downs, derived from the loaded analysis. */
export interface AdoptionFilterOptions {
  /**
   * Every email domain present in any population - licensed, unlicensed, candidate or Cowork - so a
   * domain that holds no seats at all is still selectable. That case is the interesting one: an
   * acquired business using Copilot Chat without ever having been given a licence.
   */
  emailDomains?: string[];
  departments: string[];
  countries: string[];
  bands: { value: number; name: string }[];
  /** Cowork tiers, each carrying whether it rests on observed usage or on inference. */
  coworkTiers?: { value: string; name: string; basis: CoworkBasis }[];
}

/**
 * Names of the sections the server leaves tenant-wide when the report is narrowed to one email
 * domain. Kept in step with `CopilotAdoptionUnscopedSections` on the server.
 */
export const UNSCOPED_SECTIONS = {
  usageByApp: 'usageByApp',
  topResourceTypes: 'topResourceTypes',
  weeklyTrend: 'weeklyTrend',
  agents: 'agents',
  purchasedSeats: 'purchasedSeats',
  coworkCredits: 'coworkCredits',
  periodMovement: 'periodMovement',
} as const;

/** Filter/sort state for the licensed-user list. */
export interface LicensedUserFilters {
  search: string;
  bands: AdoptionBand[];
  /** Recommended-action codes to restrict to. Drives the drill-through from the enablement plan. */
  actions: string[];
  department: string;
  country: string;
  /**
   * The email domain the list is narrowed to. Empty means the whole tenant.
   *
   * Held on the filter object rather than passed separately so the list, its CSV export and the
   * summary above it can never drift apart about which population they describe.
   */
  emailDomain: string;
  reclaimEligibility: string;
  coworkOnly: boolean;
  disabledOnly: boolean;
  sortBy: string;
  sortDesc: boolean;
}

/** Filter/sort state for the licence-opportunity list. */
export interface OpportunityFilters {
  search: string;
  department: string;
  country: string;
  /** The email domain the list is narrowed to. Empty means the whole tenant. */
  emailDomain: string;
  recommendedOnly: boolean;
  existingCopilotUsersOnly: boolean;
  sortBy: string;
  sortDesc: boolean;
}

/**
 * Whether a Cowork verdict rests on observed usage or on a prediction.
 *
 * Carried as data rather than inferred from the tier name in the UI, so there is exactly one place
 * that decides it. Presenting a prediction as an observation is the worst thing this tab could do.
 */
export type CoworkBasis = 'evidence' | 'inference';

/** The Cowork populations, strongest evidence first. */
export type CoworkTier =
  | 'established'
  | 'trialling'
  | 'primeCandidate'
  | 'buildFluencyFirst'
  | 'lowCoordinationLoad'
  | 'notIndicated';

export interface CoworkTierSummary {
  code: CoworkTier;
  label: string;
  basis: CoworkBasis;
  description: string;
  users: number;
  sharePct: number;
}

/** A department placed on the readiness quadrant. */
export interface CoworkQuadrantPoint {
  segment: string;
  licensedUsers: number;
  coordinationLoadScore: number;
  fluencyScore: number;
  regularCoworkUsers: number;
  primeCandidates: number;
}

/** One department's position in the rollout order. */
export interface CoworkSegmentRow {
  segment: string;
  licensedUsers: number;
  primeCandidates: number;
  primeCandidateRatePct: number;
  regularCoworkUsers: number;
  coworkReportTotalTasks: number;
  coworkReportScheduledTasks: number;
  coworkAutomationRatioPct: number | null;
  coworkReportRetainedUsers: number | null;
  coworkReportRetentionPct: number | null;
  coworkAdoptionPct: number;
  averageCoordinationLoad: number;
  averageFluency: number;
}

/**
 * The tenant's Copilot Credit position.
 *
 * This is the SHARED pool, not Cowork-only spend: Cowork draws on it, which makes it valid rollout
 * headroom, but Copilot Studio and other credit-billed workloads draw on the same pool and Microsoft
 * publishes no way to separate them. Every label built from this must say so.
 */
export interface CoworkCreditPosition {
  available: boolean;
  snapshotUtc: string | null;
  entitled: number | null;
  consumed: number | null;
  available_credits: number | null;
  payAsYouGoConsumed: number | null;
  status: string | null;
  /** When false the per-user credit column is hidden entirely rather than filled with "not attributable". */
  perUserCreditsAvailable: boolean;
}

/**
 * The modelled time/cost estimate.
 *
 * The `addressable*` volumes are observed; the hours and money are those volumes multiplied by an
 * assumption. `assumptions` travels with the numbers so no component can render a figure without it.
 */
export interface CoworkValueEstimate {
  isModelled: boolean;
  cohortUsers: number;
  addressableMeetings: number;
  addressableMailThreads: number;
  addressableDocuments: number;
  hoursPerMonthLow: number;
  hoursPerMonthHigh: number;
  // No monetary fields: this estimate is modelled, and currency is reported only against idle
  // licence spend, which is measured. See CoworkValueEstimate in CopilotAdoptionCoworkModels.cs.
  assumptions: string[];
}

/** One Copilot seat holder assessed for Cowork readiness. */
export interface CoworkReadinessRow {
  userId: number;
  userPrincipalName: string;
  mail: string | null;
  department: string | null;
  jobTitle: string | null;
  country: string | null;
  officeLocation: string | null;
  companyName: string | null;
  manager: string | null;
  accountEnabled: boolean | null;

  coworkInteractions: number;
  coworkActiveDays: number;
  lastCoworkInteractionUtc: string | null;
  coworkReportTotalTasks: number | null;
  coworkReportScheduledTasks: number | null;
  coworkReportUserInitiatedTasks: number | null;
  coworkReportActiveDays: number | null;
  coworkReportLastActivityDate: string | null;
  coworkReportRetainedUser: boolean | null;
  coworkAutomationRatioPct: number | null;
  coworkCreditsPerTask: number | null;
  usedCowork: boolean;
  regularCoworkUser: boolean;

  coordinationLoadScore: number;
  fluencyScore: number;
  adoptionScore: number;
  agentsUsed: number;

  collaborationScore: number;
  meetingScore: number;
  emailScore: number;
  documentScore: number;
  teamsMessages: number;
  teamsMeetings: number;
  emailsSent: number;
  emailsRead: number;
  filesViewedOrEdited: number;
  lastM365ActivityUtc: string | null;

  tier: CoworkTier;
  tierLabel: string;
  basis: CoworkBasis;
  recommendForPolicy: boolean;
  rationale: string;

  /**
   * ALL Copilot Credits for this user, not Cowork's share - Microsoft exposes no per-row workload
   * discriminator. Null means not attributable, and must never be rendered as 0.
   */
  totalCopilotCredits: number | null;
}

export interface CoworkReadinessPage {
  total: number;
  skip: number;
  take: number;
  rows: CoworkReadinessRow[];
  warnings: string[];
}

/** Filter/sort state for the Cowork readiness list. */
export interface CoworkFilters {
  search: string;
  tiers: CoworkTier[];
  department: string;
  country: string;
  /** The email domain the list is narrowed to. Empty means the whole tenant. */
  emailDomain: string;
  recommendedOnly: boolean;
  coworkUsersOnly: boolean;
  sortBy: string;
  sortDesc: boolean;
}


export interface CopilotAdoptionCreateInterventionRequest {
  actionCode: string;
  name?: string;
  owner?: string;
  interventionType?: string;
  status?: string;
  guidanceResource?: string;
  dueUtc?: string | null;
  intendedOutcome?: string;
  notes?: string;
  intendedReinvestmentType?: string;
  intendedReinvestmentDescription?: string;
}

export interface CopilotAdoptionCohort {
  cohortId: number;
  name: string;
  actionCode: string;
  createdUtc: string;
  createdBy: string;
  baselinePeriodEnd: string;
  baselinePeriodDays: number;
  baselineOptionsHash: string;
  closedUtc: string | null;
  memberCount: number;
  holdoutCount: number;
}

export interface CopilotAdoptionIntervention {
  interventionId: number;
  cohortId: number;
  cohortName: string;
  actionCode: string;
  owner: string | null;
  interventionType: string;
  guidanceResource: string | null;
  startedUtc: string | null;
  dueUtc: string | null;
  completedUtc: string | null;
  status: string;
  intendedOutcome: string | null;
  notes: string | null;
  intendedReinvestmentType: string;
  intendedReinvestmentDescription: string | null;
  createdUtc: string;
  memberCount: number;
  isOverdue: boolean;
  isUnstarted: boolean;
}

export interface CopilotAdoptionLeadingIndicatorOutcome {
  code: string;
  label: string;
  treatedChange: number;
  controlChange: number;
  differenceInDifferences: number;
  movementLabel: string;
}

export interface CopilotAdoptionInterventionOutcome {
  intervention: CopilotAdoptionIntervention;
  cohort: CopilotAdoptionCohort;
  followupPeriodEnd: string;
  methodLabel: string;
  observational: boolean;
  refused: boolean;
  refusalReason: string | null;
  matchingCriteria: string;
  treatedN: number;
  controlN: number;
  treatedChange: number;
  controlChange: number;
  differenceInDifferences: number;
  effectSizeLabel: string | null;
  leadingIndicators: CopilotAdoptionLeadingIndicatorOutcome[];
}
