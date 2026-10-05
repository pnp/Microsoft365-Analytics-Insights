import { useCallback, useMemo, useSyncExternalStore } from 'react';
import type {
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  CoworkActivity,
  CoworkValueEstimate,
  LicenceValueEstimate,
  SeatHolderTimeSavedEstimate,
  SeatHolderTimeSavedSegment,
} from '../../types/copilotAdoption';
import { formatNumber, type TFunction } from '../../i18n';

/**
 * The two time-saved models, and the reader's own assumptions.
 *
 * <b>Nothing here is measured.</b> The volumes come from Microsoft's usage reports and are real; the
 * minutes saved on each are assumptions, and every figure this module returns is those two
 * multiplied together. The server authors the same arithmetic, and the two are kept identical on
 * purpose - an uncustomised page must show exactly the hours the Excel report does, to the hour.
 *
 * <b>Each model sits beside the decision it justifies, and they are never added together.</b>
 *
 * - The LICENCE estimate (`projectLicenceTimeSaved`, server twin
 *   `CopilotAdoptionScoring.ModelLicenceValue`): the meetings, emails and documents of the people
 *   recommended for a Microsoft 365 Copilot licence, times the minutes Copilot is assumed to save on
 *   each. The minutes are evidenced by published Copilot studies, the largest of which randomised who
 *   received a licence - the decision this figure sizes. Shown on the Licence opportunities tab.
 * - The COWORK estimate (`projectCoworkTimeSaved`, server twin `CopilotAdoptionScoring.ModelCoworkValue`):
 *   each kind of work the cohort already does by hand (`COWORK_ACTIVITIES`) times the share of it
 *   handed to Cowork times the minutes Cowork saves on each piece, ON TOP of Copilot. Everyone is
 *   modelled this way, including people already using Cowork: the Copilot audit log counts their
 *   interactions, not work handed over, and Microsoft's Cowork task counts are not imported (#692).
 *   The value of enabling Cowork for people who already hold a licence, paid for in Copilot Credits.
 *   No study has measured it, and every surface says so. Shown on the Cowork tab.
 *
 * - The SEAT-HOLDER estimate (`projectSeatHolderTimeSaved`, server twin
 *   `CopilotAdoptionScoring.ModelSeatHolderTimeSaved`): the Copilot actions already observed from
 *   people who hold a licence, credited only where a published Microsoft method gives this product a
 *   defensible unit. It is shown as hours already saved, never money, and is not added to either
 *   decision estimate above.
 *
 * The reader can replace any assumption with their own figure. Those figures are kept in this
 * browser tab's session storage and nowhere else: they are never written to the database, so one
 * admin's view of the model cannot become another's report.
 */

/** The three kinds of observed work the licence estimate converts into time. */
export type TimeSavedActivity = 'meetings' | 'email' | 'documents';

export const TIME_SAVED_ACTIVITIES: readonly TimeSavedActivity[] = ['meetings', 'email', 'documents'];

/**
 * The kinds of work Cowork could take on, in the order the server publishes and SUMS them
 * (`CoworkActivities.All`). Summing in the same order is what keeps the two floating-point totals - and
 * so the hours - identical.
 */
export const COWORK_ACTIVITIES: readonly CoworkActivity[] = [
  'organiseMeetings',
  'prepareMeetings',
  'sendEmail',
  'postInTeams',
  'createDocuments',
];

export interface TimeSavedAssumptions {
  /** Minutes Copilot saves per Teams meeting attended. Licence estimate. */
  meetingMinutes: number;
  /** Minutes Copilot saves per email sent or read. Licence estimate. */
  emailMinutes: number;
  /** Minutes Copilot saves per SharePoint or OneDrive document viewed or edited. Licence estimate. */
  documentMinutes: number;
  /** Minutes credited to one Outlook Copilot action by the already-licensed estimate. */
  seatOutlookMinutes: number;
  /** Minutes credited to one Word/PowerPoint/Excel Copilot action by the already-licensed estimate. */
  seatOfficeMinutes: number;
  /** Minutes credited to one Teams meeting recap/summarise action by the already-licensed estimate. */
  seatMeetingMinutes: number;
  /** Minutes credited to Copilot Chat and other uncredited actions by the already-licensed estimate. */
  seatUncreditedMinutes: number;
  /** For each kind of work Cowork could take on: the share handed to it (0-1), and the minutes saved on each piece. */
  organiseMeetingsShare: number;
  organiseMeetingsMinutes: number;
  prepareMeetingsShare: number;
  prepareMeetingsMinutes: number;
  sendEmailShare: number;
  sendEmailMinutes: number;
  postInTeamsShare: number;
  postInTeamsMinutes: number;
  createDocumentsShare: number;
  createDocumentsMinutes: number;
  /** Share of the assumptions the conservative end of the range applies, from 0 to 1. Both. */
  conservativeRatio: number;
  /** Working hours in a day. Used only to restate the hours as full-time capacity. Both. */
  hoursPerDay: number;
}

