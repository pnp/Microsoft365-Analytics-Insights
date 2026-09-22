import type { Catalog } from '../translate';
import type { Language } from '../languages';

import enApp from './en/app';
import enCommon from './en/common';
import enCharts from './en/charts';
import enOverview from './en/overview';
import enReports from './en/reports';
import enCopilot from './en/copilotAdoption';
import enCopilotCowork from './en/copilotAdoptionCowork';
import enCopilotAgents from './en/copilotAdoptionAgents';
import enCopilotUsers from './en/copilotAdoptionUsers';
import enTeamsExplorer from './en/teamsExplorer';
import enWebActivity from './en/webActivity';
import enLicenceActivity from './en/licenceActivity';
import enAgentCosts from './en/agentCosts';
import enDlp from './en/dlp';
import enErrors from './en/errors';
import enHealth from './en/health';
import enAdmin from './en/admin';

/**
 * The translation catalog, split by feature area.
 *
 * Split into modules for two practical reasons, not for tidiness:
 *
 * 1. A single file holding every string in the portal is a guaranteed merge conflict on any branch
 *    that touches the UI, and this repository routinely has several in flight at once.
 * 2. Each Spanish module is typed against its English counterpart, so a missing key is reported
 *    against the area it belongs to rather than as one enormous error on a 2,500-key object.
 *
 * Keys are namespaced by module (`copilotAdoption.*`, `webActivity.*`, ...) and
 * `catalog.test.ts` enforces that, so two areas cannot quietly claim the same key and have one
 * silently win the merge below.
 *
 * ## Why English is bundled and every other language is fetched
 *
 * The catalog is around 90 kB gzipped per language. Bundling every language would mean an English
 * reader downloading Spanish text they will never see - and, worse, would make each new language a
 * permanent tax on every user, quietly turning "add a language" into a decision about page weight.
 *
 * English is the exception because it is load-bearing in two ways the others are not: it is the
 * source language the catalog is authored in, and it is the fallback for a key resolved at
 * runtime. So English is bundled and `loadCatalog` fetches anything else as its own chunk.
 * `main.tsx` awaits that before the first render, so a Spanish reader never sees English first.
 */
export const EN_MODULES = {
  app: enApp,
  common: enCommon,
  charts: enCharts,
  overview: enOverview,
  reports: enReports,
  copilotAdoption: enCopilot,
  copilotAdoptionCowork: enCopilotCowork,
  copilotAdoptionAgents: enCopilotAgents,
  copilotAdoptionUsers: enCopilotUsers,
  teamsExplorer: enTeamsExplorer,
  webActivity: enWebActivity,
  licenceActivity: enLicenceActivity,
  agentCosts: enAgentCosts,
  dlp: enDlp,
  errors: enErrors,
  health: enHealth,
  admin: enAdmin,
} as const;

/** The module names, so tests can report which area a duplicate key came from. */
export const CATALOG_MODULE_NAMES = Object.keys(EN_MODULES) as (keyof typeof EN_MODULES)[];

type UnionToIntersection<U> = (U extends unknown ? (k: U) => void : never) extends (
  k: infer I,
) => void
  ? I
  : never;

/** Every key in every module, as one type. Derived, so the module list is maintained in one place. */
export type EnglishCatalog = UnionToIntersection<(typeof EN_MODULES)[keyof typeof EN_MODULES]>;

/**
 * Every key the portal may ask for.
 *
 * `t()` takes this type, so a typo in a key, or a key that was renamed in the catalog but not at
 * the call site, is a build failure rather than the key name appearing on screen.
 */
export type TranslationKey = keyof EnglishCatalog & string;

function flatten(modules: readonly Catalog[]): Catalog {
  return Object.assign({}, ...modules) as Catalog;
}

export const EN_CATALOG: Catalog = flatten(Object.values(EN_MODULES) as unknown as Catalog[]);

/**
 * Per-language loaders for everything except English.
 *
 * The imports are written out one module at a time rather than through a computed path because
 * Vite has to see every import literally at build time to split them into chunks. A
 * `import('./' + language + '/app')` would defeat the splitting and fail only at runtime.
 */
