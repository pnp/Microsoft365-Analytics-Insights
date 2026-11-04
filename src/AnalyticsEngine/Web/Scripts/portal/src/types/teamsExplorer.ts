/**
 * Types for the Teams Explorer API (`api/TeamsExplorer`).
 *
 * These mirror the C# models in `Common/Entities/TeamsExplorer/TeamsExplorerModels.cs`, which are
 * serialised with a camel-case naming strategy.
 */

/** One executed query, so a section can show its SQL and report its own failure. */
export type TeamsQueryInfo = {
  key: string;
  sql: string;
  /** Present when the section failed. The section renders this instead of a chart. */
  error: string | null;
  elapsedMs: number;
};

/**
 * The reporting window, in the two forms the page needs.
 *
 * `usageFromUtc`/`usageToUtc` end a few days earlier than `fromUtc`/`toUtc`: Microsoft 365 usage
 * reports are published in arrears, so the usage-report figures deliberately cover an earlier
 * window than the call figures. The UI states this rather than quietly comparing the two.
 */
export type TeamsWindow = {
  days: number;
  fromUtc: string;
  toUtc: string;
  usageFromUtc: string;
  usageToUtc: string;
  /** Working days in the window - the denominator behind the engagement segments. */
  workingDays: number;
};

/** Common shape for every tab's payload. */
export type TeamsSection = {
  window: TeamsWindow;
  queries: TeamsQueryInfo[];
};

export type TeamsNamedCount = {
  name: string;
  count: number;
  sharePct: number | null;
};

export type TeamsBucket = {
  key: string;
  label: string;
  count: number;
  sharePct: number;
};

export type TeamsAvailability = {
  usageReportsAvailable: boolean;
  callsAvailable: boolean;
  serviceBusAvailable: boolean;
  teamsAnalyticsAvailable: boolean;
  cognitiveAvailable: boolean;
  userMetadataAvailable: boolean;
  authorisedTeams: number;
  totalTeams: number;
  /**
   * Whether the team counts above were actually read.
   *
   * They are plain numbers, with a count the server could not produce collapsed to 0, so this is
   * the only way to tell "no teams discovered" from "the count query did not return". Treating the
   * two alike tells an administrator their import has found nothing when it was never asked.
   */
  teamCountsKnown: boolean;
  available: boolean;
  reasons: string[];
};

export type TeamsJudgement = {
  key: string;
  tone: 'good' | 'warning' | 'critical' | 'neutral';
  headline: string;
  detail: string;
};

export type TeamsSegmentSlice = {
  segment: 'Power' | 'Regular' | 'Light' | 'Dormant';
  label: string;
  description: string;
  users: number;
  sharePct: number;
};

export type TeamsTrendPoint = {
  weekStart: string;
  activeUsers: number;
  channelMessages: number;
  privateMessages: number;
  meetingsAttended: number;
  calls: number;
};

export type TeamsOverviewKpis = {
  knownUsers: number;
  activeUsers: number;
  reachPct: number;
  channelMessages: number;
  privateMessages: number;
  openCollaborationPct: number;
  meetingsAttended: number;
  meetingsOrganised: number;
  meetingsPerActiveUser: number;
  /**
   * Audio hours. Used as the wall-clock proxy because audio, video and screenshare durations
   * overlap within one meeting - they must never be added together.
   */
  audioHours: number;
  videoSharePct: number;
  screenShareSharePct: number;
  calls: number;
  totalTeams: number;
  activeTeams: number;
  totalChannels: number;
  activeChannels: number;
};

export type TeamsOverview = TeamsSection & {
  kpis: TeamsOverviewKpis;
  trend: TeamsTrendPoint[];
  segmentMix: TeamsSegmentSlice[];
  judgements: TeamsJudgement[];
  /** Whether the engagement mix's Dormant share could be measured. Absent from an older API. */
  dormancy?: TeamsDormancy;
};

