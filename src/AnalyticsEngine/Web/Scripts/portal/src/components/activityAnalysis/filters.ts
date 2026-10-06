import { formatList, formatNumber, plural, type TFunction } from '../../i18n';
import type { ActivityAnalysisLicence, ActivityAnalysisMetric, ActivityAnalysisRange } from '../../types/activityAnalysis';
import { EMPTY_USER_FILTER, type UserFilter } from '../../types/userFilter';
import { clauseIsComplete, groupClauseIndexes, userFilterKey } from '../userFilter/userFilterModel';
import { describeClause, describeUserFilter, dimensionLabel, type DimensionNameSource } from '../userFilter/describeUserFilter';
import { hoursToSeconds, isDuration, metricLabel, secondsToHours } from './metrics';

/**
 * The filter panel's rules: what the reader typed (a draft) against what the figures are filtered by
 * (applied), and how one becomes the other. Nothing re-queries until the reader applies the draft, so
 * the two are kept apart, and the conversion is where every value is checked.
 *
 * Durations are typed in hours and sent in seconds, the unit the API reports and compares in.
 */

export interface AppliedFilters {
  userFilter: UserFilter;
  /** Licence type ids, ascending. */
  licences: number[];
  /** In the API's units, in catalogue order, each with at least one bound. */
  ranges: ActivityAnalysisRange[];
}

export const NO_FILTERS: AppliedFilters = { userFilter: EMPTY_USER_FILTER, licences: [], ranges: [] };

/** One metric's range as typed: text, so a half-typed or invalid value can stay on screen with its reason. */
export interface RangeText {
  min: string;
  max: string;
}

export interface FilterDraft {
  userFilter: UserFilter;
  licences: number[];
  ranges: Record<string, RangeText>;
}

/** Why a range cannot be applied: not a number of zero or more, not whole, or a minimum above the maximum. */
export type RangeError = 'number' | 'whole' | 'order';

const HOURS_DECIMALS = 100;

function boundText(value: number | null, metric: ActivityAnalysisMetric | undefined): string {
  if (value == null) return '';
  return isDuration(metric) ? String(Math.round(secondsToHours(value) * HOURS_DECIMALS) / HOURS_DECIMALS) : String(value);
}

/** The draft the panel opens with: the filters in force, durations back in hours. */
export function draftFrom(applied: AppliedFilters, metrics: ReadonlyMap<string, ActivityAnalysisMetric>): FilterDraft {
  const ranges: Record<string, RangeText> = {};
  for (const range of applied.ranges) {
    const metric = metrics.get(range.metric);
    ranges[range.metric] = { min: boundText(range.min, metric), max: boundText(range.max, metric) };
  }
  return { userFilter: applied.userFilter, licences: [...applied.licences], ranges };
}

function parseBound(text: string, duration: boolean): { value: number | null; error?: RangeError } {
  // A browser's number box hands over "1.5" whatever the reader's language, but a pasted "1,5" is the
  // Spanish way of writing the same number, and is read as one.
  const trimmed = text.trim().replace(',', '.');
  if (trimmed === '') return { value: null };
  if (!/^\d+(?:\.\d+)?$/.test(trimmed)) return { value: null, error: 'number' };

  const value = Number(trimmed);
  if (!Number.isFinite(value)) return { value: null, error: 'number' };
  if (!duration && !Number.isInteger(value)) return { value: null, error: 'whole' };
  return { value: duration ? hoursToSeconds(value) : value };
}

/** One range as the API reads it, or the reason it cannot be. */
export function parseRange(
  text: RangeText,
  metric: ActivityAnalysisMetric | undefined,
): { min: number | null; max: number | null; error: RangeError | null } {
  const duration = isDuration(metric);
  const min = parseBound(text.min, duration);
  const max = parseBound(text.max, duration);
  const error = min.error ?? max.error ?? null;
  if (error) return { min: null, max: null, error };
  if (min.value != null && max.value != null && min.value > max.value) return { min: null, max: null, error: 'order' };
  return { min: min.value, max: max.value, error: null };
}

/**
 * The draft as filters to apply, or the ranges that stop it being applied. Ranges are kept in
 * catalogue order and licences ascending, so the same filters always make the same request.
 */