export type TimeSavedAssumptionKey = keyof TimeSavedAssumptions;

/** The two assumptions each kind of work carries, and the server options they restate. */
export const COWORK_ACTIVITY_ASSUMPTIONS: Record<
  CoworkActivity,
  {
    share: TimeSavedAssumptionKey;
    minutes: TimeSavedAssumptionKey;
    shareOption: keyof CopilotAdoptionOptions;
    minutesOption: keyof CopilotAdoptionOptions;
  }
> = {
  organiseMeetings: {
    share: 'organiseMeetingsShare',
    minutes: 'organiseMeetingsMinutes',
    shareOption: 'coworkOrganiseMeetingsShare',
    minutesOption: 'coworkOrganiseMeetingsMinutes',
  },
  prepareMeetings: {
    share: 'prepareMeetingsShare',
    minutes: 'prepareMeetingsMinutes',
    shareOption: 'coworkPrepareMeetingsShare',
    minutesOption: 'coworkPrepareMeetingsMinutes',
  },
  sendEmail: {
    share: 'sendEmailShare',
    minutes: 'sendEmailMinutes',
    shareOption: 'coworkSendEmailShare',
    minutesOption: 'coworkSendEmailMinutes',
  },
  postInTeams: {
    share: 'postInTeamsShare',
    minutes: 'postInTeamsMinutes',
    shareOption: 'coworkPostInTeamsShare',
    minutesOption: 'coworkPostInTeamsMinutes',
  },
  createDocuments: {
    share: 'createDocumentsShare',
    minutes: 'createDocumentsMinutes',
    shareOption: 'coworkCreateDocumentsShare',
    minutesOption: 'coworkCreateDocumentsMinutes',
  },
};

const COWORK_ACTIVITY_KEYS: readonly TimeSavedAssumptionKey[] = COWORK_ACTIVITIES.flatMap((activity) => [
  COWORK_ACTIVITY_ASSUMPTIONS[activity].share,
  COWORK_ACTIVITY_ASSUMPTIONS[activity].minutes,
]);

export const TIME_SAVED_ASSUMPTION_KEYS: readonly TimeSavedAssumptionKey[] = [
  'meetingMinutes',
  'emailMinutes',
  'documentMinutes',
  'seatOutlookMinutes',
  'seatOfficeMinutes',
  'seatMeetingMinutes',
  'seatUncreditedMinutes',
  ...COWORK_ACTIVITY_KEYS,
  'conservativeRatio',
  'hoursPerDay',
];

/** The figures the licence estimate's hours depend on. */
export const LICENCE_ASSUMPTION_KEYS: readonly TimeSavedAssumptionKey[] = [
  'meetingMinutes',
  'emailMinutes',
  'documentMinutes',
  'conservativeRatio',
];

/** The figures the Cowork estimate's hours depend on. */
export const COWORK_ASSUMPTION_KEYS: readonly TimeSavedAssumptionKey[] = [...COWORK_ACTIVITY_KEYS, 'conservativeRatio'];

/**
 * True when the reader changed a figure THIS estimate uses. A reader who retuned the Copilot minutes
 * on the Licence opportunities tab has not changed the Cowork estimate, and its headline must not say
 * "using your figures" as though they had.
 */
export function customisesAny(customised: readonly TimeSavedAssumptionKey[], keys: readonly TimeSavedAssumptionKey[]): boolean {
  return customised.some((key) => keys.includes(key));
}

/**
 * The range a figure may take.
 *
 * Wide enough for any honest position - a customer who has timed their own meetings may well argue
 * for 20 minutes, and one piece of work handed to Cowork can be a whole multi-step job - but bounded,
 * so a typo cannot turn a sizing model into a headline of millions of hours. A share is at most all of
 * the work: above that the model would hand Cowork more than people do. The server clamps an Excel
 * export's figures to the same bounds (TimeSavedOverrides).
 */
export const TIME_SAVED_LIMITS: Record<TimeSavedAssumptionKey, { min: number; max: number }> = {
  meetingMinutes: { min: 0, max: 120 },
  emailMinutes: { min: 0, max: 60 },
  documentMinutes: { min: 0, max: 120 },
  seatOutlookMinutes: { min: 0, max: 60 },
  seatOfficeMinutes: { min: 0, max: 120 },
  seatMeetingMinutes: { min: 0, max: 120 },
  seatUncreditedMinutes: { min: 0, max: 240 },
  organiseMeetingsShare: { min: 0, max: 1 },
  organiseMeetingsMinutes: { min: 0, max: 240 },
  prepareMeetingsShare: { min: 0, max: 1 },
  prepareMeetingsMinutes: { min: 0, max: 240 },
  sendEmailShare: { min: 0, max: 1 },
  sendEmailMinutes: { min: 0, max: 240 },
  postInTeamsShare: { min: 0, max: 1 },
  postInTeamsMinutes: { min: 0, max: 240 },
  createDocumentsShare: { min: 0, max: 1 },
  createDocumentsMinutes: { min: 0, max: 240 },
  conservativeRatio: { min: 0, max: 1 },
  hoursPerDay: { min: 1, max: 24 },
};

