// Formatting helpers for the Licence activity report.
//
// The point of this module is the treatment of UNKNOWN. A value that is genuinely unknown (an import
// switched off, a source that didn't cover a user) must never render as "0" - that would assert
// "nobody did this" when the truth is "we didn't measure it". These helpers force the caller to be
// explicit about the null case rather than letting `null` coerce to 0 somewhere in a template.

import { formatDateParts, formatNumber, type TFunction } from '../../i18n';
import type { LicenceActivitySku } from '../../types/licenceActivity';
import { translateActive } from '../../i18n/runtime';

/**
 * A licence name can be absent in storage; use an actual identifier rather than invent a product.
 *
 * The first two branches return tenant data verbatim - a real SKU name or SKU id, which is the
 * same string in every language. Only the last is the product's own wording, so only the last is
 * translated. It resolves through `translateActive` rather than a hook because this is a plain
 * function used from sort comparators and filters as well as from JSX.
 */
export function licenceName(sku: LicenceActivitySku): string {
  if (sku.name?.trim()) return sku.name;
  if (sku.skuId?.trim()) return sku.skuId;
  return translateActive('licenceActivity.licenceFallbackName', { id: sku.licenceTypeId });
}

/** An em dash, for an unknown value in a dense table cell where the word "Unknown" is too heavy. */
export const DASH = '\u2014';

/** A whole number with thousands separators, e.g. 12345 -> "12,345". */
export function formatCount(value: number): string {
  return formatNumber(Math.round(value));
}

/**
 * A possibly-unknown count. Null/undefined -> the unknown marker; a real number (including 0) ->
 * that number. This is the workhorse for "Unknown is not zero".
 */
export function formatMaybeCount(value: number | null | undefined, unknown: string): string {
  return value == null ? unknown : formatCount(value);
}

/**
 * A percentage to one decimal place, dropping a trailing ".0" (e.g. 12 -> "12%", 12.34 -> "12.3%").
 *
 * The digits go through `formatNumber`, so a Spanish reader gets "12,3 %" rather than "12.3%".
 * `toFixed` would always produce a full stop, which in Spanish is the thousands separator.
 */
export function formatPct(value: number): string {
  const rounded = Math.round(value * 10) / 10;
  const digits = rounded % 1 === 0 ? 0 : 1;
  return `${formatNumber(rounded, { minimumFractionDigits: digits, maximumFractionDigits: digits })}%`;
}

/**
 * `active` as a percentage of `total`, or null when the percentage itself is unknown.
 *
 * Returns null (not 0) when either operand is unknown or the denominator is 0, so a caller can show
 * "Unknown" rather than a misleading "0%".
 */
export function ratioPct(active: number | null | undefined, total: number | null | undefined): number | null {
  if (active == null || total == null || total <= 0) return null;
  return (active / total) * 100;
}

/** A UTC ISO timestamp as a short local-format date, or the unknown marker when absent. */
export function formatDate(iso: string | null | undefined, unknown: string = DASH): string {
  if (!iso) return unknown;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return unknown;
  return formatDateParts(date, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    timeZone: 'UTC',
  });
}

/** A UTC ISO timestamp as a short local date-and-time, or the unknown marker when absent. */
export function formatDateTime(iso: string | null | undefined, unknown: string = DASH): string {
  if (!iso) return unknown;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return unknown;
  return formatDateParts(date, {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: 'UTC',
  });
}

/**
 * Whole days between a past UTC ISO timestamp and now, or null when the input is absent/invalid.
 * Used for "generated 3 days ago" style captions; null keeps an unknown time from reading as "today".
 */
export function daysAgo(iso: string | null | undefined, now: Date = new Date()): number | null {
  if (!iso) return null;
  const then = new Date(iso);
  if (Number.isNaN(then.getTime())) return null;
  const ms = now.getTime() - then.getTime();
  if (ms < 0) return 0;
  return Math.floor(ms / (24 * 60 * 60 * 1000));
}

/** "today" / "yesterday" / "N days ago" for a past UTC timestamp, or the unknown marker when absent. */
export function formatAge(
  iso: string | null | undefined,
  t: TFunction,
  now: Date = new Date(),
  unknown = t('licenceActivity.common.unknown'),
): string {
  const days = daysAgo(iso, now);
  if (days == null) return unknown;
  if (days === 0) return t('common.time.today');
  if (days === 1) return t('common.time.yesterday');
  return t('common.time.daysAgo', { days: formatNumber(days) });
}
