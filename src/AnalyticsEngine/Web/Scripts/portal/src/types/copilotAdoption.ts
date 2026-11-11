// Mirrors Common/Entities/CopilotAdoption/CopilotAdoptionModels.cs (returned by api/CopilotAdoption).
//
// The chart shapes (AdoptionSeries / AdoptionCategory) are deliberately identical to the Reports
// area's ReportSeries / ReportCategory so the existing TimeSeriesChart and CategoryBarChart
// components render them with no new charting code.

import type { ReportCategory, ReportSeries } from './reports';
import type { UserFilterEcho } from './userFilter';
import type { GlobalFilterEcho } from './globalFilter';
import type { LeadershipAdoptionComparison } from './leadershipCohort';
import type { CopilotAdoptionScoreSettingsInfo } from './copilotAdoptionSettings';

/** Which parts of the adoption tool this deployment can show. */
export interface CopilotAdoptionAvailability {
  available: boolean;
  copilotAuditImportEnabled: boolean;
  copilotUsageReportImportEnabled: boolean;
  userMetadataImportEnabled: boolean;
  m365UsageReportImportEnabled: boolean;
  messages: string[];
}

/** Why a Copilot seat's idle count is withheld from a reader without See PII (C# SeatIdleWithheldReasons). */
export type SeatIdleWithheldReason = 'fewHolders' | 'revealsSmallerSeat';

/**
 * Why a figure is withheld from a reader without See PII (C# SmallGroupWithheldReasons): `fewPeople` - fewer than the
 * floor are behind it - or `revealsSmallerGroup` - enough are, but with the total published beside it, it would give
 * back a figure withheld for having fewer. A key, never text; the page writes the sentence.
 */
export type SmallGroupWithheldReason = 'fewPeople' | 'revealsSmallerGroup';

/** A breakdown `CopilotAdoptionSummary.smallestGroupsWithheld` names (#844); the server's CopilotAdoptionWithheldBreakdowns. */
export type WithheldBreakdown =
  | 'departments'
  | 'countries'
  | 'accountability'
  | 'unlicensedDepartments'
  | 'agentDepartments'
  | 'builderDepartments';

/** A licence type and whether the tool counted it as a Microsoft 365 Copilot seat. */
export interface LicenceTypeClassification {
  id: number;
  name: string;
  skuPartNumber: string;
  assignedUsers: number;
  purchasedUnits: number | null;
  unassignedUnits: number | null;
  /** Null only when withheld from a reader without See PII (`assignedIdleWithheld`) - never a substitute zero. */
  assignedIdleUsers: number | null;
  /**
   * True when a reader without See PII is not given `assignedIdleUsers`: the seat is held by fewer than `minimumHolders`
   * people, or, with the reclaimable total, its count would give such a seat's back. Absent from an older server.
   */
  assignedIdleWithheld?: boolean;
  /** Why `assignedIdleUsers` is withheld; null when it is shown. A key the page turns into a sentence. */
  assignedIdleWithheldReason?: SeatIdleWithheldReason | null;
  /** On a withheld row, the fewest holders a seat needs for its idle count to be shown to this reader. */
  minimumHolders?: number | null;
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
  m365UsageReportsAvailable: boolean;
  userMetadataAvailable: boolean;
  copilotUsageReportDate: string | null;
  copilotUsageReportPeriodDays: number;
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
  /**
   * The department's unlicensed people recommended for a licence, over every candidate. Department rows only;
   * null when withheld (fewer than minSeatsPerSegment) or unknown - never a substitute zero. Optional only so
   * fixtures written before it still type-check.
   */
  recommendedCandidates?: number | null;
  /**
   * Set when a count large enough to show is withheld anyway, for a reader without See PII: with the tenant's
   * recommended candidates, it would give back a department withheld for having fewer (#844).
   */
  recommendedCandidatesWithheld?: SmallGroupWithheldReason | null;
}

