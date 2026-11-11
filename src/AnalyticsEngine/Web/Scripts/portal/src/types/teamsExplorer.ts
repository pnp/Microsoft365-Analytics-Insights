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
  /**
   * Present when the section failed. The section renders it instead of a chart, in English: for a query that ran and
   * failed, the reason and an error reference, never the exception's text; for a section the server did not run, the
   * fallback for `errorCode`.
   */
  error: string | null;
  /**
   * A stable code when the server itself decided the section could not run (`error` is then only its English
   * fallback); absent for a query that ran and failed, which carries `errorReason` and `errorReference` instead.
   */
  errorCode?: string | null;
  /** The failure's stable reason (`ReportQueryFailure.Reason`), worded by `reportQueryFailureText`. */
  errorReason?: string | null;
  /** The error reference the server log carries for this failure. */
  errorReference?: string | null;
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
  /** Null for the combined `other` row. */
  key: string | null;
  /** Null for the combined `other` row. */
  label: string | null;
  count: number;
  sharePct: number;
  /** The one row a reader without See PII gets in place of every bucket too few people are behind. */
  other?: boolean;
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
  /** Null when `withheld`. */
  users: number | null;
  /**
   * Null when `withheld`, and on every slice when, for a reader without See PII, any slice is - a share of the measured
   * total would give the withheld slice back.
   */
  sharePct: number | null;
  /**
   * For a reader without See PII: one to four people are in the segment, or it is the segment shown beside such a one so
   * that it cannot be worked out from the active users. Its figures are null - withheld, never 0.
   */
  withheld?: boolean;
};

/** One week of the Overview trend - or, with only `activeUsers`, of the Teams & channels membership trend. */
export type TeamsTrendPoint = {
  weekStart: string;
  /** Null when `withheld`. */
  activeUsers: number | null;
  /** Null when `withheld`. */
  channelMessages: number | null;
  /** Null when `withheld`. */
  privateMessages: number | null;
  /** Null when `withheld`. */
  meetingsAttended: number | null;
  /** Null for a reader without See PII: see `TeamsOverview.weeklyCallsWithheld`. */
  calls: number | null;
  /**
   * For a reader without See PII: one to four people were active in the week, or it is the week withheld beside such a
   * one so that it cannot be worked out from the period's totals.
   */
  withheld?: boolean;
};

/**
 * The Overview's headline figures. The usage-report figures - everything but the calls, teams and channels - are null for
 * a reader without See PII when one to four people were active, or are in the directory (`TeamsOverview.usageWithheld`).
 */
export type TeamsOverviewKpis = {
  knownUsers: number | null;
  activeUsers: number | null;
  reachPct: number | null;
  channelMessages: number | null;
  privateMessages: number | null;
  openCollaborationPct: number | null;
  meetingsAttended: number | null;
  meetingsOrganised: number | null;
  meetingsPerActiveUser: number | null;
  /**
   * Audio hours. Used as the wall-clock proxy because audio, video and screenshare durations
   * overlap within one meeting - they must never be added together.
   */
  audioHours: number | null;
  videoSharePct: number | null;
  screenShareSharePct: number | null;
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
  /**
   * For a reader without See PII, the fewest people a figure here describes: the figures count only the people the
   * Adoption breakdown is made of, and leave out groups smaller than this. Null or absent otherwise.
   */
  minimumGroupSize?: number | null;
  /** For a reader without See PII: one to four people were active, so every usage-report KPI is withheld. */
  usageWithheld?: boolean;
  /** For a reader without See PII: the trend's weekly calls are withheld (the Meetings tab floors them). */
  weeklyCallsWithheld?: boolean;
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
  /**
   * Licensed people with no activity left out of the engagement mix because dormancy is not known. Null when, for a reader
   * without See PII, it is one to four people - like a dormant segment of that few.
   */
  unmeasuredUsers: number | null;
};

