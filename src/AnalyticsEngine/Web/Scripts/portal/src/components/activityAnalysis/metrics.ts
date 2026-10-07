import { EN_CATALOG, formatNumber, type TFunction, type TranslationKey } from '../../i18n';
import type {
  ActivityAnalysisAvailability,
  ActivityAnalysisCategory,
  ActivityAnalysisMetric,
} from '../../types/activityAnalysis';

/**
 * The metric catalogue as the page uses it: labels, units, grouping and the slicer's selection rules.
 *
 * Kept free of React so the selection rules - the part a reader actually drives - are tested directly.
 * The catalogue itself comes from the server (`availability.metrics`): it decides which metrics this
 * installation has. This module only decides how they read and how a selection behaves.
 */

/** The category order the slicer uses when the server does not send one - the API's own order. */
export const CATEGORY_ORDER: readonly ActivityAnalysisCategory[] = [
  'teams',
  'outlook',
  'onedrive',
  'sharepoint',
  'copilot',
  'vivaEngage',
];

/** The metrics selected when the page opens: the core Teams activity, as the Power BI page does. */
export const DEFAULT_METRICS: readonly string[] = [
  'teams.privateChats',
  'teams.teamChats',
  'teams.calls',
  'teams.meetings',
  'teams.meetingsAttended',
  'teams.meetingsOrganized',
];

const SECONDS_PER_HOUR = 3600;

function catalogKey(key: string): TranslationKey | null {
  return Object.prototype.hasOwnProperty.call(EN_CATALOG, key) ? (key as TranslationKey) : null;
}

/** The catalog key for a metric's label, or null for a key this build does not know. */
export function metricLabelKey(metricKey: string): TranslationKey | null {
  return catalogKey(`activityAnalysis.metric.${metricKey}`);
}

/**
 * A metric's label in the reader's language. A metric added on the server after this build shipped
 * has no catalog entry yet, so it falls back to the English label the server sends with it, and to
 * the key itself when even that is missing.
 */
export function metricLabel(t: TFunction, metricKey: string, serverLabel?: string | null): string {
  const key = metricLabelKey(metricKey);
  if (key) return t(key);
  return serverLabel && serverLabel.trim() ? serverLabel : metricKey;
}

/** A category's label: a product name, or the key for a category this build does not know. */
export function categoryLabel(t: TFunction, category: string): string {
  const key = catalogKey(`activityAnalysis.category.${category}`);
  return key ? t(key) : category;
}

export function isDuration(metric: Pick<ActivityAnalysisMetric, 'unit'> | null | undefined): boolean {
  return metric?.unit === 'seconds';
}

/** A metric's label with its unit where the figure is converted: "Teams audio time (hours)". */
export function metricLabelWithUnit(t: TFunction, metricKey: string, metric: ActivityAnalysisMetric | undefined): string {
  const label = metricLabel(t, metricKey, metric?.label);
  return isDuration(metric) ? t('activityAnalysis.unit.withHours', { metric: label }) : label;
}

/** The API always reports durations in seconds; the page shows hours. */
export function secondsToHours(seconds: number): number {
  return seconds / SECONDS_PER_HOUR;
}

export function hoursToSeconds(hours: number): number {
  return Math.round(hours * SECONDS_PER_HOUR);
}

/** A metric's figure as displayed: hours for a duration, the count otherwise. */
export function displayValue(metric: Pick<ActivityAnalysisMetric, 'unit'> | undefined, raw: number): number {
  return isDuration(metric) ? secondsToHours(raw) : raw;
}

/** A metric's figure formatted for a table cell, in the reader's locale. */
export function formatMetricValue(metric: Pick<ActivityAnalysisMetric, 'unit'> | undefined, raw: number): string {
  return isDuration(metric)
    ? formatNumber(secondsToHours(raw), { maximumFractionDigits: 1 })
    : formatNumber(Math.round(raw));
}

/** One category of the slicer and the metrics this installation offers in it. */
export interface MetricGroup {
  category: string;
  metrics: ActivityAnalysisMetric[];
}

/**
 * The available metrics, grouped by category in the server's order (then any category it did not
 * list, so a new one is never dropped). Metrics keep the server's order inside each category.
 */
export function groupMetrics(availability: Pick<ActivityAnalysisAvailability, 'categories' | 'metrics'>): MetricGroup[] {
  const order: string[] = [...(availability.categories?.length ? availability.categories : CATEGORY_ORDER)];
  for (const metric of availability.metrics) {
    if (!order.includes(metric.category)) order.push(metric.category);
  }

  return order
    .map((category) => ({
      category,
      metrics: availability.metrics.filter((m) => m.category === category && m.available),
    }))
    .filter((group) => group.metrics.length > 0);
}

/** Every available metric key, in slicer order - the order a selection is always kept in. */
export function metricOrder(groups: readonly MetricGroup[]): string[] {
  return groups.flatMap((group) => group.metrics.map((m) => m.key));
}

/**
 * Keys in catalogue order, unknown ones dropped. Keeping the selection in one canonical order means a
 * metric ticked last does not reorder the chart, and two readers with the same selection send the same
 * request.
 */
export function orderedSelection(keys: Iterable<string>, order: readonly string[]): string[] {
  const wanted = new Set(keys);
  return order.filter((key) => wanted.has(key));
}

/**
 * The selection the page opens with: the core Teams metrics this installation has; failing those, the
 * core metrics of the first category that has any; failing that, the first metric there is.
 */
export function defaultSelection(groups: readonly MetricGroup[]): string[] {
  const order = metricOrder(groups);
  const teams = orderedSelection(DEFAULT_METRICS, order);
  if (teams.length > 0) return teams;

  for (const group of groups) {
    const core = group.metrics.filter((m) => m.core).map((m) => m.key);
    if (core.length > 0) return core;
  }

  return order.slice(0, 1);
}

/** A category's checkbox: ticked when all its metrics are, mixed when some are. */
export function categoryCheckState(selected: readonly string[], group: MetricGroup): boolean | 'mixed' {
  const chosen = group.metrics.filter((m) => selected.includes(m.key)).length;
  if (chosen === 0) return false;
  return chosen === group.metrics.length ? true : 'mixed';
}

/**
 * Toggling a category's checkbox: a fully ticked category is cleared; an empty or partly ticked one is
 * completed, which is what a reader clicking a mixed box expects.
 */
export function toggleCategory(selected: readonly string[], group: MetricGroup, order: readonly string[]): string[] {
  const keys = group.metrics.map((m) => m.key);
  const next = categoryCheckState(selected, group) === true
    ? selected.filter((key) => !keys.includes(key))
    : [...selected, ...keys];
  return orderedSelection(next, order);
}

export function toggleMetric(selected: readonly string[], key: string, order: readonly string[]): string[] {
  const next = selected.includes(key) ? selected.filter((k) => k !== key) : [...selected, key];
  return orderedSelection(next, order);
}

/** The metric definitions by key, for looking up a unit or a fallback label. */
export function metricsByKey(metrics: readonly ActivityAnalysisMetric[]): Map<string, ActivityAnalysisMetric> {
  return new Map(metrics.map((m) => [m.key, m]));
}