/**
 * Adoption for one email domain - one of the organisations sharing this tenant.
 *
 * Richer than a plain segment because a domain is usually a whole company rather than a function
 * within one. A domain with idle seats AND unlicensed Copilot Chat use is a seat-allocation problem
 * inside one business; a domain with strong adoption and a queue of licence candidates is a
 * business case. Neither is visible when the same people are split across departments that span
 * every company in the tenant.
 *
 * Three populations share a row - the seat holders (every figure from `licensedUsers` to
 * `coworkPrimeCandidates`), the unlicensed Copilot Chat users and the licence candidates - and for a reader
 * without See PII each is floored on its own (#844): a withheld population's figures are null, with the reason
 * beside them. Never render a withheld figure as 0, and never read withheld seat holders as "no seats".
 */
export interface AdoptionDomainRow {
  segment: string;
  licensedUsers: number | null;
  activeUsers: number | null;
  habitualUsers: number | null;
  neverUsedUsers: number | null;
  adoptionRatePct: number | null;
  averageAdoptionScore: number | null;
  /** Seats in this domain that look reclaimable, on the same confidence tiering as the headline. */
  reclaimableSeats: number | null;
  /** Audit interactions per licensed user, normalised to a month. Idle seats are in the denominator. */
  interactionsPerLicensedUser: number | null;
  /** People here using Copilot Chat in the window with no seat assigned. */
  unlicensedActiveUsers: number | null;
  /** People here the licence-opportunity ranking recommends buying a seat for. */
  recommendedForLicence: number | null;
  /** Seat holders here scored as prime Cowork candidates. */
  coworkPrimeCandidates: number | null;
  /**
   * True when this domain is external guests rather than members of the tenant - a partner being
   * collaborated with, not a part of the business that can be sent on a training course.
   */
  external: boolean;
  /** Why the seat holders' figures are withheld. Absent when they are shown. */
  seatHoldersWithheld?: SmallGroupWithheldReason | null;
  /** Why `unlicensedActiveUsers` is withheld. Absent when it is shown. */
  unlicensedUsersWithheld?: SmallGroupWithheldReason | null;
  /** Why `recommendedForLicence` is withheld. Absent when it is shown. */
  licenceCandidatesWithheld?: SmallGroupWithheldReason | null;
  /** The fewest people a figure on this row may describe, when any of them is withheld. */
  minimumPeople?: number | null;
}

/** Every threshold and weight the adoption maths used, echoed back so a figure can be traced to its rule. */
export interface CopilotAdoptionOptions {
  guidanceCatalogueVersion?: string;
  windowDays: number;
  fromUtc?: string | null;
  toUtc?: string | null;
  toExclusiveUtc?: string | null;
  usesExplicitDates?: boolean;
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
  /** Whether an administrator customised the weights or band thresholds above. Absent from older servers. */
  scoreSettings?: CopilotAdoptionScoreSettingsInfo;

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
  copilotSeatOutlookMinutesPerAction?: number;
  copilotSeatOfficeMinutesPerAction?: number;
  copilotSeatMeetingMinutesPerAction?: number;
  copilotSeatUncreditedMinutesPerAction?: number;
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
  /** How many of the heaviest agent users the Agents tab lists. */
  topAgentUsers?: number;
  /** How many agent users, heaviest first, the analysis keeps to pick that list from. */
  maxAgentUsersScored?: number;
  maxUnlicensedUsersScored: number;
  maxCoworkUsersScored: number;
}