export function applyDraft(
  draft: FilterDraft,
  order: readonly string[],
  metrics: ReadonlyMap<string, ActivityAnalysisMetric>,
): { applied: AppliedFilters; errors: Record<string, RangeError> } {
  const errors: Record<string, RangeError> = {};
  const ranges: ActivityAnalysisRange[] = [];

  for (const key of order) {
    const text = draft.ranges[key];
    if (!text) continue;
    const parsed = parseRange(text, metrics.get(key));
    if (parsed.error) errors[key] = parsed.error;
    else if (parsed.min != null || parsed.max != null) ranges.push({ metric: key, min: parsed.min, max: parsed.max });
  }

  const licences = [...new Set(draft.licences)].sort((a, b) => a - b);
  return { applied: { userFilter: draft.userFilter, licences, ranges }, errors };
}

/** A stable identity for applied filters, for effect dependencies and comparisons. */
export function filtersKey(applied: AppliedFilters): string {
  return JSON.stringify({ u: userFilterKey(applied.userFilter), l: applied.licences, r: applied.ranges });
}

/**
 * The number on the Filters button: one per people condition, one for the licence filter however many
 * licences it names, and one per activity range.
 */
export function activeFilterCount(applied: AppliedFilters): number {
  return (
    applied.userFilter.clauses.filter(clauseIsComplete).length +
    (applied.licences.length > 0 ? 1 : 0) +
    applied.ranges.length
  );
}

function boundLabel(t: TFunction, value: number, metric: ActivityAnalysisMetric | undefined): string {
  return isDuration(metric)
    ? t('activityAnalysis.filters.summary.hours', { value: formatNumber(secondsToHours(value), { maximumFractionDigits: 2 }) })
    : formatNumber(value);
}

/** A range in words: "Teams calls: 5 to 100", "Emails sent: at least 10". */
export function describeRange(t: TFunction, range: ActivityAnalysisRange, metric: ActivityAnalysisMetric | undefined): string {
  const label = metricLabel(t, range.metric, metric?.label);
  if (range.min != null && range.max != null) {
    return t('activityAnalysis.filters.summary.between', {
      metric: label,
      min: boundLabel(t, range.min, metric),
      max: boundLabel(t, range.max, metric),
    });
  }
  return range.min != null
    ? t('activityAnalysis.filters.summary.atLeast', { metric: label, min: boundLabel(t, range.min, metric) })
    : t('activityAnalysis.filters.summary.atMost', { metric: label, max: boundLabel(t, range.max ?? 0, metric) });
}

/** The licence filter in words, naming each licence as the tenant calls it. */
export function describeLicences(t: TFunction, ids: readonly number[], licences: readonly ActivityAnalysisLicence[]): string {
  const names = ids.map(
    (id) => licences.find((l) => l.id === id)?.name ?? t('activityAnalysis.filters.licences.unknown', { id: String(id) }),
  );
  return t(plural(ids.length, 'activityAnalysis.filters.summary.licences.one', 'activityAnalysis.filters.summary.licences.other'), {
    names: formatList(names),
  });
}

/**
 * Every applied filter in words, one entry per thing a reader would name: each people condition, the
 * licences, each activity range. A people filter with OR groups is one entry, read back whole, because
 * splitting it would lose which conditions belong together.
 */
export function describeAppliedFilters(
  t: TFunction,
  applied: AppliedFilters,
  metrics: ReadonlyMap<string, ActivityAnalysisMetric>,
  licences: readonly ActivityAnalysisLicence[],
  source?: DimensionNameSource,
): string[] {
  const items: string[] = [];
  const clauses = applied.userFilter.clauses.filter(clauseIsComplete);

  if (clauses.length > 0) {
    const filter = { clauses };
    if (groupClauseIndexes(filter).length > 1) items.push(describeUserFilter(t, filter, source));
    else items.push(...clauses.map((clause) => describeClause(t, clause, dimensionLabel(t, clause.dimension, source))));
  }

  if (applied.licences.length > 0) items.push(describeLicences(t, applied.licences, licences));
  for (const range of applied.ranges) items.push(describeRange(t, range, metrics.get(range.metric)));

  return items;
}
