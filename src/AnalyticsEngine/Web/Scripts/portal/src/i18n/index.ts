/**
 * The portal's translation layer.
 *
 * Import from here, not from the files behind it:
 *
 * ```tsx
 * import { useT } from '../../i18n';
 *
 * const t = useT();
 * return <Text>{t('licenceActivity.title')}</Text>;
 * ```
 *
 * The rules, in short:
 *
 * - **Every string a user can read goes in the catalog.** `src/i18n/noHardcodedStrings.test.ts`
 *   fails the build on a literal in a JSX text node or a user-facing prop, so this is enforced
 *   rather than remembered.
 * - **Keys are checked at compile time.** `t()` takes a `TranslationKey`, so a typo or a renamed
 *   key is a build error, not a key name rendered on screen.
 * - **Spanish is checked at compile time too.** Each `catalog/es/*.ts` is typed against its
 *   English counterpart, so a new English string with no Spanish translation fails `npm run lint`.
 *   That is what stops a half-translated portal shipping.
 * - **Data from SQL is not translated.** User names, site titles, agent names, department names
 *   and everything else that came out of the customer's tenant is shown as stored. Only the
 *   product's own text is translated.
 * - **Numbers and dates follow the language**, through `formatNumber`/`formatDateParts` and the
 *   per-area format helpers built on them. `1,234` and `1.234` mean different things in English
 *   and Spanish, so this is correctness, not polish.
 * - **English is bundled; every other language is fetched as its own chunk.** `main.tsx` awaits
 *   the reader's language before the first render, so a new language costs existing readers
 *   nothing. See the note in `catalog/index.ts`.
 */

export {
  I18nProvider,
  useI18n,
  useT,
  useTNode,
  translateStatic,
  languageName,
  type I18nContextValue,
  type TFunction,
  type TNodeFunction,
} from './I18nProvider';

export {
  LANGUAGES,
  DEFAULT_LANGUAGE,
  LANGUAGE_STORAGE_KEY,
  detectLanguage,
  isLanguage,
  languageDefinition,
  localeFor,
  matchLanguage,
  browserLanguages,
  storedLanguage,
  storeLanguage,
  type Language,
  type LanguageDefinition,
} from './languages';

export {
  activeLocale,
  setActiveLanguage,
  formatNumber,
  formatDateParts,
  compareStrings,
} from './locale';

export {
  interpolate,
  interpolateNodes,
  resetTranslationWarnings,
  type Catalog,
  type TranslationValues,
  type RichTranslationValues,
} from './translate';

export {
  EN_CATALOG,
  EN_MODULES,
  CATALOG_MODULE_NAMES,
  catalogFor,
  isCatalogLoaded,
  loadCatalog,
  resetLoadedCatalogs,
  type TranslationKey,
} from './catalog';

export { plural } from './plural';
export { default as LanguageSwitcher } from './LanguageSwitcher';