const LOADERS: Record<Exclude<Language, 'en'>, () => Promise<Catalog>> = {
  es: async () =>
    flatten(
      (
        await Promise.all([
          import('./es/app'),
          import('./es/common'),
          import('./es/charts'),
          import('./es/overview'),
          import('./es/reports'),
          import('./es/copilotAdoption'),
          import('./es/copilotAdoptionCowork'),
          import('./es/copilotAdoptionAgents'),
          import('./es/copilotAdoptionUsers'),
          import('./es/teamsExplorer'),
          import('./es/webActivity'),
          import('./es/licenceActivity'),
          import('./es/agentCosts'),
          import('./es/dlp'),
          import('./es/errors'),
          import('./es/health'),
          import('./es/admin'),
        ])
      ).map((module) => module.default as Catalog),
    ),
};

const loaded = new Map<Language, Catalog>([['en', EN_CATALOG]]);
const inFlight = new Map<Language, Promise<Catalog>>();
const failed = new Set<Language>();

/**
 * Fetches a language's catalog, once.
 *
 * Concurrent callers share one request, so the provider and `main.tsx` can both ask without
 * fetching the chunk twice, and a reader who clicks two languages quickly does not start two loads
 * of the same one.
 */
export function loadCatalog(language: Language): Promise<Catalog> {
  const already = loaded.get(language);
  if (already) return Promise.resolve(already);

  const existing = inFlight.get(language);
  if (existing) return existing;

  const loader = LOADERS[language as Exclude<Language, 'en'>];
  if (!loader) return Promise.resolve(EN_CATALOG);

  const promise = loader()
    .then((catalog) => {
      loaded.set(language, catalog);
      failed.delete(language);
      return catalog;
    })
    .catch((error: unknown) => {
      // A failed chunk fetch - an offline reader, or a stale index.html after a redeploy. English
      // is already in memory, so the portal stays usable rather than blank. It is deliberately
      // NOT recorded as loaded: `isCatalogLoaded` stays false, which is how the provider knows to
      // fall the whole language back to English rather than show English text while claiming to
      // be Spanish - `<html lang="es-ES">` over English prose makes a screen reader read English
      // with Spanish phonetics, and the numbers would still be grouped the Spanish way.
      console.error(`[portal i18n] Could not load the ${language} translations.`, error);
      failed.add(language);
      return EN_CATALOG;
    })
    .finally(() => {
      inFlight.delete(language);
    });

  inFlight.set(language, promise);
  return promise;
}

/** True once a language's catalog is in memory and `catalogFor` will return it. */
export function isCatalogLoaded(language: Language): boolean {
  return loaded.has(language);
}

/** True when a language's catalog was attempted and could not be fetched. */
export function catalogFailed(language: Language): boolean {
  return failed.has(language);
}

/**
 * Forgets a previous failure, so the next `loadCatalog` tries again.
 *
 * Without this a single transient failure - an offline moment, a stale `index.html` after a
 * redeploy - would lock the reader out of that language for the rest of the session, because the
 * provider falls back to English and then never asks for it again. Picking the language from the
 * menu is an explicit "try again", and must behave like one.
 */
export function clearCatalogFailure(language: Language): void {
  failed.delete(language);
}

/** The catalog to render with now. Falls back to English until the language has loaded. */
export function catalogFor(language: Language): Catalog {
  return loaded.get(language) ?? EN_CATALOG;
}

/** Test-only: forget fetched catalogs so a test can exercise the loading path. */
export function resetLoadedCatalogs(): void {
  loaded.clear();
  loaded.set('en', EN_CATALOG);
  inFlight.clear();
  failed.clear();
}

/**
 * Test-only: pretend a language's chunk could not be fetched.
 *
 * The failure path is worth testing and cannot be reached otherwise - the imports are static
 * enough for Vite to split them, which is exactly what makes them hard to make fail on demand.
 */
export function markCatalogFailed(language: Language): void {
  failed.add(language);
}