/**
 * Hours in a working day, for the full-time-capacity restatement only.
 *
 * Not a server option because nothing the server produces depends on it: the Excel report quotes
 * hours, and hours are the models' real output. Eight hours over the report's working month is the
 * conventional 160-hour full-time month.
 */
export const DEFAULT_HOURS_PER_DAY = 8;

function finite(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

/** A configured or entered figure, with a non-number or a negative read as zero - as the server reads it. */
function nonNegative(value: unknown): number {
  return finite(value) ? Math.max(0, value) : 0;
}

/** True when a figure is one the model will accept as it stands. */
export function isValidAssumption(key: TimeSavedAssumptionKey, value: number): boolean {
  const limits = TIME_SAVED_LIMITS[key];
  return finite(value) && value >= limits.min && value <= limits.max;
}

/**
 * The product's own assumptions, as configured on the server. Every Cowork figure is configured:
 * everyone is modelled from their own work, so there is no observed rate to take a default from.
 */
export function defaultTimeSavedAssumptions(options: CopilotAdoptionOptions | null | undefined): TimeSavedAssumptions {
  const o = (options ?? {}) as Partial<CopilotAdoptionOptions>;
  const option = (key: keyof CopilotAdoptionOptions) => nonNegative(o[key]);
  return {
    meetingMinutes: nonNegative(o.copilotMinutesSavedPerMeeting),
    emailMinutes: nonNegative(o.copilotMinutesSavedPerMailThread),
    documentMinutes: nonNegative(o.copilotMinutesSavedPerDocument),
    seatOutlookMinutes: nonNegative(o.copilotSeatOutlookMinutesPerAction),
    seatOfficeMinutes: nonNegative(o.copilotSeatOfficeMinutesPerAction),
    seatMeetingMinutes: nonNegative(o.copilotSeatMeetingMinutesPerAction),
    seatUncreditedMinutes: nonNegative(o.copilotSeatUncreditedMinutesPerAction),
    // Shares clamped exactly as the server clamps them (CoworkActivity.Share): above 1 the model would
    // hand Cowork more work than people do.
    organiseMeetingsShare: Math.min(1, option('coworkOrganiseMeetingsShare')),
    organiseMeetingsMinutes: option('coworkOrganiseMeetingsMinutes'),
    prepareMeetingsShare: Math.min(1, option('coworkPrepareMeetingsShare')),
    prepareMeetingsMinutes: option('coworkPrepareMeetingsMinutes'),
    sendEmailShare: Math.min(1, option('coworkSendEmailShare')),
    sendEmailMinutes: option('coworkSendEmailMinutes'),
    postInTeamsShare: Math.min(1, option('coworkPostInTeamsShare')),
    postInTeamsMinutes: option('coworkPostInTeamsMinutes'),
    createDocumentsShare: Math.min(1, option('coworkCreateDocumentsShare')),
    createDocumentsMinutes: option('coworkCreateDocumentsMinutes'),
    // Clamped exactly as the server clamps it: above 1 the "conservative" end would exceed the full
    // one and the range would print backwards.
    conservativeRatio: clamp(finite(o.coworkEstimateLowerBoundRatio) ? o.coworkEstimateLowerBoundRatio : 0, 0, 1),
    hoursPerDay: DEFAULT_HOURS_PER_DAY,
  };
}

/** A modelled figure with a sensible number of decimals: one below ten, none above. */
export function formatModelled(value: number): string {
  const digits = Math.abs(value) < 10 ? 1 : 0;
  return formatNumber(value, { minimumFractionDigits: 0, maximumFractionDigits: digits });
}

/** A range, or a single figure when both ends round to the same value. */
export function modelledRange(t: TFunction, low: string, high: string): string {
  return low === high ? high : t('copilotAdoptionTimeSaved.range', { low, high });
}

/** The high end from which a tile's range may switch to compact notation. */
export const COMPACT_RANGE_FROM = 10_000;

/**
 * A range of hours short enough for a headline tile, in the reader's own number conventions.
 *
 * At the 200,000-user design point these ranges reach seven digits, and a tile a couple of hundred
 * pixels wide wraps "1,275,000-2,550,000 h" onto three lines. Compact notation ("1.28m", "48k") fixes
 * that in English - but not in Spanish, which writes thousands as "48 mil": longer than "48.000". So
 * compact notation is used only where it is actually shorter in the reader's language, which in
 * Spanish means from the millions. Both ends always use the same notation, so a range never reads
 * as "9,500-12k".
 */
export function compactHoursRange(t: TFunction, low: number, high: number): string {
  const whole = [low, high].map((v) => formatNumber(Math.round(Math.max(0, v))));
  if (Math.round(high) >= COMPACT_RANGE_FROM) {
    const compact = [low, high].map((v) =>
      formatNumber(Math.max(0, v), { notation: 'compact', maximumSignificantDigits: 3 }),
    );
    if (compact.join('').length < whole.join('').length) return modelledRange(t, compact[0], compact[1]);
  }
  return modelledRange(t, whole[0], whole[1]);
}

/**
 * Working days in the report's month, exactly as the server restates per-day volumes.
 *
 * The usage-report volumes are per-active-day averages, and the server multiplies them by this to
 * quote a month. Anything the model says "per day" or "per full-time person" must divide by the same
 * number, or the per-person figure would describe a different month from the total it came from.
 */
export function workingDaysPerMonth(options: CopilotAdoptionOptions): number {
  return Math.max(1, options.habitBucketNormalisationDays * (options.workingDaysPerWeek / 7));
}

/**
 * Splits a rounded total across unrounded parts so the rounded parts add up to it.
 *
 * Largest-remainder apportionment: floor every part, then give the leftover units to the parts with
 * the biggest fractional remainders. Ties go to the earlier part, so the result is deterministic.
 */
export function apportion(total: number, parts: number[]): number[] {
  const safe = parts.map((p) => (Number.isFinite(p) && p > 0 ? p : 0));
  const result = safe.map((p) => Math.floor(p));
  // Never more than the number of parts when `total` is the rounded sum of `parts`, because the
  // leftover is the rounded sum of the fractional remainders.
  let leftover = Math.max(0, Math.round(total)) - result.reduce((sum, p) => sum + p, 0);
  const order = safe
    .map((p, index) => ({ index, remainder: p - Math.floor(p) }))
    .sort((a, b) => b.remainder - a.remainder || a.index - b.index);

  for (let i = 0; leftover > 0 && i < order.length; i++) {
    result[order[i].index] += 1;
    leftover -= 1;
  }
  return result;
}

/** The same modelled minutes, restated in units people feel. Shared by both models. */
export interface TimeSavedRestatement {
  /** The hours restated as full-time people, at each end of the range. */
  fteHigh: number;
  fteLow: number;
  /** Modelled minutes per person per working day, at each end of the range. */
  minutesPerPersonDayHigh: number;
  minutesPerPersonDayLow: number;
  workingDaysPerMonth: number;
  /** Hours in one full-time month: working days a month times hours a day. */
  hoursPerFullTimeMonth: number;
}

function restate(
  minutes: number,
  ratio: number,
  cohortUsers: number,
  assumptions: TimeSavedAssumptions,
  options: CopilotAdoptionOptions,
): TimeSavedRestatement {
  const days = workingDaysPerMonth(options);
  const hoursPerDay = clamp(finite(assumptions.hoursPerDay) ? assumptions.hoursPerDay : DEFAULT_HOURS_PER_DAY, 1, 24);
  const hoursPerFullTimeMonth = days * hoursPerDay;
  const personDays = cohortUsers * days;
  return {
    fteHigh: minutes / 60 / hoursPerFullTimeMonth,
    fteLow: (minutes * ratio) / 60 / hoursPerFullTimeMonth,
    minutesPerPersonDayHigh: minutes / personDays,
    minutesPerPersonDayLow: (minutes * ratio) / personDays,
    workingDaysPerMonth: days,
    hoursPerFullTimeMonth,
  };
}

/** One kind of work in the licence estimate: its observed volume, the assumption applied, and the result. */
export interface ActivityProjection {
  activity: TimeSavedActivity;
  /** Observed volume a month across the cohort. Measured, not modelled. */
  volume: number;
  /** The minutes-saved assumption applied to each item. */
  minutesEach: number;
  /** Modelled hours a month at the full assumption, unrounded. */
  hours: number;
  /**
   * The same hours rounded so the three activities add up to exactly the estimate's rounded total.
   * Rounding each independently lets "120 + 300 + 181" sit under a total of 600, which a reader
   * checking the sum takes as an arithmetic error in the model.
   */
  displayHours: number;
  /** Share of the estimate's total, 0-100. */
  sharePct: number;
}

/** Everything the page says about the time a licence could give back to one cohort. */
export interface LicenceProjection extends TimeSavedRestatement {
  cohortUsers: number;
  /** At the full assumptions - identical to the server's high figure. */
  hoursHigh: number;
  /** At the conservative end - identical to the server's low figure. */
  hoursLow: number;
  activities: ActivityProjection[];
  /** True when the candidate list reached its row cap, so the figure is a floor. */
  candidatesCapped: boolean;
}

/** One kind of work in the Cowork estimate: what is done by hand, what is handed over, and the result. */
export interface CoworkActivityProjection {
  activity: CoworkActivity;
  /** Done by hand a month by the people the estimate covers. Observed, not modelled. */
  volume: number;
  /** The share of it handed to Cowork, 0-1: the reader's, or the product default. */
  share: number;
  /** The minutes Cowork saves on each piece it takes on. */
  minutesEach: number;
  /** Pieces of work a month handed to Cowork, unrounded. */
  pieces: number;
  /** The same, rounded so the kinds of work add up to exactly `projectedTasks`. */
  displayPieces: number;
  /** Modelled hours a month at the full assumptions, unrounded. */
  hours: number;
  /** The same hours rounded so every kind of work adds up to exactly `hoursHigh`. */
  displayHours: number;
  /** Share of the estimate's total, 0-100. */
  sharePct: number;
}

/**
 * Everything the page says about the time Cowork could give back to one cohort, on top of Copilot.
 *
 * Everyone in the cohort is modelled from the work they already do by hand, including people already
 * using Cowork - see `projectCoworkTimeSaved`.
 */
export interface CoworkProjection extends TimeSavedRestatement {
  cohortUsers: number;
  /** At the full assumptions - identical to the server's high figure. */
  hoursHigh: number;
  /** At the conservative end - identical to the server's low figure. */
  hoursLow: number;
  /** The work done by hand, kind by kind, in `COWORK_ACTIVITIES` order. */
  activities: CoworkActivityProjection[];
  /** Pieces of work a month handed to Cowork, rounded exactly as the server rounds them. */
  projectedTasks: number;
  /** The pieces of work a month this model hands each person it covers. */
  piecesPerPerson: number;
}

/**
 * Applies the Copilot assumptions to a licence cohort's observed volumes.
 *
 * Returns null for an empty or missing cohort: "0 hours" would read as a finding about the tenant,
 * when it only means there was nobody to model.
 *
 * The arithmetic deliberately mirrors `CopilotAdoptionScoring.ModelLicenceValue` step for step - the
 * same operands, in the same order, rounded the same way - because the two must agree to the hour
 * when the assumptions match.
 */
export function projectLicenceTimeSaved(
  estimate: LicenceValueEstimate | null | undefined,
  assumptions: TimeSavedAssumptions,
  options: CopilotAdoptionOptions,
): LicenceProjection | null {
  if (!estimate || !(estimate.cohortUsers > 0)) return null;

  const meetings = nonNegative(estimate.addressableMeetings);
  const mail = nonNegative(estimate.addressableMailThreads);
  const documents = nonNegative(estimate.addressableDocuments);

  const meetingMinutes = nonNegative(assumptions.meetingMinutes);
  const emailMinutes = nonNegative(assumptions.emailMinutes);
  const documentMinutes = nonNegative(assumptions.documentMinutes);
  const ratio = clamp(nonNegative(assumptions.conservativeRatio), 0, 1);

  // Observed items x minutes saved on each.
  const minutes = meetings * meetingMinutes + mail * emailMinutes + documents * documentMinutes;
  const hoursHigh = Math.round(minutes / 60);
  const hoursLow = Math.round((minutes * ratio) / 60);

  const raw: Array<{ activity: TimeSavedActivity; volume: number; minutesEach: number }> = [
    { activity: 'meetings', volume: meetings, minutesEach: meetingMinutes },
    { activity: 'email', volume: mail, minutesEach: emailMinutes },
    { activity: 'documents', volume: documents, minutesEach: documentMinutes },
  ];
  const hours = raw.map((r) => (r.volume * r.minutesEach) / 60);
  const display = apportion(hoursHigh, hours);

  return {
    cohortUsers: estimate.cohortUsers,
    hoursHigh,
    hoursLow,
    activities: raw.map((r, i) => ({
      ...r,
      hours: hours[i],
      displayHours: display[i],
      sharePct: minutes > 0 ? ((r.volume * r.minutesEach) / minutes) * 100 : 0,
    })),
    candidatesCapped: estimate.candidatesCapped === true,
    ...restate(minutes, ratio, estimate.cohortUsers, assumptions, options),
  };
}

/** The kinds of Copilot action the seat-holder estimate credits, in the order the server sums them. */
export type SeatHolderAction = 'outlook' | 'office' | 'meetings' | 'other';

export const SEAT_HOLDER_ACTIONS: readonly SeatHolderAction[] = ['outlook', 'office', 'meetings', 'other'];

/** The minutes-per-action assumption each kind of action is credited with. */
export const SEAT_HOLDER_ACTION_ASSUMPTION: Record<SeatHolderAction, TimeSavedAssumptionKey> = {
  outlook: 'seatOutlookMinutes',
  office: 'seatOfficeMinutes',
  meetings: 'seatMeetingMinutes',
  other: 'seatUncreditedMinutes',
};

/** One kind of Copilot action in the seat-holder estimate: how many were observed, the credit, and the result. */
export interface SeatHolderActionProjection {
  action: SeatHolderAction;
  /** Observed actions a month across the seat holders modelled. Measured, not modelled. */
  volume: number;
  /** The minutes credited to each action. */
  minutesEach: number;
  /** Modelled hours a month at the full credit, unrounded. */
  hours: number;
  /** The same hours rounded so the kinds of action add up to exactly `hoursHigh`. */
  displayHours: number;
  /** Share of the estimate's total, 0-100. */
  sharePct: number;
}

export interface SeatHolderTimeSavedProjection {
  cohortUsers: number;
  excludedUsageReportSourcedUsers: number;
  hoursLow: number;
  hoursHigh: number;
  outlookActions: number;
  officeActions: number;
  teamsMeetingActions: number;
  uncreditedActions: number;
  /** The same arithmetic kind by kind, in `SEAT_HOLDER_ACTIONS` order. */
  actions: SeatHolderActionProjection[];
}

/** Observed actions as the model counts them: whole, never negative - as the server reads them. */
function seatHolderCounts(source: {
  observedOutlookActions: number;
  observedOfficeActions: number;
  observedTeamsMeetingActions: number;
  observedUncreditedActions: number;
}): Record<SeatHolderAction, number> {
  return {
    outlook: Math.round(nonNegative(source.observedOutlookActions)),
    office: Math.round(nonNegative(source.observedOfficeActions)),
    meetings: Math.round(nonNegative(source.observedTeamsMeetingActions)),
    other: Math.round(nonNegative(source.observedUncreditedActions)),
  };
}

/** Observed actions times the minutes credited to each, summed in the server's order. */
function seatHolderMinutes(counts: Record<SeatHolderAction, number>, assumptions: TimeSavedAssumptions): number {
  return counts.outlook * nonNegative(assumptions.seatOutlookMinutes)
    + counts.office * nonNegative(assumptions.seatOfficeMinutes)
    + counts.meetings * nonNegative(assumptions.seatMeetingMinutes)
    + counts.other * nonNegative(assumptions.seatUncreditedMinutes);
}

export function projectSeatHolderTimeSaved(
  estimate: SeatHolderTimeSavedEstimate | null | undefined,
  assumptions: TimeSavedAssumptions,
): SeatHolderTimeSavedProjection | null {
  if (!estimate || !((estimate.cohortUsers ?? 0) > 0 || (estimate.excludedUsageReportSourcedUsers ?? 0) > 0)) return null;
  const counts = seatHolderCounts(estimate);
  const ratio = clamp(nonNegative(assumptions.conservativeRatio), 0, 1);
  const minutes = seatHolderMinutes(counts, assumptions);
  const hoursHigh = Math.round(minutes / 60);

  const raw = SEAT_HOLDER_ACTIONS.map((action) => ({
    action,
    volume: counts[action],
    minutesEach: nonNegative(assumptions[SEAT_HOLDER_ACTION_ASSUMPTION[action]]),
  }));
  const hours = raw.map((r) => (r.volume * r.minutesEach) / 60);
  const display = apportion(hoursHigh, hours);

  return {
    cohortUsers: estimate.cohortUsers ?? 0,
    excludedUsageReportSourcedUsers: estimate.excludedUsageReportSourcedUsers ?? 0,
    hoursHigh,
    hoursLow: Math.round((minutes * ratio) / 60),
    outlookActions: counts.outlook,
    officeActions: counts.office,
    teamsMeetingActions: counts.meetings,
    uncreditedActions: counts.other,
    actions: raw.map((r, i) => ({
      ...r,
      hours: hours[i],
      displayHours: display[i],
      sharePct: minutes > 0 ? ((r.volume * r.minutesEach) / minutes) * 100 : 0,
    })),
  };
}

/**
 * One segment of the seat-holder estimate - a department - under the reader's assumptions.
 *
 * The server sends each segment's hours at the product defaults; recomputed here so the breakdown
 * follows the figures the reader typed, exactly as the total above it does.
 */
export function projectSeatHolderSegment(
  segment: SeatHolderTimeSavedSegment,
  assumptions: TimeSavedAssumptions,
): { hoursLow: number; hoursHigh: number } {
  const minutes = seatHolderMinutes(seatHolderCounts(segment), assumptions);
  const ratio = clamp(nonNegative(assumptions.conservativeRatio), 0, 1);
  return { hoursLow: Math.round((minutes * ratio) / 60), hoursHigh: Math.round(minutes / 60) };
}

/**
 * Applies the Cowork assumptions to a cohort: each kind of work it already does by hand, times the
 * share of it handed to Cowork, times the minutes Cowork saves on each piece. All on top of Copilot.
 *
 * Everyone is modelled this way, including people the Copilot audit log shows already using Cowork:
 * the audit log counts interactions, not work handed over, and Microsoft's Cowork task counts are not
 * imported (#692). So the result is the potential at full use, not the gain over today.
 *
 * Returns null for an empty or missing cohort, for the same reason as `projectLicenceTimeSaved`, and
 * mirrors `CopilotAdoptionScoring.ModelCoworkValue` step for step: the same rounded volumes, summed in
 * the same order, so the floating-point total - and the hours - are identical.
 */
export function projectCoworkTimeSaved(
  estimate: CoworkValueEstimate | null | undefined,
  assumptions: TimeSavedAssumptions,
  options: CopilotAdoptionOptions,
): CoworkProjection | null {
  if (!estimate || !(estimate.cohortUsers > 0)) return null;

  const ratio = clamp(nonNegative(assumptions.conservativeRatio), 0, 1);

  // kind by kind: volume x share x minutes
  let minutes = 0;
  let handedOver = 0;
  const raw = COWORK_ACTIVITIES.map((activity) => {
    const published = (estimate.activities ?? []).find((a) => a?.activity === activity);
    const volume = Math.round(nonNegative(published?.volumePerMonth));
    const keys = COWORK_ACTIVITY_ASSUMPTIONS[activity];
    const share = Math.min(1, nonNegative(assumptions[keys.share]));
    const minutesEach = nonNegative(assumptions[keys.minutes]);
    const pieces = volume * share;
    handedOver += pieces;
    minutes += pieces * minutesEach;
    return { activity, volume, share, minutesEach, pieces, activityMinutes: pieces * minutesEach };
  });

  const hoursHigh = Math.round(minutes / 60);
  const projectedTasks = Math.round(handedOver);

  // Apportioned in the server's order, so each row, the bar and the Excel report all show the same
  // split, adding up to the headline.
  const displayHours = apportion(hoursHigh, raw.map((r) => r.activityMinutes / 60));
  const displayPieces = apportion(projectedTasks, raw.map((r) => r.pieces));
  const sharePct = (part: number) => (minutes > 0 ? (part / minutes) * 100 : 0);

  return {
    cohortUsers: estimate.cohortUsers,
    hoursHigh,
    hoursLow: Math.round((minutes * ratio) / 60),
    activities: raw.map((r, i) => ({
      activity: r.activity,
      volume: r.volume,
      share: r.share,
      minutesEach: r.minutesEach,
      pieces: r.pieces,
      displayPieces: displayPieces[i],
      hours: r.activityMinutes / 60,
      displayHours: displayHours[i],
      sharePct: sharePct(r.activityMinutes),
    })),
    projectedTasks,
    piecesPerPerson: handedOver / estimate.cohortUsers,
    ...restate(minutes, ratio, estimate.cohortUsers, assumptions, options),
  };
}

// ---------------------------------------------------------------------------------------------
// The reader's own figures, in session storage.
// ---------------------------------------------------------------------------------------------

/** Session-storage key. Versioned so a future change of shape cannot misread an old value. */
export const TIME_SAVED_STORAGE_KEY = 'm365ai.copilotAdoption.coworkTimeSaved.v1';

/** Only the figures the reader changed. Anything absent follows the server's configured default. */
export type TimeSavedOverrides = Partial<TimeSavedAssumptions>;

/**
 * `sessionStorage`, guarded.
 *
 * Reading the property itself throws `SecurityError` when site data is blocked, before any method on
 * it is called - see the same guard in `api/http.ts`. A blocked store means the reader's figures last
 * only as long as the page does, which is a degradation, not a failure.
 */
function sessionStore(): Storage | undefined {
  try {
    return typeof window === 'undefined' ? undefined : window.sessionStorage;
  } catch {
    return undefined;
  }
}

/** Figures held in memory when session storage cannot be used, so editing still works. */
let memoryFallback = '';

function readRaw(): string {
  const store = sessionStore();
  if (!store) return memoryFallback;
  try {
    return store.getItem(TIME_SAVED_STORAGE_KEY) ?? '';
  } catch {
    return memoryFallback;
  }
}

function writeRaw(value: string): void {
  memoryFallback = value;
  const store = sessionStore();
  if (store) {
    try {
      if (value) store.setItem(TIME_SAVED_STORAGE_KEY, value);
      else store.removeItem(TIME_SAVED_STORAGE_KEY);
    } catch {
      // Quota or a blocked store: the in-memory copy above keeps the page consistent.
    }
  }
  for (const listener of [...listeners]) listener();
}

const listeners = new Set<() => void>();

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/**
 * Parses stored overrides, keeping only figures the model would accept.
 *
 * Anything else - a hand-edited value, a number outside the bounds, a key from a future version - is
 * dropped rather than clamped, so a corrupt entry falls back to the product default instead of
 * quietly becoming the nearest bound.
 */
export function parseTimeSavedOverrides(raw: string): TimeSavedOverrides {
  if (!raw) return {};
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return {};
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};

  const overrides: TimeSavedOverrides = {};
  for (const key of TIME_SAVED_ASSUMPTION_KEYS) {
    const value = (parsed as Record<string, unknown>)[key];
    if (finite(value) && isValidAssumption(key, value)) overrides[key] = value;
  }
  return overrides;
}