/** Why a licensed person with no Teams activity can or cannot be called dormant (mirrors TeamsDormancy). */
export type TeamsDormancyReason =
  | 'incompleteImport'
  | 'groupFiltered'
  | 'namesConcealed'
  | 'noTeamsLicences'
  | 'notChecked';

/**
 * Whether the usage reports can prove somebody did nothing in Teams in the window. They list only the people
 * active on each day, so a licensed person with no activity is dormant only when every day was imported, the
 * import covers everyone and the reports carry real names. When not, such people are left out of the Dormant
 * segment and list and counted in `unmeasuredUsers`.
 */
export type TeamsDormancy = {
  known: boolean;
  /** A stable key, null when `known`; the page writes the sentence. */
  reason: TeamsDormancyReason | string | null;
  expectedDays: number;
  importedDays: number;
  teamsLicensedUsers: number;
  /** Licensed people with no activity left out of the engagement mix because dormancy is not known. */
  unmeasuredUsers: number;
};

export type TeamsAdoptionRhythm = {
  meanDailyActiveUsers: number;
  weeklyActiveUsers: number;
  monthlyActiveUsers: number;
  stickinessPct: number;
};

export type TeamsSegmentTrendPoint = {
  weekStart: string;
  power: number;
  regular: number;
  light: number;
};

export type TeamsDemographicRow = {
  /** Tenant data, never translated. Null for the combined `other` row. */
  name: string | null;
  /** True for the single row a reader without See PII gets in place of every group too small to name. */
  other?: boolean;
  knownUsers: number;
  activeUsers: number;
  reachPct: number;
  messagesPerActiveUser: number;
  meetingsPerActiveUser: number;
};

export type TeamsDeviceRow = {
  platform: string;
  users: number;
  /** Shares intentionally sum above 100: one person uses several platforms. */
  sharePct: number;
};

export type TeamsLifecycle = {
  newUsers: number;
  returningUsers: number;
  lapsedUsers: number;
};

export type TeamsGrouping = 'department' | 'country' | 'office' | 'jobTitle' | 'company';

export type TeamsAdoption = TeamsSection & {
  groupBy: TeamsGrouping;
  rhythm: TeamsAdoptionRhythm;
  segmentMix: TeamsSegmentSlice[];
  segmentTrend: TeamsSegmentTrendPoint[];
  breakdown: TeamsDemographicRow[];
  devices: TeamsDeviceRow[];
  lifecycle: TeamsLifecycle;
  /** Whether the segment mix's Dormant share could be measured. Absent from an older API. */
  dormancy?: TeamsDormancy;
  /**
   * For a reader without See PII, the fewest people - and active people - a breakdown row describes. Null or
   * absent when every group is shown.
   */
  minimumGroupSize?: number | null;
  /**
   * How many groups were combined into the `other` row - or, when even all of them together are too few people,
   * left out of the breakdown. 0 or absent when none were.
   */
  otherGroups?: number;
};

export type TeamsCallKpis = {
  calls: number;
  groupCalls: number;
  peerToPeerCalls: number;
  attendees: number;
  callHours: number;
  attendeeHours: number;
  meanDurationMinutes: number;
  meanAttendees: number;
  afterHoursPct: number;
  weekendPct: number;
  organiserConcentrationPct: number;
  attendeeEngagementPct: number;
};

export type TeamsCallTrendPoint = {
  weekStart: string;
  calls: number;
  minutes: number;
  attendees: number;
};

export type TeamsHeatCell = {
  /** 0 = Monday ... 6 = Sunday. */
  dayOfWeek: number;
  /** UTC hour, 0-23. */
  hour: number;
  calls: number;
};

export type TeamsCallQuality = {
  feedbackCount: number;
  failureCount: number;
  ratings: TeamsBucket[];
  failureReasons: TeamsBucket[];
  failureStages: TeamsBucket[];
};

