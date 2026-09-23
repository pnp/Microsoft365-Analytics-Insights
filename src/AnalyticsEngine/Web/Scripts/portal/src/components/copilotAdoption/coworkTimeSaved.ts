import { useCallback, useMemo, useSyncExternalStore } from 'react';
import type {
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  CoworkTaskRateBasis,
  CoworkValueEstimate,
  LicenceValueEstimate,
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
 *   Cowork tasks times the minutes each is assumed to save ON TOP of Copilot - the value of enabling
 *   Cowork for people who already hold a licence, paid for in Copilot Credits. No study has measured
 *   it, and every surface says so. Shown on the Cowork tab.
 *
 * There is deliberately no figure for the time Copilot gives back to people who already hold a
 * licence: no decision hangs on it, and it used to swell the Cowork headline with time Cowork does
 * not unlock.
 *
 * The reader can replace any assumption with their own figure. Those figures are kept in this
 * browser tab's session storage and nowhere else: they are never written to the database, so one
 * admin's view of the model cannot become another's report.
 */

/** The three kinds of observed work the licence estimate converts into time. */
export type TimeSavedActivity = 'meetings' | 'email' | 'documents';

export const TIME_SAVED_ACTIVITIES: readonly TimeSavedActivity[] = ['meetings', 'email', 'documents'];

export interface TimeSavedAssumptions {
  /** Minutes Copilot saves per Teams meeting attended. Licence estimate. */
  meetingMinutes: number;
  /** Minutes Copilot saves per email sent or read. Licence estimate. */
  emailMinutes: number;
  /** Minutes Copilot saves per SharePoint or OneDrive document viewed or edited. Licence estimate. */
  documentMinutes: number;
  /** Minutes Cowork saves per task, on top of Copilot. No study has measured it. Cowork estimate. */
  taskMinutes: number;
  /** Cowork tasks a month for each person not yet observed running them. Cowork estimate. */
  tasksPerPerson: number;
  /** Share of the assumptions the conservative end of the range applies, from 0 to 1. Both. */
  conservativeRatio: number;
  /** Working hours in a day. Used only to restate the hours as full-time capacity. Both. */
  hoursPerDay: number;
}

export type TimeSavedAssumptionKey = keyof TimeSavedAssumptions;

export const TIME_SAVED_ASSUMPTION_KEYS: readonly TimeSavedAssumptionKey[] = [
  'meetingMinutes',
  'emailMinutes',
  'documentMinutes',
  'taskMinutes',
  'tasksPerPerson',
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
export const COWORK_ASSUMPTION_KEYS: readonly TimeSavedAssumptionKey[] = ['taskMinutes', 'tasksPerPerson', 'conservativeRatio'];

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
 * for 20 minutes, and one Cowork task can be a whole piece of multi-step work - but bounded, so a typo
 * cannot turn a sizing model into a headline of millions of hours. The server clamps an Excel export's
 * figures to the same bounds (TimeSavedOverrides).
 */
export const TIME_SAVED_LIMITS: Record<TimeSavedAssumptionKey, { min: number; max: number }> = {
  meetingMinutes: { min: 0, max: 120 },
  emailMinutes: { min: 0, max: 60 },
  documentMinutes: { min: 0, max: 120 },
  taskMinutes: { min: 0, max: 240 },
  tasksPerPerson: { min: 0, max: 200 },
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
 * The Cowork task rate an estimate was published with: the average of the people with tasks in
 * Microsoft's Cowork report, or the server's labelled placeholder when nobody has any.
 *
 * The same figure for both cohorts - the server computes it once for the whole tenant.
 */
export function publishedTaskRate(
  estimate: CoworkValueEstimate | null | undefined,
  options: CopilotAdoptionOptions | null | undefined,
): number {
  const rate = estimate?.coworkTasksPerPersonPerMonth;
  if (finite(rate)) return Math.max(0, rate);
  const placeholder = (options as Partial<CopilotAdoptionOptions> | null | undefined)?.coworkAssumedTasksPerPersonPerMonth;
  return finite(placeholder) ? Math.max(0, placeholder) : 0;
}

/**
 * The product's own assumptions, as configured on the server - plus the tenant's published Cowork
 * task rate, which is observed rather than configured wherever the tenant has Cowork use to observe.
 */
export function defaultTimeSavedAssumptions(
  options: CopilotAdoptionOptions | null | undefined,
  estimate?: CoworkValueEstimate | null,
): TimeSavedAssumptions {
  const o = (options ?? {}) as Partial<CopilotAdoptionOptions>;
  return {
    meetingMinutes: nonNegative(o.copilotMinutesSavedPerMeeting),
    emailMinutes: nonNegative(o.copilotMinutesSavedPerMailThread),
    documentMinutes: nonNegative(o.copilotMinutesSavedPerDocument),
    taskMinutes: nonNegative(o.coworkMinutesSavedPerTask),
    tasksPerPerson: publishedTaskRate(estimate, options),
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

/** Everything the page says about the time Cowork could give back to one cohort, on top of Copilot. */
export interface CoworkProjection extends TimeSavedRestatement {
  cohortUsers: number;
  /** At the full assumptions - identical to the server's high figure. */
  hoursHigh: number;
  /** At the conservative end - identical to the server's low figure. */
  hoursLow: number;
  /** People in the cohort with Cowork tasks in Microsoft's report. Observed. */
  observedUsers: number;
  /** Their tasks a month. Observed. */
  observedTasks: number;
  /** Everyone else in the cohort, projected at `tasksPerPerson`. */
  projectedUsers: number;
  /** The rate in force: the reader's own, or the one the estimate was published with. */
  tasksPerPerson: number;
  /** Where `tasksPerPerson` came from. */
  rateBasis: CoworkTaskRateBasis;
  /** For an observed rate, how many people it is the average of. */
  rateUsers: number;
  /** Projected tasks a month, rounded exactly as the server rounds them. */
  projectedTasks: number;
  /** Observed plus projected tasks a month. */
  tasks: number;
  /** The minutes-saved assumption applied to each task. */
  minutesEach: number;
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

/**
 * Applies the Cowork assumptions to a cohort: tasks already in Microsoft's Cowork report, plus
 * everyone else projected at the rate in force, times the minutes each task saves on top of Copilot.
 *
 * Returns null for an empty or missing cohort, for the same reason as `projectLicenceTimeSaved`, and
 * mirrors `CopilotAdoptionScoring.ModelCoworkValue` step for step.
 */
export function projectCoworkTimeSaved(
  estimate: CoworkValueEstimate | null | undefined,
  assumptions: TimeSavedAssumptions,
  options: CopilotAdoptionOptions,
): CoworkProjection | null {
  if (!estimate || !(estimate.cohortUsers > 0)) return null;

  // (observed tasks + projected people x rate) x minutes per task
  const observedUsers = Math.min(estimate.cohortUsers, Math.max(0, Math.floor(estimate.coworkTaskUsers || 0)));
  const observedTasks = observedUsers > 0 ? nonNegative(estimate.observedCoworkTasks) : 0;
  const projectedUsers = estimate.cohortUsers - observedUsers;
  const tasksPerPerson = nonNegative(assumptions.tasksPerPerson);
  const projectedTasks = Math.round(projectedUsers * tasksPerPerson);
  const tasks = observedTasks + projectedTasks;
  const taskMinutes = nonNegative(assumptions.taskMinutes);
  const ratio = clamp(nonNegative(assumptions.conservativeRatio), 0, 1);

  const minutes = tasks * taskMinutes;

  // A rate that differs from the one published is the reader's; otherwise it keeps the server's label.
  const published = publishedTaskRate(estimate, options);
  const rateBasis: CoworkTaskRateBasis =
    tasksPerPerson !== published ? 'custom' : (estimate.coworkTaskRateBasis ?? 'assumed');

  return {
    cohortUsers: estimate.cohortUsers,
    hoursHigh: Math.round(minutes / 60),
    hoursLow: Math.round((minutes * ratio) / 60),
    observedUsers,
    observedTasks,
    projectedUsers,
    tasksPerPerson,
    rateBasis,
    rateUsers: rateBasis === 'observed' ? nonNegative(estimate.coworkTaskRateUsers) : 0,
    projectedTasks,
    tasks,
    minutesEach: taskMinutes,
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
 * Takes the summary, not just its options, because one default is not configured: the Cowork task rate
 * is the average of the tenant's own Cowork users wherever there are any, so it comes from the
 * published estimate.
 */
export function useTimeSavedAssumptions(
  summary: Pick<CopilotAdoptionSummary, 'options' | 'coworkValueEstimate' | 'coworkFullRolloutEstimate'> | null | undefined,
): TimeSavedAssumptionState {
  const raw = useSyncExternalStore(subscribe, readRaw, () => '');
  const options = summary?.options;
  const estimate = summary?.coworkFullRolloutEstimate ?? summary?.coworkValueEstimate;
  const defaults = useMemo(() => defaultTimeSavedAssumptions(options, estimate), [options, estimate]);
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
 * the Copilot minutes restate the licence estimate, the task figures the Cowork one, and the
 * conservative share both.
 */
export function timeSavedExportParams(state: Pick<TimeSavedAssumptionState, 'assumptions' | 'customised'>): Record<string, string> {
  const params: Record<string, string> = {};
  const { assumptions, customised } = state;
  if (customised.includes('meetingMinutes')) params.copilotMinutesSavedPerMeeting = String(assumptions.meetingMinutes);
  if (customised.includes('emailMinutes')) params.copilotMinutesSavedPerMailThread = String(assumptions.emailMinutes);
  if (customised.includes('documentMinutes')) params.copilotMinutesSavedPerDocument = String(assumptions.documentMinutes);
  if (customised.includes('conservativeRatio')) params.coworkEstimateLowerBoundRatio = String(assumptions.conservativeRatio);
  if (customised.includes('taskMinutes')) params.coworkMinutesSavedPerTask = String(assumptions.taskMinutes);
  if (customised.includes('tasksPerPerson')) params.coworkTasksPerPersonPerMonth = String(assumptions.tasksPerPerson);
  return params;
}