/** Test-only: forget every stored figure. */
export function resetTimeSavedStore(): void {
  memoryFallback = '';
  const store = sessionStore();
  try {
    store?.removeItem(TIME_SAVED_STORAGE_KEY);
  } catch {
    // Nothing stored, nothing to remove.
  }
  for (const listener of [...listeners]) listener();
}

export interface TimeSavedAssumptionState {
  /** The figures in force: the reader's where they set one, the product default everywhere else. */
  assumptions: TimeSavedAssumptions;
  /** The product defaults, for "reset" and for saying what a customised figure replaced. */
  defaults: TimeSavedAssumptions;
  /** The figures the reader has changed from the default. */
  customised: TimeSavedAssumptionKey[];
  /** True when any figure differs from the product default. */
  isCustomised: boolean;
  /** Sets one figure. An invalid figure is ignored; setting the default back removes the override. */
  setAssumption: (key: TimeSavedAssumptionKey, value: number) => void;
  /** Returns one figure to the product default. */
  resetAssumption: (key: TimeSavedAssumptionKey) => void;
  /** Returns every figure to the product default. */
  resetAll: () => void;
}

/**
 * The assumptions in force for this browser tab.
 *
 * Every component that shows a modelled figure reads it through here - the Licence opportunities and
 * Cowork tabs, the two time-back tiles on the overview and the Excel export link - so a figure changed
 * in one place is the figure used in all of them, and no two parts of the page can quote a model under
 * different assumptions. The conservative share and the hours in a day apply to both models, so
 * changing either on one tab changes it on the other.
 *
 * Takes the summary for its options: every default, the Cowork shares and minutes included, is the
 * product's configured figure.
 */