export type TeamsMeetings = TeamsSection & {
  workingDayStartHour: number;
  workingDayEndHour: number;
  kpis: TeamsCallKpis;
  trend: TeamsCallTrendPoint[];
  sizeDistribution: TeamsBucket[];
  durationDistribution: TeamsBucket[];
  heatmap: TeamsHeatCell[];
  periodOfDay: TeamsBucket[];
  modalityMix: TeamsBucket[];
  topOrganisers: TeamsNamedCount[];
  topAttendees: TeamsNamedCount[];
  quality: TeamsCallQuality;
};

export type TeamsCollaborationKpis = {
  totalTeams: number;
  activeTeams: number;
  dormantTeams: number;
  ownerlessTeams: number;
  authorisedTeams: number;
  totalChannels: number;
  activeChannels: number;
  channelMessages: number;
  reactions: number;
};

export type TeamsTeamRow = {
  id: number;
  name: string;
  members: number;
  owners: number;
  channels: number;
  tabs: number;
  messages: number;
  reactions: number;
  /** 0 negative, 0.5 NEUTRAL, 1 positive - chat-count weighted. Never render as a percentage. */
  sentiment: number | null;
  activeDays: number;
  /** False means "not measured", not "quiet" - the team was never authorised. */
  authorised: boolean;
};

export type TeamsChannelRow = {
  id: number;
  name: string;
  teamName: string;
  tabs: number;
  messages: number;
  reactions: number;
  /** Distinct people who reacted - the only per-user, per-channel signal the schema stores. */
  reactingUsers: number;
  sentiment: number | null;
  activeDays: number;
};

export type TeamsCollaboration = TeamsSection & {
  kpis: TeamsCollaborationKpis;
  teams: TeamsTeamRow[];
  channels: TeamsChannelRow[];
  ownerlessTeams: TeamsNamedCount[];
  dormantTeams: TeamsNamedCount[];
  reactionMix: TeamsBucket[];
  tabUsage: TeamsNamedCount[];
  membershipTrend: TeamsTrendPoint[];
};

export type TeamsSentimentPoint = {
  weekStart: string;
  sentiment: number | null;
  messages: number;
};

export type TeamsSentimentRow = {
  name: string;
  sentiment: number;
  messages: number;
};

export type TeamsConversations = TeamsSection & {
  cognitiveAvailable: boolean;
  scoredChannelDays: number;
  keywords: TeamsNamedCount[];
  languages: TeamsNamedCount[];
  sentimentTrend: TeamsSentimentPoint[];
  sentimentByTeam: TeamsSentimentRow[];
  sentimentByChannel: TeamsSentimentRow[];
};

export type TeamsPersonRow = {
  userPrincipalName: string;
  department: string | null;
  activeDays: number;
  channelMessages: number;
  privateMessages: number;
  meetingsOrganised: number;
  meetingsAttended: number;
  callsHosted: number;
  callsAttended: number;
  segment: string;
  lastActivity: string | null;
};

export type TeamsPeople = TeamsSection & {
  /** True when the usage reports appear to be anonymised, so no one can be named. */
  namesObfuscated: boolean;
  champions: TeamsPersonRow[];
  dormant: TeamsPersonRow[];
  championsByDepartment: TeamsDepartmentCount[];
  /** Whether `dormant` can include licensed people with no Teams activity at all. Absent from an older API. */
  dormancy?: TeamsDormancy;
  /**
   * For a reader without See PII, the fewest power users a department row counts. Null or absent when every
   * department is shown.
   */
  minimumGroupSize?: number | null;
  /** How many departments were combined into the `other` row - or left out when too few even together. */
  otherChampionDepartments?: number;
};

/** Power users in one department. `other` marks the combined row a reader without See PII gets; its name is null. */
export type TeamsDepartmentCount = {
  name: string | null;
  count: number;
  sharePct: number | null;
  other?: boolean;
};

/** Exportable sections, matching `TeamsExplorerExports.Sections`. */
export type TeamsExportSection = 'people' | 'dormant' | 'teams' | 'channels' | 'adoption';