/** One Microsoft-published resource attached to an adoption action. */
export interface AdoptionGuidanceLink {
  actionCode: string;
  /** Stable per-resource key; the SPA translates the title through it (see guidanceLinkTitle). */
  titleKey?: string;
  /** English title, shown only for a titleKey this build does not know. */
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

/**
 * Who made an agent, as the stable key the server sends (C# `CopilotAgentOriginKeys`). `unknown` is a real
 * answer: the audit log does not always say, and the product does not guess.
 */
export type AgentOrigin = 'customerBuilt' | 'microsoft' | 'unknown';

/** One Copilot agent with the figures an inventory review needs, and the verdict on it. */
export interface AgentUsageRow {
  agentId: number;
  name: string;
  agentKey: string | null;
  /** Who made the agent. Map it to text with `agentOriginLabel`. */
  origin: AgentOrigin;
  /** True for a customer-built agent (`origin` is `customerBuilt`). Kept for older clients; prefer `origin`. */
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
  /** People who used it in the reporting period - the denominator of the home share (#647). */
  windowUsers?: number | null;
  /** Departments with at least one of its users in the period. Null when not measured. */
  departments?: number | null;
  /** The department most of its users are in; null when fewer than minSeatsPerSegment of them are. Tenant data. */
  homeDepartment?: string | null;
  /** The share of its period users in the home department, 0-100. */
  homeDepartmentSharePct?: number | null;
}

/** The agent estate at a glance. */
export interface AgentEstateSummary {
  historyDays: number;
  /** Agents used in the reporting period. Null for a past period that could not be measured - never the inventory's count. */
  activeAgents: number | null;
  knownAgents: number;
  /** Customer-built agents only. */
  customAgents: number;
  /** Agents whose origin the audit log does not reveal - counted separately rather than guessed. */
  unknownOriginAgents: number;
  agentUsers: number;
  licensedAgentUsers: number;
  /** Agent interactions in the reporting period; null when activeAgents is. */
  agentInteractions: number | null;
  interactionsPerAgentUser: number | null;
  mostPopularAgent: string | null;
  mostVersatileAgent: string | null;
  healthBreakdown: ReportCategory[];
  usageByDepartment: ReportCategory[];
  usageByAgent: ReportCategory[];
  agents: AgentUsageRow[];
  /**
   * Which agents `growth` counts: 'customerBuilt' (the Work Trend Index's scope), or 'allAgents'. A key, so
   * the page words it. Optional only so fixtures written before #645 still type-check.
   */
  growthScope?: string;
  /** The first Copilot interaction the audit log holds - what explains a blank year-ago window. */
  growthAuditHistoryStartUtc?: string | null;
  /** Agent use over consecutive closed 28-day windows, most recent first (#645). Empty when not computed. */
  growth?: AgentGrowthWindow[];
}

/**
 * One closed 28-day window of the agent growth series. Every figure is null when the window was not
 * measured - never zero - so "not measured" and "nobody used an agent" stay distinguishable.
 */
export interface AgentGrowthWindow {
  /** 0 for the most recent closed window, 13 for the same 28 days a year earlier. */
  windowsAgo: number;
  fromUtc: string;
  toUtc: string;
  activeAgents: number | null;
  /**
   * Agents of unknown origin used in the window, left out of every other figure because nobody can say who
   * made them: the figures are a floor by this much. Optional only so fixtures written before it still type-check.
   */
  unknownOriginAgents?: number | null;
  agentUsers: number | null;
  agentInteractions: number | null;
  interactionsPerAgentUser: number | null;
  /** Separate evidence of autonomous runs: Copilot Studio agents with billed consumption. Never added to activeAgents. */
  copilotStudioBilledAgents: number | null;
}

/**
 * One of the people who used Copilot agents most in the period, and whether they hold a Copilot seat.
 * Names a person, so the server only sends these to a reader with the See PII permission.
 */
export interface AgentUserRow {
  userId: number;
  userPrincipalName: string;
  mail: string | null;
  emailDomain: string | null;
  department: string | null;
  jobTitle: string | null;
  interactions: number;
  agentsUsed: number;
  activeDays: number;
  lastUsedUtc: string | null;
  /** The agent this person used most, or the server's unnamed-agent placeholder. */
  topAgentName: string;
  topAgentInteractions: number;
  holdsCopilotSeat: boolean;
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
  /**
   * Each side's figures are null when fewer than `minSeatsPerSegment` people are on that side: withheld so
   * that no one can be singled out, never a measured zero.
   */
  licensedUsers: number | null;
  licensedActiveUsers: number | null;
  interactionsPerLicensedUser: number | null;
  licensedAgentUserPct: number | null;
  unlicensedActiveUsers: number | null;
  interactionsPerUnlicensedUser: number | null;
  unlicensedAgentUserPct: number | null;
  /** True when the agent figures below are null because too few people are behind them, not because they were not measured. */
  agentFiguresWithheld?: boolean;
  /**
   * Agent breadth and depth (#646) over the department's active Copilot users seen in the audit log, and its
   * Copilot Studio builders (#647). Each is null when it could not be measured, and absent from a server
   * that predates it.
   */
  agentActiveUsers?: number | null;
  agentUsers?: number | null;
  agentUserPct?: number | null;
  distinctAgents?: number | null;
  agentsPer100ActiveUsers?: number | null;
  agentInteractions?: number | null;
  interactionsPerActiveAgent?: number | null;
  agentBuilders?: number | null;
  /**
   * Set, for a reader without See PII, when that side's figures are null although enough people are on it: with the
   * tenant's figures beside the table, they would give back a department too small to list (#844).
   */
  licensedWithheld?: SmallGroupWithheldReason | null;
  unlicensedWithheld?: SmallGroupWithheldReason | null;
  /** Why the agent figures are withheld, when it is to protect a smaller department rather than this one's size (#844). */
  agentFiguresWithheldReason?: SmallGroupWithheldReason | null;
  /** Set when `agentBuilders` is null for a reader without See PII: too few builders, or it would give back such a count (#844). */
  agentBuildersWithheld?: SmallGroupWithheldReason | null;
  /** The floor a reader without See PII is held to, sent with any of the reasons above. */
  minimumPeople?: number | null;
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

/**
 * Whether people managers use Copilot themselves, and how their direct reports compare (#641), for one
 * population - the whole summary, or one department. Aggregates only: no manager is ever named. A null
 * figure was withheld because the group was smaller than `minSeatsPerSegment` (or there was nothing to
 * divide) - a blank, never a measured zero.
 */
export interface ManagerModellingFigures {
  /** Licensed users counted as direct reports: enabled, with an enabled manager who is someone else. */
  reportsWithManager: number;
  /** People managers whose own Copilot use in the period is known: the denominator of `managersActivePct`. */
  managersStatusKnown: number;
  /** People managers whose own use the data cannot determine - never counted as "not active". */
  managersStatusUnknown: number;
  managersActive: number | null;
  managersActivePct: number | null;
  reportsManagerActive: number | null;
  reportsActiveRatePctManagerActive: number | null;
  reportsHabitRatePctManagerActive: number | null;
  reportsManagerInactive: number | null;
  reportsActiveRatePctManagerInactive: number | null;
  reportsHabitRatePctManagerInactive: number | null;
  reportsManagerUnknown: number;
  reportsManagerActiveLicensed: number | null;
  reportsActiveRatePctManagerActiveLicensed: number | null;
  reportsHabitRatePctManagerActiveLicensed: number | null;
  reportsManagerActiveUnlicensed: number | null;
  reportsActiveRatePctManagerActiveUnlicensed: number | null;
  reportsHabitRatePctManagerActiveUnlicensed: number | null;
  reportsManagerInactiveLicensed: number | null;
  reportsActiveRatePctManagerInactiveLicensed: number | null;
  reportsHabitRatePctManagerInactiveLicensed: number | null;
  reportsManagerInactiveUnlicensed: number | null;
  reportsActiveRatePctManagerInactiveUnlicensed: number | null;
  reportsHabitRatePctManagerInactiveUnlicensed: number | null;
}

/** The manager-modelling figures for one department of `adoptionByDepartment` - the report's own department. */
export interface ManagerModellingSegmentRow extends ManagerModellingFigures {
  /** Tenant data: rendered verbatim, never translated. */
  segment: string;
  licensedUsers: number;
  /**
   * Why a step of this department's figures is withheld when it has enough managers for it (#844): a reader without
   * See PII only, so that the whole population's figures less the departments shown never give back a department
   * withheld for having too few. Absent otherwise, and from older servers.
   */
  managersWithheld?: SmallGroupWithheldReason | null;
  comparisonWithheld?: SmallGroupWithheldReason | null;
  seatSplitWithheld?: SmallGroupWithheldReason | null;
}

export interface AdoptionProgressStep {
  step: string;
  state: 'pending' | 'running' | 'loaded' | 'failed' | 'skipped' | 'blocked';
  durationMs: number;
  queryElapsedMs?: number | null;
  failure?: string | null;
  reference?: string | null;
}

export interface AdoptionProgress {
  generationId: string;
  phase: string;
  elapsedMs: number;
  percentComplete: number | null;
  steps: AdoptionProgressStep[];
}

/**
 * The executive view.
 *
 * The manager-modelling figures (#641) are top-level, as the server sends them, and optional only so a
 * fixture or a server that predates them still type-checks.
 */
export interface CopilotAdoptionSummary extends Partial<ManagerModellingFigures> {
  diagnostics?: {
    generationId?: string;
    runId?: string | null;
    totalMs: number;
    steps: { step: string; durationMs: number; failed: boolean }[];
    progress?: AdoptionProgress;
  };
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
  reclaimCaveatKey?: string | null;
  reclaimSeatsHeldBackForWindowMismatch: number;
  reclaimSeatsHeldBackForReview: number;
  reclaimSeatsNoLongerHeld: number;
  reclaimSeatsFromActiveBands: number;
  usageReportSourcedUsers: number;
  usageReportSourcedUserPct: number;
  usageReportWindowMismatch: boolean;
  averageAdoptionScore: number;
  medianAdoptionScore: number;
  totalInteractions: number;

