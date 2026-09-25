import { DEFAULT_LANGUAGE, type Language } from './languages';
import { setActiveLocale } from './locale';
import { catalogFor, EN_CATALOG, type TranslationKey } from './catalog';
import { interpolate, lookup, type TranslationValues } from './translate';

/**
 * The language in force right now, for code that is not a component.
 *
 * Not everything a reader sees is rendered from a hook. The API layer throws `Error`s whose
 * message a page puts straight on screen ("Couldn't load the licence activity overview (500)",
 * "Your session has expired. Reload the page to sign in again."), chart libraries call formatting
 * callbacks, and CSV exporters build headings. None of those can call `useT()`, and an error
 * message in the wrong language is exactly the kind of half-translated seam this feature exists to
 * remove - arguably the worst one, because it is what a reader sees when something has already
 * gone wrong.
 *
 * `I18nProvider` is the only writer. Components should still use `useT()`: it re-renders when the
 * language changes, whereas this is a snapshot read at call time.
 */
let active: Language = DEFAULT_LANGUAGE;

/**
 * Records the language and points the number/date formatters at its locale.
 *
 * One function sets both, because the two must never disagree: a portal that says "Cargando" while
 * formatting 1.234 as 1,234 is worse than one that is consistently wrong.
 */
export function setActiveLanguage(language: Language): void {
  active = language;
  setActiveLocale(language);
}

export function activeLanguage(): Language {
  return active;
}

/**
 * Resolves a key in whatever language is currently in force.
 *
 * Use `useT()` in a component. This is for the places that have no component: thrown errors, chart
 * callbacks, exporters.
 */
export function translateActive(key: TranslationKey, values?: TranslationValues): string {
  return interpolate(lookup(catalogFor(active), EN_CATALOG, key), values);
}
