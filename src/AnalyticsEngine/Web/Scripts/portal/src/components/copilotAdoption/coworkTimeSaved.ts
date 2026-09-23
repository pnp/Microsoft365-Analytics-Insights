import { useCallback, useMemo, useSyncExternalStore } from 'react';
import type { CopilotAdoptionOptions, CoworkValueEstimate } from '../../types/copilotAdoption';
import { formatNumber, type TFunction } from '../../i18n';

/**
 * The potential-time-saved model behind the Cowork tab's headline, and the reader's own assumptions.
 *
 * <b>Nothing here is measured.</b> The volumes come from Microsoft's usage reports and are real; the
 * minutes saved per meeting, email and document are assumptions, and every figure this module
 * returns is those two multiplied together. The server authors the same arithmetic in
 * `CopilotAdoptionScoring.ModelCoworkValue`, and the two are kept identical on purpose - an
 * uncustomised page must show exactly the hours the Excel report does, to the hour.
 *
 * The reader can replace any assumption with their own figure. Those figures are kept in this
 * browser tab's session storage and nowhere else: they are never written to the database, so one
 * admin's view of the model cannot become another's report.
 */

/** The three kinds of observed work the model converts into time. */
export type TimeSavedActivity = 'meetings' | 'email' | 'documents';

export const TIME_SAVED_ACTIVITIES: readonly TimeSavedActivity[] = ['meetings', 'email', 'documents'];

export interface TimeSavedAssumptions {
  /** Minutes saved per Teams meeting attended. */
  meetingMinutes: number;
  /** Minutes saved per email sent or read. */
  emailMinutes: number;
  /** Minutes saved per SharePoint or OneDrive document viewed or edited. */
  documentMinutes: number;
  /** Share of the assumptions the conservative end of the range applies, from 0 to 1. */
  conservativeRatio: number;
  /** Working hours in a day. Used only to restate the hours as full-time capacity. */
  hoursPerDay: number;
}

export type TimeSavedAssumptionKey = keyof TimeSavedAssumptions;

export const TIME_SAVED_ASSUMPTION_KEYS: readonly TimeSavedAssumptionKey[] = [
  'meetingMinutes',
  'emailMinutes',
  'documentMinutes',
  'conservativeRatio',
  'hoursPerDay',
];

/**
 * The range a figure may take.
 *
 * Wide enough for any honest position - a customer who has timed their own meetings may well argue
 * for 20 minutes - but bounded, so a typo cannot turn a rollout-sizing model into a headline of
 * millions of hours. The server clamps an Excel export's figures to the same bounds.
 */
export const TIME_SAVED_LIMITS: Record<TimeSavedAssumptionKey, { min: number; max: number }> = {
  meetingMinutes: { min: 0, max: 120 },
  emailMinutes: { min: 0, max: 60 },
  documentMinutes: { min: 0, max: 120 },
  conservativeRatio: { min: 0, max: 1 },
  hoursPerDay: { min: 1, max: 24 },
};

/**
 * Hours in a working day, for the full-time-capacity restatement only.
 *
 * Not a server option because nothing the server produces depends on it: the Excel report quotes
 * hours, and hours are the model's real output. Eight hours over the report's working month is the
 * conventional 160-hour full-time month.
 */
export const DEFAULT_HOURS_PER_DAY = 8;