/** Each figure is null for a reader without See PII when it would describe one to four people. */
export type TeamsAdoptionRhythm = {
  meanDailyActiveUsers: number | null;
  weeklyActiveUsers: number | null;
  monthlyActiveUsers: number | null;
  stickinessPct: number | null;
};

export type TeamsSegmentTrendPoint = {
  weekStart: string;
  /** Null when `withheld`. */
  power: number | null;
  /** Null when `withheld`. */
  regular: number | null;
  /** Null when `withheld`. */
  light: number | null;
  /** For a reader without See PII: one of the week's segments had one to four people, so the week is withheld whole. */
  withheld?: boolean;
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
  /** Null when `withheld`. */
  users: number | null;
  /** Shares intentionally sum above 100: one person uses several platforms. Null when any platform is withheld. */
  sharePct: number | null;
  /**
   * For a reader without See PII: one to four people used the platform - or, for "Mobile (any)", iOS or Android is
   * withheld, which it would bound.
   */
  withheld?: boolean;
};

/** The three counts add up to the period's active people; for a reader without See PII, one of one to four is null. */
export type TeamsLifecycle = {
  newUsers: number | null;
  returningUsers: number | null;
  lapsedUsers: number | null;
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
   * For a reader without See PII, the fewest people - and active people - a figure here describes: the breakdown folds
   * smaller groups, and the rest of the tab counts only the people the breakdown is made of and withholds what fewer are
   * behind. Null or absent when every group is shown.
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
  /** Null when `withheld`. */
  calls: number | null;
  /** Null when `withheld`. */
  minutes: number | null;
  /** Null when `withheld`. */
  attendees: number | null;
  /**
   * For a reader without See PII: one to four people attended the week's calls, or it is the quietest other week, withheld
   * with it so that neither can be worked out from the totals; its figures are null.
   */
  withheld?: boolean;
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
  /** The fewest people a figure here describes, for a reader without See PII; null or absent otherwise. */
  minimumGroupSize?: number | null;
  /** For a reader without See PII: fewer than `minimumGroupSize` people attended, so the distributions are empty. */
  distributionsWithheld?: boolean;
  /** Modalities combined into the `other` row, or left out when too few people used even all of them. */
  otherModalities?: number;
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
  /** Null when `reactionsWithheld`. */
  reactions: number | null;
  /** For a reader without See PII: one to four people reacted, so the reactions are withheld. */
  reactionsWithheld?: boolean;
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
  /** Null when `reactionsWithheld`. */
  reactions: number | null;
  /** Distinct people who reacted - the only per-user, per-channel signal the schema stores. Null when `reactionsWithheld`. */
  reactingUsers: number | null;
  /** For a reader without See PII: one to four people reacted, so the reactions are withheld. */
  reactionsWithheld?: boolean;
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
  /** The fewest people a row here describes, for a reader without See PII; null or absent otherwise. */
  minimumGroupSize?: number | null;
  /** Teams left out of `teams` for a reader without See PII: fewer than `minimumGroupSize` members. */
  withheldTeams?: number;
  /** Channels left out of `channels` for a reader without See PII: their team has fewer than `minimumGroupSize` members. */
  withheldChannels?: number;
  /** Reactions combined into the `other` row of `reactionMix`, or left out when too few people used even all of them. */
  otherReactions?: number;
  /** For a reader without See PII: the tab list is withheld. */
  tabUsageWithheld?: boolean;
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
  /** The fewest people a row here describes, for a reader without See PII; null or absent otherwise. */
  minimumGroupSize?: number | null;
  /** For a reader without See PII: the key phrases are withheld. */
  keywordsWithheld?: boolean;
  /** For a reader without See PII: the languages are withheld. */
  languagesWithheld?: boolean;
  /** Teams left out of `sentimentByTeam` for a reader without See PII: fewer than `minimumGroupSize` members. */
  withheldSentimentTeams?: number;
  /** Channels left out of `sentimentByChannel` for a reader without See PII: their team has too few members. */
  withheldSentimentChannels?: number;
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
