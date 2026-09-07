// Pure date-range helpers for the Licence activity report, kept free of React so the preset maths
// and the custom-range validation can be unit-tested directly.
//
// These mirror the backend rules in LicenceActivityQuery.Create:
//   - Dates are UTC calendar dates in `YYYY-MM-DD` form (the `from`/`to` params).
//   - The window must be MinimumDays..MaximumDays (7..180) inclusive.
//   - It must END BEFORE TODAY (UTC): the server rejects `to == today`, so the custom `to` ceiling is
//     yesterday. PRESETS go further and end on the latest fully-settled Sunday (at least 3 days old),
//     so a default window is whole Monday-Sunday weeks; CUSTOM ranges stay exact and are never rounded.
// Working in UTC (not local) matches the server's `nowUtc.Date`, so a viewer east of UTC can't pick a
// date the server then rejects as "in the future".

import type { DateRange } from '../../types/licenceActivity';

/** The preset windows offered as one-click buttons, in ascending length. Every preset ENDS on the
 * latest settled Sunday. The 7- and 28-day presets are whole settled weeks (Monday start); 90 and 180
 * are EXACT day counts ending on the same settled Sunday - never rounded to whole weeks. */
export const PRESETS = [7, 28, 90, 180] as const;
export type PresetDays = (typeof PRESETS)[number];

export const PRESET_LABELS: Record<PresetDays, string> = {
  7: 'Last settled week',
  28: 'Last 4 fully settled weeks',
  // NOT "Last 90/180 days": like every preset these END on the latest settled Sunday, which is 3-9
  // days before today, so the window is 90/180 days ending THEN - not the 90/180 days up to today.
  // The 7/28 labels were already honest about this; these two were not.
  90: 'Last 90 settled days',
  180: 'Last 180 settled days',
};

/** Backend LicenceActivityQuery.MinimumDays / MaximumDays; overridable from the availability payload. */
export const DEFAULT_MIN_DAYS = 7;
export const DEFAULT_MAX_DAYS = 180;

/** Earliest date the backend accepts (LicenceActivityQuery rejects start.Year < 1753). */
export const MIN_SUPPORTED_DATE = '1753-01-01';

function pad2(n: number): string {
  return n < 10 ? `0${n}` : String(n);
}

/** A Date as a UTC `YYYY-MM-DD` string. */
export function toDateString(d: Date): string {
  return `${d.getUTCFullYear()}-${pad2(d.getUTCMonth() + 1)}-${pad2(d.getUTCDate())}`;
}

/** Today (UTC) as `YYYY-MM-DD`. */
export function todayString(now: Date = new Date()): string {
  return toDateString(now);
}

/** Yesterday (UTC) as `YYYY-MM-DD` - the latest date the server will accept as `to`. */
export function latestEndString(now: Date = new Date()): string {
  return addDays(todayString(now), -1);
}

/**
 * The settlement lag: a Monday-Sunday week is only treated as fully settled once its Sunday snapshot
 * has landed, which the backend allows up to 3 days after the week ends. Presets end on the latest
 * settled week so a default window is whole, settled weeks rather than a partial trailing one.
 */
export const SETTLE_LAG_DAYS = 3;

/** The latest fully-settled Sunday: the most recent Sunday on or before (today UTC - SETTLE_LAG_DAYS). */
export function settledEndString(now: Date = new Date()): string {
  const cutoff = addDays(todayString(now), -SETTLE_LAG_DAYS);
  const cutoffDate = parseDateString(cutoff);
  const daysSinceSunday = cutoffDate ? cutoffDate.getUTCDay() : 0; // getUTCDay: 0 = Sunday
  return addDays(cutoff, -daysSinceSunday);
}

/** Strict check that a string is a real `YYYY-MM-DD` calendar date (rejects 2026-02-31 etc.). */
export function isValidDateString(s: string | null | undefined): boolean {
  if (!s || !/^\d{4}-\d{2}-\d{2}$/.test(s)) return false;
  const [y, m, d] = s.split('-').map(Number);
  const date = new Date(Date.UTC(y, m - 1, d));
  return date.getUTCFullYear() === y && date.getUTCMonth() === m - 1 && date.getUTCDate() === d;
}

/** Parse a `YYYY-MM-DD` string to a UTC-midnight Date, or null if it isn't a real date. */
export function parseDateString(s: string): Date | null {
  if (!isValidDateString(s)) return null;
  const [y, m, d] = s.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d));
}

/** `s` shifted by `days` (may be negative), as a `YYYY-MM-DD` string. Invalid input is returned
 * UNCHANGED - it must never silently fall back to today, which would fabricate a valid-looking date. */
export function addDays(s: string, days: number): string {
  const date = parseDateString(s);
  if (!date) return s;
  date.setUTCDate(date.getUTCDate() + days);
  return toDateString(date);
}

/** Inclusive day count of a range (from == to is 1 day). Returns 0 for an inverted or invalid range. */
export function diffDaysInclusive(from: string, to: string): number {
  const a = parseDateString(from);
  const b = parseDateString(to);
  if (!a || !b) return 0;
  const days = Math.round((b.getTime() - a.getTime()) / (24 * 60 * 60 * 1000)) + 1;
  return days > 0 ? days : 0;
}

/**
 * A preset ending on the latest settled Sunday. The 7- and 28-day presets are whole settled weeks, so
 * their start lands on a Monday; 90 and 180 are EXACT day counts ending on the same settled Sunday,
 * never rounded to whole weeks. E.g. preset 28 -> the four fully-settled Monday-Sunday weeks.
 */
export function presetRange(days: PresetDays, now: Date = new Date()): DateRange {
  const to = settledEndString(now);
  return { from: addDays(to, -(days - 1)), to };
}

/** The preset a range corresponds to, or null when it is a genuine custom range. */
export function matchPreset(range: DateRange, now: Date = new Date()): PresetDays | null {
  return (
    PRESETS.find((p) => {
      const preset = presetRange(p, now);
      return preset.from === range.from && preset.to === range.to;
    }) ?? null
  );
}

/** The outcome of validating a custom range: either OK, or a single human-readable reason. */
export type RangeValidation = { ok: true } | { ok: false; error: string };

/**
 * Validate a custom range against the backend's rules: both ends real dates, not inverted, ending
 * before today, and within [minDays, maxDays]. Returns the first problem found, phrased for display.
 */
export function validateRange(
  range: DateRange,
  opts: { now?: Date; minDays?: number; maxDays?: number } = {},
): RangeValidation {
  const now = opts.now ?? new Date();
  const minDays = opts.minDays ?? DEFAULT_MIN_DAYS;
  const maxDays = opts.maxDays ?? DEFAULT_MAX_DAYS;

  if (!isValidDateString(range.from) || !isValidDateString(range.to)) {
    return { ok: false, error: 'Enter both a start and end date.' };
  }
  if (range.from < MIN_SUPPORTED_DATE) {
    return { ok: false, error: `The earliest supported date is ${MIN_SUPPORTED_DATE}.` };
  }
  if (range.from > range.to) {
    return { ok: false, error: 'The start date must be on or before the end date.' };
  }
  if (range.to > latestEndString(now)) {
    return { ok: false, error: 'The end date must be before today (reporting covers whole past days).' };
  }
  const span = diffDaysInclusive(range.from, range.to);
  if (span < minDays) {
    return { ok: false, error: `The range must be at least ${minDays} days.` };
  }
  if (span > maxDays) {
    return { ok: false, error: `The range cannot be longer than ${maxDays} days.` };
  }
  return { ok: true };
}