function finite(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

/** True when a figure is one the model will accept as it stands. */
export function isValidAssumption(key: TimeSavedAssumptionKey, value: number): boolean {
  const limits = TIME_SAVED_LIMITS[key];
  return finite(value) && value >= limits.min && value <= limits.max;
}

/** The product's own assumptions, as configured on the server. */
export function defaultTimeSavedAssumptions(options: CopilotAdoptionOptions | null | undefined): TimeSavedAssumptions {
  const o = (options ?? {}) as Partial<CopilotAdoptionOptions>;
  return {
    meetingMinutes: Math.max(0, finite(o.coworkMinutesSavedPerMeeting) ? o.coworkMinutesSavedPerMeeting : 0),
    emailMinutes: Math.max(0, finite(o.coworkMinutesSavedPerMailThread) ? o.coworkMinutesSavedPerMailThread : 0),
    documentMinutes: Math.max(0, finite(o.coworkMinutesSavedPerDocument) ? o.coworkMinutesSavedPerDocument : 0),
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
  return low === high ? high : t('copilotAdoptionCowork.timeSaved.range', { low, high });
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

/** One kind of work in the model: its observed volume, the assumption applied, and the result. */
export interface ActivityProjection {
  activity: TimeSavedActivity;
  /** Observed volume a month across the cohort. Measured, not modelled. */
  volume: number;
  /** The minutes-saved assumption applied to each item. */
  minutesEach: number;
  /** Modelled hours a month at the full assumption, unrounded. */
  hours: number;
  /**
   * The same hours rounded so the three activities add up to exactly the rounded total. Rounding each
   * independently lets "120 + 300 + 181" sit under a total of 600, which a reader checking the sum
   * takes as an arithmetic error in the model.
   */
  displayHours: number;
  /** Share of the modelled total, 0-100. */
  sharePct: number;
}

/** Everything the page says about one cohort's potential time saved. */
export interface TimeSavedProjection {
  cohortUsers: number;
  /** Modelled hours a month at the full assumptions - identical to the server's high figure. */
  hoursHigh: number;
  /** Modelled hours a month at the conservative end - identical to the server's low figure. */
  hoursLow: number;
  activities: ActivityProjection[];
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

/**
 * Applies a set of assumptions to one cohort's observed volumes.
 *
 * Returns null for an empty or missing cohort: "0 hours" would read as a finding about the tenant,
 * when it only means there was nobody to model.
 *
 * The arithmetic deliberately mirrors the server step for step - the same operands, in the same
 * order, rounded the same way - because the two must agree to the hour when the assumptions match.
 */
export function projectTimeSaved(
  estimate: CoworkValueEstimate | null | undefined,
  assumptions: TimeSavedAssumptions,
  options: CopilotAdoptionOptions,
): TimeSavedProjection | null {
  if (!estimate || !(estimate.cohortUsers > 0)) return null;

  const meetings = Math.max(0, estimate.addressableMeetings || 0);
  const mail = Math.max(0, estimate.addressableMailThreads || 0);
  const documents = Math.max(0, estimate.addressableDocuments || 0);

  const meetingMinutes = Math.max(0, assumptions.meetingMinutes);
  const emailMinutes = Math.max(0, assumptions.emailMinutes);
  const documentMinutes = Math.max(0, assumptions.documentMinutes);
  const ratio = clamp(assumptions.conservativeRatio, 0, 1);

  const minutesHigh = meetings * meetingMinutes + mail * emailMinutes + documents * documentMinutes;
  const hoursHigh = Math.round(minutesHigh / 60);
  const hoursLow = Math.round((minutesHigh * ratio) / 60);

  const raw: Array<{ activity: TimeSavedActivity; volume: number; minutesEach: number }> = [
    { activity: 'meetings', volume: meetings, minutesEach: meetingMinutes },
    { activity: 'email', volume: mail, minutesEach: emailMinutes },
    { activity: 'documents', volume: documents, minutesEach: documentMinutes },
  ];
  const hours = raw.map((r) => (r.volume * r.minutesEach) / 60);
  const display = apportion(hoursHigh, hours);

  const days = workingDaysPerMonth(options);
  const hoursPerDay = clamp(finite(assumptions.hoursPerDay) ? assumptions.hoursPerDay : DEFAULT_HOURS_PER_DAY, 1, 24);
  const hoursPerFullTimeMonth = days * hoursPerDay;
  const personDays = estimate.cohortUsers * days;

  return {
    cohortUsers: estimate.cohortUsers,
    hoursHigh,
    hoursLow,
    activities: raw.map((r, i) => ({
      ...r,
      hours: hours[i],
      displayHours: display[i],
      sharePct: minutesHigh > 0 ? ((r.volume * r.minutesEach) / minutesHigh) * 100 : 0,
    })),
    fteHigh: (minutesHigh / 60) / hoursPerFullTimeMonth,
    fteLow: ((minutesHigh * ratio) / 60) / hoursPerFullTimeMonth,
    minutesPerPersonDayHigh: minutesHigh / personDays,
    minutesPerPersonDayLow: (minutesHigh * ratio) / personDays,
    workingDaysPerMonth: days,
    hoursPerFullTimeMonth,
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
 * Every component that shows a modelled figure reads it through here - the Cowork tab, the headline
 * KPI on the overview and the Excel export link - so a figure changed in one place is the figure used
 * in all of them, and no two parts of the page can quote the model under different assumptions.
 */
export function useTimeSavedAssumptions(options: CopilotAdoptionOptions | null | undefined): TimeSavedAssumptionState {
  const raw = useSyncExternalStore(subscribe, readRaw, () => '');
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
 * hours-per-day restatement exists on screen alone. Named after the server options they override.
 */
export function timeSavedExportParams(state: Pick<TimeSavedAssumptionState, 'assumptions' | 'customised'>): Record<string, string> {
  const params: Record<string, string> = {};
  const { assumptions, customised } = state;
  if (customised.includes('meetingMinutes')) params.coworkMinutesSavedPerMeeting = String(assumptions.meetingMinutes);
  if (customised.includes('emailMinutes')) params.coworkMinutesSavedPerMailThread = String(assumptions.emailMinutes);
  if (customised.includes('documentMinutes')) params.coworkMinutesSavedPerDocument = String(assumptions.documentMinutes);
  if (customised.includes('conservativeRatio')) params.coworkEstimateLowerBoundRatio = String(assumptions.conservativeRatio);
  return params;
}