  /**
   * Microsoft's own tenant figures from its Microsoft 365 Copilot usage report (#642), exactly as the
   * report stated them: the report date and period they describe, the report version the import recorded,
   * the prompts submitted and the average per active user. A different source, unit and population from
   * every audit-derived figure here, so never added to them. Each is null - never zero - when the report did
   * not carry it (version 1) or none has been imported; optional because an older server does not send them.
   */
  microsoftReportDate?: string | null;
  microsoftReportPeriodDays?: number | null;
  microsoftReportVersion?: string | null;
  microsoftReportPromptsSubmitted?: number | null;
  microsoftReportAveragePromptsPerActiveUser?: number | null;

  // Cowork use, from the Copilot audit log only: interactions, never tasks (#692).
  coworkUsers: number;
  coworkAdoptionPct: number | null;
  coworkEligibilityKnown: boolean;
  coworkEligibleUsers: number | null;
  coworkAuditUsers: number;
  coworkInteractions: number;
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
  /**
   * The same model over the people WITHOUT a Copilot seat - every licence candidate: what Cowork could
   * add for them once licensed and enabled. Cowork needs a Copilot licence, and the time that licence
   * would give back is the licence estimate - never added to this. Absent from an older server.
   */
  coworkWithoutLicenceEstimate?: CoworkValueEstimate;

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
  /**
   * The same model over every licence candidate, recommended or not - the headline's "all candidates"
   * option, and the only figure there is when nobody is recommended. Optional only so older fixtures
   * still type-check; the server always sends it.
   */
  licenceAllCandidatesEstimate?: LicenceValueEstimate;
  seatHolderTimeSavedEstimate?: SeatHolderTimeSavedEstimate;

