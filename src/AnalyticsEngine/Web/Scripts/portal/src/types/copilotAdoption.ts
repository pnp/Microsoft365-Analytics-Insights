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

  /**
   * Minutes Microsoft 365 Copilot is assumed to save per meeting, email and document. They drive the
   * licence estimate. Named `coworkMinutesSavedPer*` until the licence estimate was split out.
   */
  copilotMinutesSavedPerMeeting: number;
  copilotMinutesSavedPerMailThread: number;
  copilotMinutesSavedPerDocument: number;
  /** The conservative share of every minutes-saved assumption. Shared by both estimates. */
  coworkEstimateLowerBoundRatio: number;
  /**
   * Minutes Cowork is assumed to save per task already in Microsoft's Cowork report, on top of Copilot.
   * No study has measured it.
   */
  coworkMinutesSavedPerTask: number;
  /**
   * For each kind of work Cowork could take on (`CoworkActivity`), the share of it handed to Cowork
   * (0 to 1) and the minutes Cowork saves on each piece, on top of Copilot. Assumptions, all of them -
   * see CoworkActivities.cs. Named so the Excel export can send a reader's figure back under the same key.
   */
  coworkOrganiseMeetingsShare: number;
  coworkOrganiseMeetingsMinutes: number;
  coworkPrepareMeetingsShare: number;
  coworkPrepareMeetingsMinutes: number;
  coworkSendEmailShare: number;
  coworkSendEmailMinutes: number;
  coworkPostInTeamsShare: number;
  coworkPostInTeamsMinutes: number;
  coworkCreateDocumentsShare: number;
  coworkCreateDocumentsMinutes: number;

  usageReportLagDays: number;
  topSegments: number;
  minSeatsPerSegment: number;
  accountabilityDimension: string;
  maxLicensedUsersScored: number;
  maxOpportunityCandidates: number;
  /**
   * Rows per per-user sheet in the Excel export. Optional because the portal never reads it - it is
   * carried here only so this type still describes the payload the API returns.
   */
  maxWorkbookUserRows?: number;
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
  emptySegmentKey?: string | null;
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
  /**
   * The modelled Cowork estimate for the people ready for Cowork now (the recommended policy cohort):
   * the time Cowork could give back on top of what their Copilot licences already save.
   */
  coworkValueEstimate: CoworkValueEstimate;
  /**
   * The same model over every scored Copilot seat holder - the ceiling if everyone used Cowork.
   * Optional only so a fixture written before it existed still type-checks; the server always sends it.
   */
  coworkFullRolloutEstimate?: CoworkValueEstimate;

  unlicensedActiveUsers: number;
  recommendedForLicence: number;
  /**
   * The modelled licence estimate: the time Microsoft 365 Copilot could give back to every person
   * recommended for a licence. Empty (no cohort) when nobody is recommended or the Microsoft 365 usage
   * reports are unavailable. Optional only so older fixtures still type-check.
   */
  licenceOpportunityEstimate?: LicenceValueEstimate;
  /** The same model over the recommended candidates already using Copilot Chat without a licence. */
  licenceChatUsersEstimate?: LicenceValueEstimate;

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
 * The kinds of work the Cowork estimate models for people not yet running Cowork tasks: each thing
 * Microsoft says Cowork does, against the count Microsoft's usage reports keep of people doing it by
 * hand. In the order the server publishes and sums them - see `COWORK_ACTIVITIES`.
 */
export type CoworkActivity = 'organiseMeetings' | 'prepareMeetings' | 'sendEmail' | 'postInTeams' | 'createDocuments';

/** What one cohort already does by hand of one kind of work, a month. Observed, not modelled. */
export interface CoworkActivityVolume {
  activity: CoworkActivity;
  /** Done by hand a month by the people not yet running Cowork tasks. */
  volumePerMonth: number;
}

/**
 * The modelled Cowork estimate for one cohort: the time Cowork could give back ON TOP of what Microsoft
 * 365 Copilot already saves - the value of enabling Cowork, paid for in Copilot Credits.
 *
 * The Cowork tasks already in Microsoft's report, at minutes per task; and for everyone else, each kind
 * of work they already do by hand x the share of it handed to Cowork x the minutes saved on each piece.
 * The volumes are observed; the shares and minutes are assumptions no study has tested. There is
 * deliberately no Copilot layer: these people already hold a licence, and the time it gives back is not
 * Cowork's to claim.
 *
 * `assumptions` travels with the numbers so no component can render a figure without it. The portal
 * recomputes the hours from the published inputs whenever the reader enters their own assumptions -
 * see `components/copilotAdoption/coworkTimeSaved.ts`.
 */
export interface CoworkValueEstimate {
  isModelled: boolean;
  cohortUsers: number;
  /** People in the cohort with Cowork tasks in Microsoft's Cowork report. Observed. */
  coworkTaskUsers: number;
  /** Their tasks, restated as a month. Observed. */
  observedCoworkTasks: number;
  /** Everyone else in the cohort, modelled from the work they already do. */
  projectedCoworkUsers: number;
  /** That work, a month, one entry per kind - every kind present, in `COWORK_ACTIVITIES` order. Observed. */
  activities: CoworkActivityVolume[];
  /** Pieces of work a month handed to Cowork: each volume x its share, summed, then rounded. */
  projectedCoworkTasks: number;
  /** Observed tasks plus the pieces of work handed over. */
  coworkTasks: number;
  /**
   * The tenant's own Cowork users' average tasks a month - the sense check, not an input. Zero when
   * nobody has Cowork tasks in the report.
   */
  observedTasksPerPersonPerMonth: number;
  /** How many people that average is of. */
  observedTaskRateUsers: number;
  hoursPerMonthLow: number;
  hoursPerMonthHigh: number;
  // No monetary fields, and none anywhere else in this report: the estimate is modelled, and a money
  // figure derived from it would be quoted as though it were measured. See CoworkValueEstimate in
  // CopilotAdoptionCoworkModels.cs.
  assumptions: string[];
}

/**
 * The modelled licence estimate: the time Microsoft 365 Copilot could give back to people recommended
 * for a licence - observed meetings, emails and documents times the minutes Copilot is assumed to save
 * on each, evidenced by published studies of Microsoft 365 Copilot.
 *
 * See `LicenceValueEstimate` in CopilotAdoptionTimeSavedModels.cs. No monetary fields.
 */
export interface LicenceValueEstimate {
  isModelled: boolean;
  cohortUsers: number;
  /** Meetings a month across the cohort. Observed. */
  addressableMeetings: number;
  /** Emails sent and read a month across the cohort. Observed. */
  addressableMailThreads: number;
  /** SharePoint and OneDrive documents viewed or edited a month across the cohort. Observed. */
  addressableDocuments: number;
  hoursPerMonthLow: number;
  hoursPerMonthHigh: number;
  /** True when the candidate list reached its row cap, so the figure is a floor. */
  candidatesCapped: boolean;
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
