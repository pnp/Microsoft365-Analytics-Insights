/**
 * The languages the portal ships in, and how one gets chosen for a visitor.
 *
 * Adding a language is deliberately a three-step change - add it here, add a `catalog/<id>`
 * directory, and register it in `catalog/index.ts` - because the catalog is typed against English.
 * TypeScript then refuses to build until the new language covers every key, which is what stops a
 * half-translated portal reaching a customer.
 */

export type Language = 'en' | 'es';

export interface LanguageDefinition {
  id: Language;
  /**
   * BCP-47 tag handed to `Intl` for numbers, dates and collation.
   *
   * Region-qualified on purpose. The product is written in British English ("licence",
   * "organisation"), and `es-ES` picks European Spanish conventions - `1.234,5` rather than the
   * `1,234.5` of most Latin American locales - so a figure never reads as a thousand times its
   * real value to a Spanish reader.
   */
  locale: string;
  /** The language's name in that language, which is how a language picker must name it. */
  nativeName: string;
  /** The same name in English, for accessibility text and logs. */
  englishName: string;
}

export const LANGUAGES: readonly LanguageDefinition[] = [
  { id: 'en', locale: 'en-GB', nativeName: 'English', englishName: 'English' },
  { id: 'es', locale: 'es-ES', nativeName: 'Espa\u00f1ol', englishName: 'Spanish' },
] as const;

/** The language everything falls back to, and the one the catalog is authored in. */
export const DEFAULT_LANGUAGE: Language = 'en';

/** Where a visitor's explicit choice is remembered between sessions. */
export const LANGUAGE_STORAGE_KEY = 'm365analytics.portal.language';

export function isLanguage(value: unknown): value is Language {
  return typeof value === 'string' && LANGUAGES.some((l) => l.id === value);
}

export function languageDefinition(language: Language): LanguageDefinition {
  return LANGUAGES.find((l) => l.id === language) ?? LANGUAGES[0];
}

/** The BCP-47 tag to format numbers and dates with for a language. */
export function localeFor(language: Language): string {
  return languageDefinition(language).locale;
}

/**
 * The best supported language for a list of browser preferences, or null when none match.
 *
 * Matches on the primary subtag, so `es-419` (Latin American Spanish) and `es-MX` both resolve to
 * Spanish rather than silently falling back to English - the alternative would give a Mexican
 * reader an English portal because the region happened not to be Spain.
 */
export function matchLanguage(preferences: readonly string[] | undefined): Language | null {
  for (const preference of preferences ?? []) {
    const primary = String(preference).toLowerCase().split('-')[0];
    const match = LANGUAGES.find((l) => l.id === primary);
    if (match) return match.id;
  }
  return null;
}

/** Browser-preferred languages, most-preferred first. Split out so tests can supply their own. */
export function browserLanguages(
  nav: Pick<Navigator, 'languages' | 'language'> | undefined,
): string[] {
  if (!nav) return [];
  if (Array.isArray(nav.languages) && nav.languages.length > 0) return [...nav.languages];
  return nav.language ? [nav.language] : [];
}

/**
 * Reads a previously chosen language.
 *
 * Wrapped because `localStorage` throws outright when cookies/site data are blocked, which is a
 * supported way to run a browser and must not take the whole portal down.
 */
export function storedLanguage(storage: Pick<Storage, 'getItem'> | undefined): Language | null {
  try {
    const value = storage?.getItem(LANGUAGE_STORAGE_KEY);
    return isLanguage(value) ? value : null;
  } catch {
    return null;
  }
}

export function storeLanguage(
  storage: Pick<Storage, 'setItem'> | undefined,
  language: Language,
): void {
  try {
    storage?.setItem(LANGUAGE_STORAGE_KEY, language);
  } catch {
    // Choice won't survive a reload. Detection still runs, so the session itself is unaffected.
  }
}

/**
 * The language to open in: an explicit earlier choice, else the browser's preference, else English.
 *
 * The stored choice wins over the browser because it is the more specific signal - someone who
 * picked English on a Spanish-configured machine did so on purpose.
 */
export function detectLanguage(options?: {
  storage?: Pick<Storage, 'getItem'>;
  navigator?: Pick<Navigator, 'languages' | 'language'>;
}): Language {
  const storage =
    options?.storage ?? (typeof window === 'undefined' ? undefined : window.localStorage);
  const nav = options?.navigator ?? (typeof navigator === 'undefined' ? undefined : navigator);

  return storedLanguage(storage) ?? matchLanguage(browserLanguages(nav)) ?? DEFAULT_LANGUAGE;
}