export function useTimeSavedAssumptions(
  summary: Pick<CopilotAdoptionSummary, 'options'> | null | undefined,
): TimeSavedAssumptionState {
  const raw = useSyncExternalStore(subscribe, readRaw, () => '');
  const options = summary?.options;
  const defaults = useMemo(() => defaultTimeSavedAssumptions(options), [options]);
  const overrides = useMemo(() => parseTimeSavedOverrides(raw), [raw]);

  const assumptions = useMemo<TimeSavedAssumptions>(() => ({ ...defaults, ...overrides }), [defaults, overrides]);
  const customised = useMemo(
    () => TIME_SAVED_ASSUMPTION_KEYS.filter((key) => key in overrides && overrides[key] !== defaults[key]),
    [defaults, overrides],
  );

  const write = useCallback(
    (next: TimeSavedOverrides) => {
      // An override equal to today's default is dropped, so it follows the server if an admin later
      // retunes the default, rather than pinning this tab to the old value.
      const kept: TimeSavedOverrides = {};
      for (const key of TIME_SAVED_ASSUMPTION_KEYS) {
        const value = next[key];
        if (value !== undefined && value !== defaults[key]) kept[key] = value;
      }
      writeRaw(Object.keys(kept).length > 0 ? JSON.stringify(kept) : '');
    },
    [defaults],
  );

  const setAssumption = useCallback(
    (key: TimeSavedAssumptionKey, value: number) => {
      if (!isValidAssumption(key, value)) return;
      write({ ...parseTimeSavedOverrides(readRaw()), [key]: value });
    },
    [write],
  );

  const resetAssumption = useCallback(
    (key: TimeSavedAssumptionKey) => {
      const next = { ...parseTimeSavedOverrides(readRaw()) };
      delete next[key];
      write(next);
    },
    [write],
  );

  const resetAll = useCallback(() => writeRaw(''), []);

  return {
    assumptions,
    defaults,
    customised,
    isCustomised: customised.length > 0,
    setAssumption,
    resetAssumption,
    resetAll,
  };
}