  funnel: ReportCategory[];
  bandBreakdown: ReportCategory[];
  habitBuckets: AdoptionHabitBucket[];
  intensityByDepartment: AdoptionIntensityPoint[];
  actionPlan: AdoptionActionSummary[];
  guidanceCatalogueVersion?: string;
  guidanceLinks?: AdoptionGuidanceLink[];
  adoptionByDepartment: AdoptionSegmentRow[];
  /**
   * The manager-modelling figures for each department of `adoptionByDepartment`, in its order (#641).
   * Absent from a server that predates them.
   */
  managerModellingByDepartment?: ManagerModellingSegmentRow[];
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
   * Sections that stayed tenant-wide while `scopedEmailDomain` or `userFilter` is set, because they
   * come from aggregate queries carrying no per-user identity. Values are the constants in
   * {@link UNSCOPED_SECTIONS}; render each as a "tenant-wide" badge rather than hiding it.
   */
  unscopedSections: string[];
  /**
   * The user filter every figure above was narrowed by - Entra ID attributes and custom
   * organisations - echoed back by the server, or null/absent for none. Optional so a server that
   * predates the filter still type-checks.
   */
  userFilter?: UserFilterEcho | null;
  /**
   * The administrator's global report filter as it applied to this reader - echoed apart from their own
   * filter, so the page shows it locked. Null/absent when none applied.
   */
  globalFilter?: GlobalFilterEcho | null;
  /** The tenant-wide Copilot seat count, sent only when the summary is narrowed. */
  unscopedLicensedUsers?: number | null;
  /**
   * The sections left out because the administrator's report filter applies - the same names as
   * `unscopedSections`, which they describe the whole organisation for. Show each as not available under
   * the filter, never as empty. Absent from a server that predates it.
   */
  withheldSections?: string[];
  accountabilityDimension: string | null;
  accountabilityDimensionLabel: string | null;
  accountabilityRollup: AccountabilityRollupRow[];
  /** The leadership group's aggregate comparison with the tenant (#654). Absent from an older server. */
  leadershipComparison?: LeadershipAdoptionComparison | null;
  usageByApp: ReportCategory[];
  opportunityByDepartment: ReportCategory[];
  /**
   * Set when `opportunityByDepartment` leaves out a department with enough candidates to show, for a reader without
   * See PII: with the tenant's recommended candidates, its count would give back a smaller department's (#844).
   */
  opportunityByDepartmentWithheld?: SmallGroupWithheldReason | null;
  /**
   * The breakdowns that, for a reader without See PII, left out their smallest listed group from every view of them as
   * well as the groups too small to list, so that the tenant's figures less the groups shown cannot give back a small one
   * (#844). Absent when none did.
   */
  smallestGroupsWithheld?: WithheldBreakdown[] | null;
  weeklyTrend: ReportSeries[];
  weeklyVolumeTrend: ReportSeries[];
  scoreProfiles: AdoptionScoreProfile[];
  concentration: AdoptionConcentrationBand[];
  combinedByDepartment: AdoptionCombinedSegmentRow[];
  topResourceTypes: AdoptionResourceTypeRow[];
  agents: AgentEstateSummary;
  /**
   * The heaviest agent users, heaviest first. Empty for a reader without the See PII permission, and
   * absent from a server that predates it.
   */
  topAgentUsers?: AgentUserRow[];
  /** The people that list was picked from stopped at the server's row cap, so a filtered view's list may be short. */
  topAgentUsersCapped?: boolean;
  /**
   * Which agents the breadth, depth and reach figures count (#646, #647): a stable key the page words in
   * the reader's language - see `agentFiguresScopeText`.
   */
  agentFiguresScope?: string | null;
  /** Tenant-level agent breadth and depth (#646), builders and reach (#647). Null when not measured. */
  agentBreadthDepartments?: number | null;
  agentBreadthDepartmentsWithAgentUsers?: number | null;
  agentBreadthDepartmentPct?: number | null;
  agentActiveUsers?: number | null;
  agentBreadthAgentUsers?: number | null;
  agentBreadthUserPct?: number | null;
  agentDepthDistinctAgents?: number | null;
  agentDepthAgentsPer100ActiveUsers?: number | null;
  agentDepthInteractions?: number | null;
  agentDepthInteractionsPerActiveAgent?: number | null;
  /** Agents of unknown origin the active users used in the period - not counted, so the figures above are a floor. */
  agentUnknownOriginAgents?: number | null;
  agentBuilders?: number | null;
  agentsInThreeOrMoreDepartments?: number | null;
  /** Inventory agents of unknown origin used in three or more departments - not counted in the figure above. */
  agentsInThreeOrMoreDepartmentsUnknownOrigin?: number | null;
  unlicensed: UnlicensedPopulationSummary;

