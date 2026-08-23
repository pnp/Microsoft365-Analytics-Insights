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

/** Which imports supplied the data, so no headline number is quoted without its caveats. */
export interface AdoptionDataSources {
  auditAvailable: boolean;
  copilotUsageReportAvailable: boolean;
  m365UsageReportsAvailable: boolean;
  userMetadataAvailable: boolean;
  copilotUsageReportDate: string | null;
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

/** Every threshold and weight the adoption maths used, echoed back so a figure can be traced to its rule. */
export interface CopilotAdoptionOptions {
  windowDays: number;
  historyDays: number;
  workingDaysPerWeek: number;
  frequencyTargetRatio: number;
  depthTargetInteractionsPerActiveDay: number;
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
  agentMinUsers: number;
  agentHistoryDays: number;

  opportunityUnlicensedCopilotWeight: number;
  opportunityCollaborationWeight: number;
  opportunityEmailWeight: number;
  opportunityDocumentWeight: number;
  opportunityCopilotTarget: number;
  opportunityCollaborationTarget: number;
  opportunityEmailTarget: number;
  opportunityDocumentTarget: number;
  opportunityRecommendScore: number;

  usageReportLagDays: number;
  topSegments: number;
  minSeatsPerSegment: number;
  maxAgents: number;
  maxUnlicensedUsersScored: number;
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
  averageAdoptionScore: number;
  medianAdoptionScore: number;
  totalInteractions: number;

  coworkUsers: number;
  coworkAdoptionPct: number;
  coworkInteractions: number;
  coworkDetected: boolean;

  unlicensedActiveUsers: number;
  recommendedForLicence: number;

  funnel: ReportCategory[];
  bandBreakdown: ReportCategory[];
  habitBuckets: AdoptionHabitBucket[];
  intensityByDepartment: AdoptionIntensityPoint[];
  actionPlan: AdoptionActionSummary[];
  adoptionByDepartment: AdoptionSegmentRow[];
  adoptionByCountry: AdoptionSegmentRow[];
  usageByApp: ReportCategory[];
  opportunityByDepartment: ReportCategory[];
  weeklyTrend: ReportSeries[];
  weeklyVolumeTrend: ReportSeries[];
  scoreProfiles: AdoptionScoreProfile[];
  concentration: AdoptionConcentrationBand[];
  combinedByDepartment: AdoptionCombinedSegmentRow[];
  topResourceTypes: ReportCategory[];
  agents: AgentEstateSummary;
  unlicensed: UnlicensedPopulationSummary;

  options: CopilotAdoptionOptions;
  warnings: string[];
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
  seatLicences: string | null;

  interactions: number;
  activeDays: number;
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
  departments: string[];
  countries: string[];
  bands: { value: number; name: string }[];
}

/** Filter/sort state for the licensed-user list. */
export interface LicensedUserFilters {
  search: string;
  bands: AdoptionBand[];
  department: string;
  country: string;
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
  recommendedOnly: boolean;
  existingCopilotUsersOnly: boolean;
  sortBy: string;
  sortDesc: boolean;
}
