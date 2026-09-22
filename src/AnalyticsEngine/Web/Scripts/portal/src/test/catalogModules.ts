import type { Catalog } from '../i18n/translate';
import type { Language } from '../i18n/languages';
import { EN_MODULES } from '../i18n/catalog';
import esModules from '../i18n/catalog/es';

/**
 * The catalog with its module boundaries intact, for the tests that are about those boundaries -
 * "is every key namespaced to its own area?", "does any key appear in two modules?".
 *
 * Reads the same per-language `index.ts` that production loads, rather than keeping a second list
 * of modules in step with it. An earlier version did keep its own list, which meant a module added
 * to the catalog but forgotten here made the tests crash with
 * `Cannot convert undefined or null to object` instead of reporting anything useful.
 */
const MODULES: Partial<Record<Language, Record<string, Catalog>>> = {
  en: EN_MODULES as unknown as Record<string, Catalog>,
  es: esModules,
};

export function catalogModulesFor(language: Language): Record<string, Catalog> {
  const modules = MODULES[language];
  if (!modules) {
    throw new Error(
      `No catalog modules registered for "${language}". Add its index.ts to src/test/catalogModules.ts.`,
    );
  }
  return modules;
}

/** Every language whose catalog can be broken down by module - i.e. every language that ships. */
export function languagesWithModules(): Language[] {
  return Object.keys(MODULES) as Language[];
}