  options: CopilotAdoptionOptions;
  warnings: string[];
  warningDetails?: CopilotAdoptionWarningDetail[];

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
  /**
   * True when the person's Copilot use is confined to at most half the configured breadth target's apps - what
   * makes the server advise adding another app. Optional only so fixtures written before it still type-check.
   */
  needsBroadening?: boolean;
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
  warningDetails?: CopilotAdoptionWarningDetail[];
}

export interface LicenceOpportunityPage {
  total: number;
  skip: number;
  take: number;
  rows: LicenceOpportunityRow[];
  warnings: string[];
  warningDetails?: CopilotAdoptionWarningDetail[];
}

export interface CopilotAdoptionWarningDetail {
  key: string;
  values?: Record<string, string | number | boolean | null>;
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
  microsoftReport: 'microsoftReport',
} as const;

/** Filter/sort state for the licensed-user list. */
export interface LicensedUserFilters {
  search: string;
  bands: AdoptionBand[];
  /** Recommended-action codes to restrict to. Drives the drill-through from the enablement plan. */
  actions: string[];
  /**
   * The page-wide user filter in its wire form (`serializeUserFilter`), or empty for none - who the
   * list is about, from Entra ID attributes, email domain and custom organisations alike. Held on the
   * filter object rather than passed separately so the list, its CSV export and the summary above it
   * can never drift apart about which population they describe.
   */
  userFilter?: string;
  reclaimEligibility: string;
  coworkOnly: boolean;
  disabledOnly: boolean;
  sortBy: string;
  sortDesc: boolean;
}

/** Filter/sort state for the licence-opportunity list. */
export interface OpportunityFilters {
  search: string;
  /** The page-wide user filter in its wire form, or empty for none. */
  userFilter?: string;
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
  coworkAdoptionPct: number;
  averageCoordinationLoad: number;
  averageFluency: number;
}

/**
 * The tenant's Copilot Studio credit-capacity snapshot.
 *
 * This is not Cowork funding, usage or spend. Manage Cowork billing separately in the Microsoft 365
 * admin center under Copilot > Cost management.
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
 * The kinds of work the Cowork estimate models: each thing Microsoft says Cowork does, against the count
 * Microsoft's usage reports keep of people doing it by hand. In the order the server publishes and sums
 * them - see `COWORK_ACTIVITIES`.
 */
export type CoworkActivity = 'organiseMeetings' | 'prepareMeetings' | 'sendEmail' | 'postInTeams' | 'createDocuments';

/** What one cohort already does by hand of one kind of work, a month. Observed, not modelled. */
export interface CoworkActivityVolume {
  activity: CoworkActivity;
  /** Done by hand a month by the people the estimate covers. */
  volumePerMonth: number;
}

/**
 * The modelled Cowork estimate for one cohort: the time Cowork could give back ON TOP of what Microsoft
 * 365 Copilot already saves - the value of enabling Cowork, paid for in Copilot Credits.
 *
 * Each kind of work the cohort already does by hand x the share of it handed to Cowork x the minutes
 * saved on each piece. Everyone is modelled this way, including people already using Cowork. The volumes
 * are observed; the shares and minutes are assumptions no study has tested. There is deliberately no
 * Copilot layer: these people already hold a licence, and the time it gives back is not Cowork's to claim.
 *
 * `assumptions` travels with the numbers so no component can render a figure without it. The portal
 * recomputes the hours from the published inputs whenever the reader enters their own assumptions -
 * see `components/copilotAdoption/coworkTimeSaved.ts`.
 */
export interface SeatHolderTimeSavedCredits {
  outlookMinutesPerAction: number;
  officeMinutesPerAction: number;
  teamsMeetingMinutesPerAction: number;
  uncreditedMinutesPerAction: number;
  lowerBoundRatio: number;
}

export interface SeatHolderTimeSavedSegment {
  segment: string;
  cohortUsers: number;
  observedOutlookActions: number;
  observedOfficeActions: number;
  observedTeamsMeetingActions: number;
  observedUncreditedActions: number;
  hoursPerMonthLow: number;
  hoursPerMonthHigh: number;
}

export interface SeatHolderTimeSavedEstimate {
  isModelled: boolean;
  cohortUsers: number;
  excludedUsageReportSourcedUsers: number;
  observedOutlookActions: number;
  observedOfficeActions: number;
  observedTeamsMeetingActions: number;
  observedUncreditedActions: number;
  credits: SeatHolderTimeSavedCredits;
  hoursPerMonthLow: number;
  hoursPerMonthHigh: number;
  assumptions: string[];
  byBand: SeatHolderTimeSavedSegment[];
  byDepartment: SeatHolderTimeSavedSegment[];
}

export interface CoworkValueEstimate {
  isModelled: boolean;
  cohortUsers: number;
  /** The work the cohort does by hand, a month, one entry per kind - every kind present, in `COWORK_ACTIVITIES` order. Observed. */
  activities: CoworkActivityVolume[];
  /** Pieces of work a month handed to Cowork: each volume x its share, summed, then rounded. Modelled. */
  projectedCoworkTasks: number;
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

  // Cowork use, from the Copilot audit log only: interactions, never tasks (#692).
  coworkInteractions: number;
  coworkActiveDays: number;
  lastCoworkInteractionUtc: string | null;
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
  warningDetails?: CopilotAdoptionWarningDetail[];
}

/** Filter/sort state for the Cowork readiness list. */
export interface CoworkFilters {
  search: string;
  tiers: CoworkTier[];
  /** The page-wide user filter in its wire form, or empty for none. */
  userFilter?: string;
  recommendedOnly: boolean;
  coworkUsersOnly: boolean;
  sortBy: string;
  sortDesc: boolean;
}
