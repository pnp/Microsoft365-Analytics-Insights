/**
 * Locale-aware number and date formatting.
 *
 * The active locale is module state rather than React context on purpose. Dozens of formatting
 * helpers (`formatCount`, `formatPct`, `formatDate`, chart axis callbacks, `toLocaleString` in
 * table cells) are plain functions called from outside a component, and turning every one of them
 * into a hook would mean rewriting every call site and would still not reach the chart libraries
 * that call them back. Keeping the locale here lets those functions stay pure-looking while still
 * following the language the user picked.
 *
 * `I18nProvider` is the only writer, and it re-renders the tree on a language change, so anything
 * rendered through React re-formats immediately.
 *
 * Getting this wrong is not cosmetic: `1,234` means one thousand two hundred and thirty-four in
 * English and one point two three four in Spanish. A number formatted in the wrong locale is not
 * ugly, it is wrong by a factor of a thousand.
 */

import { DEFAULT_LANGUAGE, localeFor, type Language } from './languages';

let activeLocaleTag: string = localeFor(DEFAULT_LANGUAGE);

/**
 * Points the formatters at a language's locale.
 *
 * Call `setActiveLanguage` in `runtime.ts` rather than this directly - it sets the language and
 * the locale together, and letting the two drift apart is how a portal ends up saying "Cargando"
 * while formatting 1.234 as 1,234.
 */
export function setActiveLocale(language: Language): void {
  activeLocaleTag = localeFor(language);
}

/** The BCP-47 tag every `Intl` call in the portal should use. */
export function activeLocale(): string {
  return activeLocaleTag;
}

// Intl formatter construction is the expensive part (locale data lookup), not formatting, and
// these run per table cell on tables with thousands of rows. Cache on the full option shape.
const numberFormatters = new Map<string, Intl.NumberFormat>();
const dateFormatters = new Map<string, Intl.DateTimeFormat>();

function numberFormatter(options?: Intl.NumberFormatOptions): Intl.NumberFormat {
  const key = `${activeLocaleTag}|${options ? JSON.stringify(options) : ''}`;
  let formatter = numberFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.NumberFormat(activeLocaleTag, options);
    numberFormatters.set(key, formatter);
  }
  return formatter;
}

function dateFormatter(options: Intl.DateTimeFormatOptions): Intl.DateTimeFormat {
  const key = `${activeLocaleTag}|${JSON.stringify(options)}`;
  let formatter = dateFormatters.get(key);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat(activeLocaleTag, options);
    dateFormatters.set(key, formatter);
  }
  return formatter;
}

/** A number in the active locale's conventions. */
export function formatNumber(value: number, options?: Intl.NumberFormatOptions): string {
  return numberFormatter(options).format(value);
}

/** A date in the active locale's conventions. */
export function formatDateParts(date: Date, options: Intl.DateTimeFormatOptions): string {
  return dateFormatter(options).format(date);
}

/**
 * Locale-aware string comparison, for sorting names and labels.
 *
 * `<`/`>` on strings compares UTF-16 code units, which puts "N with tilde" after "Z" and every
 * accented letter after every unaccented one - so a Spanish user sorting by name gets a list that
 * is not in their alphabet.
 */
export function compareStrings(a: string, b: string): number {
  return a.localeCompare(b, activeLocaleTag, { sensitivity: 'base', numeric: true });
}
