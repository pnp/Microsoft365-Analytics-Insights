import { createElement, Fragment, isValidElement, type ReactNode } from 'react';

/**
 * The translation primitives: catalog lookup, `{placeholder}` substitution and pluralisation.
 *
 * Kept free of React and of module state so the rules can be tested directly, and so a catalog
 * string can be resolved from a chart callback or a CSV exporter as easily as from a component.
 */

/** Values substituted into `{placeholder}` markers. */
export type TranslationValues = Record<string, string | number>;

/** Same, but a value may be an element - for a sentence with a link or a bold figure in it. */
export type RichTranslationValues = Record<string, ReactNode>;

/** A resolved catalog: every key the portal can ask for, mapped to text in one language. */
export type Catalog = Readonly<Record<string, string>>;

const PLACEHOLDER = /\{(\w+)\}/g;

/**
 * Substitutes `{name}` markers from `values`.
 *
 * An unmatched marker is left verbatim rather than blanked, because `Showing {count} users` on
 * screen is an obvious bug report whereas `Showing  users` looks like a rendering glitch and gets
 * lived with.
 */
export function interpolate(template: string, values?: TranslationValues): string {
  if (!values) return template;
  return template.replace(PLACEHOLDER, (match, name: string) => {
    const value = values[name];
    return value === undefined || value === null ? match : String(value);
  });
}

/**
 * Splits a template into text and substituted nodes, so a sentence can carry a link, a bold
 * figure or an icon without being cut into fragments that no translator can reorder.
 *
 * Returns an array suitable for rendering directly: React accepts mixed strings and elements.
 * Substituted *elements* are wrapped in a keyed Fragment, because React warns about every
 * unkeyed element in an array - and a sentence with a link in it is the normal case here, so
 * without this the console fills with key warnings on any page using `tNode`.
 */
export function interpolateNodes(template: string, values?: RichTranslationValues): ReactNode[] {
  const out: ReactNode[] = [];
  let lastIndex = 0;
  let match: RegExpExecArray | null;

  // Fresh regex per call: PLACEHOLDER is /g, so a shared instance would carry lastIndex between
  // calls and silently skip the first placeholder of every other sentence.
  const pattern = new RegExp(PLACEHOLDER.source, 'g');
  while ((match = pattern.exec(template)) !== null) {
    if (match.index > lastIndex) out.push(template.slice(lastIndex, match.index));
    const name = match[1];
    const value = values?.[name];
    if (value === undefined || value === null) {
      out.push(match[0]);
    } else if (isValidElement(value)) {
      out.push(createElement(Fragment, { key: `${name}-${match.index}` }, value));
    } else {
      out.push(value);
    }
    lastIndex = match.index + match[0].length;
  }
  if (lastIndex < template.length) out.push(template.slice(lastIndex));
  return out;
}

/**
 * Looks a key up, falling back to the fallback catalog and finally to the key itself.
 *
 * The fallback exists for a key resolved at runtime - a status code or workload name used to build
 * a key - which the compiler cannot check. Every statically written key is guaranteed present in
 * every language by the type of the catalog, so this path should never fire for those.
 */
export function lookup(catalog: Catalog, fallback: Catalog, key: string): string {
  const value = catalog[key];
  if (typeof value === 'string') return value;

  const fallbackValue = fallback[key];
  if (typeof fallbackValue === 'string') {
    warnOnce(`[portal i18n] Missing translation for "${key}"; showing the English text.`);
    return fallbackValue;
  }

  warnOnce(`[portal i18n] Unknown translation key "${key}".`);
  return key;
}

const warned = new Set<string>();

/**
 * One warning per distinct message.
 *
 * A missing key is usually inside a table cell, so an unguarded `console.warn` would fire once per
 * row and bury everything else in the console - including the first occurrence.
 */
function warnOnce(message: string): void {
  if (warned.has(message)) return;
  warned.add(message);
  if (typeof console !== 'undefined') console.warn(message);
}

/** Test-only: forget which warnings have been emitted. */
export function resetTranslationWarnings(): void {
  warned.clear();
}