/**
 * The figures to send with an Excel export, so the workbook's modelled hours match the screen.
 *
 * Only the figures that differ from the default are sent, and only those the server models: the
 * hours-per-day restatement exists on screen alone. Named after the server options they override -
 * the Copilot minutes restate the licence estimate, each kind of work's share and minutes the Cowork
 * one, and the conservative share both.
 */
export function timeSavedExportParams(state: Pick<TimeSavedAssumptionState, 'assumptions' | 'customised'>): Record<string, string> {
  const params: Record<string, string> = {};
  const { assumptions, customised } = state;
  if (customised.includes('meetingMinutes')) params.copilotMinutesSavedPerMeeting = String(assumptions.meetingMinutes);
  if (customised.includes('emailMinutes')) params.copilotMinutesSavedPerMailThread = String(assumptions.emailMinutes);
  if (customised.includes('documentMinutes')) params.copilotMinutesSavedPerDocument = String(assumptions.documentMinutes);
  if (customised.includes('conservativeRatio')) params.coworkEstimateLowerBoundRatio = String(assumptions.conservativeRatio);
  if (customised.includes('seatOutlookMinutes')) params.copilotSeatOutlookMinutesPerAction = String(assumptions.seatOutlookMinutes);
  if (customised.includes('seatOfficeMinutes')) params.copilotSeatOfficeMinutesPerAction = String(assumptions.seatOfficeMinutes);
  if (customised.includes('seatMeetingMinutes')) params.copilotSeatMeetingMinutesPerAction = String(assumptions.seatMeetingMinutes);
  if (customised.includes('seatUncreditedMinutes')) params.copilotSeatUncreditedMinutesPerAction = String(assumptions.seatUncreditedMinutes);
  for (const activity of COWORK_ACTIVITIES) {
    const keys = COWORK_ACTIVITY_ASSUMPTIONS[activity];
    if (customised.includes(keys.share)) params[keys.shareOption] = String(assumptions[keys.share]);
    if (customised.includes(keys.minutes)) params[keys.minutesOption] = String(assumptions[keys.minutes]);
  }
  return params;
}
