// Formatting helpers for the Licence activity report.
//
// The point of this module is the treatment of UNKNOWN. A value that is genuinely unknown (an import
// switched off, a source that didn't cover a user) must never render as "0" - that would assert
// "nobody did this" when the truth is "we didn't measure it". These helpers force the caller to be
// explicit about the null case rather than letting `null` coerce to 0 somewhere in a template.

/** Rendered for a value that is genuinely unknown, as opposed to a measured zero. */
export const UNKNOWN_TEXT = 'Unknown';

/** An em dash, for an unknown value in a dense table cell where the word "Unknown" is too heavy. */
export const DASH = '\u2014';

/** A whole number with thousands separators, e.g. 12345 -> "12,345". */
export function formatCount(value: number): string {
  return Math.round(value).toLocaleString();
}

/**
 * A possibly-unknown count. Null/undefined -> the unknown marker; a real number (including 0) ->
 * that number. This is the workhorse for "Unknown is not zero".
 */
export function formatMaybeCount(value: number | null | undefined, unknown: string = UNKNOWN_TEXT): string {
  return value == null ? unknown : formatCount(value);
}

/** A percentage to one decimal place, dropping a trailing ".0" (e.g. 12 -> "12%", 12.34 -> "12.3%"). */
export function formatPct(value: number): string {
  const rounded = Math.round(value * 10) / 10;
  return `${rounded % 1 === 0 ? rounded.toFixed(0) : rounded.toFixed(1)}%`;
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
  return date.toLocaleDateString(undefined, {
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
  return date.toLocaleString(undefined, {
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
export function formatAge(iso: string | null | undefined, now: Date = new Date(), unknown: string = UNKNOWN_TEXT): string {
  const days = daysAgo(iso, now);
  if (days == null) return unknown;
  if (days === 0) return 'today';
  if (days === 1) return 'yesterday';
  return `${days.toLocaleString()} days ago`;
}
