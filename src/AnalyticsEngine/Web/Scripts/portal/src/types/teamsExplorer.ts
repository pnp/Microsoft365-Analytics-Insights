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
  teamsAnalyticsAvailable: boolean;
  cognitiveAvailable: boolean;
  userMetadataAvailable: boolean;
  authorisedTeams: number;
  totalTeams: number;
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
  name: string;
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
  championsByDepartment: TeamsNamedCount[];
};

/** Exportable sections, matching `TeamsExplorerExports.Sections`. */
export type TeamsExportSection = 'people' | 'dormant' | 'teams' | 'channels' | 'adoption';
