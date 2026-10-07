import { formatDateParts, formatNumber, type TFunction, type TranslationKey } from '../../i18n';
import type { ActivityAnalysisAvailability } from '../../types/activityAnalysis';

/**
 * Periods for the Activity analysis page: whole ISO weeks, each named by its Monday (`yyyy-MM-dd`),
 * inclusive of both ends - the server's own rule.
 *
 * Pure functions on date strings, in UTC, so the maths is the same in every time zone: a reader west
 * of UTC must not see a Monday turn into the Sunday before it.
 */

export type PeriodPreset = 'months3' | 'months6' | 'months12' | 'all';

export const PERIOD_PRESETS: readonly PeriodPreset[] = ['months3', 'months6', 'months12', 'all'];

/** The Power BI page opens on a year, and so does this one. */
export const DEFAULT_PRESET: PeriodPreset = 'months12';

export const PRESET_LABEL_KEYS: Record<PeriodPreset, TranslationKey> = {
  months3: 'activityAnalysis.period.preset.months3',
  months6: 'activityAnalysis.period.preset.months6',
  months12: 'activityAnalysis.period.preset.months12',
  all: 'activityAnalysis.period.preset.all',
};

/** Whole weeks per preset: a quarter, a half and a whole year of Mondays. */
const PRESET_WEEKS: Record<Exclude<PeriodPreset, 'all'>, number> = {
  months3: 13,
  months6: 26,
  months12: 52,
};

const DAY_MS = 24 * 60 * 60 * 1000;

export interface WeekPeriod {
  /** The first week's Monday. */
  from: string;
  /** The last week's Monday. */
  to: string;
}

type Bounds = Pick<ActivityAnalysisAvailability, 'earliestWeek' | 'latestWeek' | 'maximumWeeks'>;

function pad2(n: number): string {
  return n < 10 ? `0${n}` : String(n);
}

/** A strict `yyyy-MM-dd` calendar date as UTC midnight, or null (2026-02-31 is not a date). */
export function parseDay(value: string | null | undefined): Date | null {
  if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
  const [y, m, d] = value.split('-').map(Number);
  const date = new Date(Date.UTC(y, m - 1, d));
  return date.getUTCFullYear() === y && date.getUTCMonth() === m - 1 && date.getUTCDate() === d ? date : null;
}

export function formatDay(date: Date): string {
  return `${date.getUTCFullYear()}-${pad2(date.getUTCMonth() + 1)}-${pad2(date.getUTCDate())}`;
}

export function addDays(value: string, days: number): string {
  const date = parseDay(value);
  return date ? formatDay(new Date(date.getTime() + days * DAY_MS)) : value;
}

export function addWeeks(value: string, weeks: number): string {
  return addDays(value, weeks * 7);
}

/** The Monday of the week a date falls in - the server snaps any date the same way. */
export function mondayOnOrBefore(value: string): string | null {
  const date = parseDay(value);
  if (!date) return null;
  // getUTCDay: 0 = Sunday ... 6 = Saturday, so Sunday belongs to the week that started six days before.
  const sinceMonday = (date.getUTCDay() + 6) % 7;
  return formatDay(new Date(date.getTime() - sinceMonday * DAY_MS));
}

/** How many weeks a period covers, counting both ends. Zero for a reversed or unreadable period. */
export function weekCount(period: WeekPeriod): number {
  const from = parseDay(period.from);
  const to = parseDay(period.to);
  if (!from || !to || to < from) return 0;
  return Math.round((to.getTime() - from.getTime()) / (7 * DAY_MS)) + 1;
}

function weeksFor(preset: PeriodPreset, bounds: Bounds): number {
  const wanted = preset === 'all' ? Number.POSITIVE_INFINITY : PRESET_WEEKS[preset];
  return Math.max(1, Math.min(wanted, bounds.maximumWeeks > 0 ? bounds.maximumWeeks : wanted));
}

/**
 * A preset's period: it ends on the latest week with data and reaches back its number of weeks, but
 * never before the earliest week - nor further than the longest period the report accepts, which is
 * what "All available" is limited to on a long-running installation.
 */
export function presetPeriod(preset: PeriodPreset, bounds: Bounds): WeekPeriod | null {
  const latest = bounds.latestWeek;
  if (!latest || !parseDay(latest)) return null;

  const earliest = bounds.earliestWeek && parseDay(bounds.earliestWeek) ? bounds.earliestWeek : latest;
  const from = addWeeks(latest, -(weeksFor(preset, bounds) - 1));
  return { from: from < earliest ? earliest : from, to: latest };
}

/** Whether "All available" had to leave out early weeks to stay within the report's limit. */
export function allIsLimited(bounds: Bounds): boolean {
  const all = presetPeriod('all', bounds);
  return !!all && !!bounds.earliestWeek && all.from > bounds.earliestWeek;
}

export type PeriodValidation = { ok: true; period: WeekPeriod } | { ok: false; error: string };

/**
 * A custom period as the reader typed it: both dates moved to their Mondays and kept within the weeks
 * that have data. Returns the first problem, phrased for the reader.
 */
export function validateCustomPeriod(draft: { from: string; to: string }, bounds: Bounds, t: TFunction): PeriodValidation {
  let from = mondayOnOrBefore(draft.from);
  let to = mondayOnOrBefore(draft.to);
  if (!from || !to) return { ok: false, error: t('activityAnalysis.period.error.missing') };

  if (bounds.earliestWeek && from < bounds.earliestWeek) from = bounds.earliestWeek;
  if (bounds.latestWeek && to > bounds.latestWeek) to = bounds.latestWeek;
  if (bounds.earliestWeek && to < bounds.earliestWeek) to = bounds.earliestWeek;
  if (bounds.latestWeek && from > bounds.latestWeek) from = bounds.latestWeek;

  if (from > to) return { ok: false, error: t('activityAnalysis.period.error.order') };

  const period = { from, to };
  if (bounds.maximumWeeks > 0 && weekCount(period) > bounds.maximumWeeks) {
    return { ok: false, error: t('activityAnalysis.period.error.tooLong', { weeks: formatNumber(bounds.maximumWeeks) }) };
  }

  return { ok: true, period };
}

/** A week's Monday as a short date in the reader's language: "6 Oct 2025" / "6 oct 2025". */
export function formatWeekDate(value: string): string {
  const date = parseDay(value);
  return date
    ? formatDateParts(date, { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' })
    : value;
}
