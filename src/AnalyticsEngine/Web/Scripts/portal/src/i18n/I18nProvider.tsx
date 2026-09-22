import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';

import {
  DEFAULT_LANGUAGE,
  browserStorage,
  detectLanguage,
  languageDefinition,
  localeFor,
  storeLanguage,
  type Language,
} from './languages';
import { setActiveLanguage } from './runtime';
import {
  catalogFailed,
  catalogFor,
  EN_CATALOG,
  isCatalogLoaded,
  loadCatalog,
  type TranslationKey,
} from './catalog';
import {
  interpolate,
  interpolateNodes,
  lookup,
  type RichTranslationValues,
  type TranslationValues,
} from './translate';

/** Resolves a catalog key to text. */
export type TFunction = (key: TranslationKey, values?: TranslationValues) => string;

/** Resolves a catalog key to text with elements substituted in, for a sentence carrying a link. */
export type TNodeFunction = (key: TranslationKey, values?: RichTranslationValues) => ReactNode;

export interface I18nContextValue {
  language: Language;
  /** BCP-47 tag for `Intl` - `en-GB` or `es-ES`, not the bare language id. */
  locale: string;
  setLanguage: (language: Language) => void;
  t: TFunction;
  tNode: TNodeFunction;
}

const I18nContext = createContext<I18nContextValue | null>(null);

/**
 * Makes a language available to the tree, and keeps everything that depends on it in step.
 *
 * Three things have to move together when the language changes, and all three are done here so a
 * call site cannot get one of them and miss the others:
 *
 * - the catalog every `t()` reads from;
 * - the locale used by the plain (non-hook) number and date formatters in `locale.ts`;
 * - `<html lang>`, which is what a screen reader uses to choose a voice and what the browser uses
 *   to decide whether to offer to translate the page. Leaving it at `en` on a Spanish page makes a
 *   screen reader read Spanish with English phonetics, which is close to unusable.
 *
 * The locale is pushed into `locale.ts` during render rather than in an effect: children render
 * before a parent's effect runs, so an effect would let the first paint after a language change
 * format its numbers with the old locale.
 *
 * Every language except English is fetched as its own chunk (see `catalog/index.ts`). The provider
 * renders straight away in whatever is already loaded and re-renders when the fetch lands, rather
 * than blocking on it: a portal that shows nothing until a translation file arrives is worse than
 * one that shows English for a moment. In production there is no such moment, because `main.tsx`
 * awaits the catalog before the first render; this path exists for the language *switch*, and for
 * a reader whose chunk fetch is slow.
 */
export function I18nProvider({
  children,
  initialLanguage,
}: {
  children: ReactNode;
  /** Overrides detection. Tests use it; the app leaves it unset so the browser decides. */
  initialLanguage?: Language;
}) {
  const [requested, setRequestedLanguage] = useState<Language>(
    () => initialLanguage ?? detectLanguage(),
  );
  // Bumped when a catalog fetch settles, to re-render with text that was not available before.
  const [catalogRevision, setCatalogRevision] = useState(0);

  /**
   * The language actually in use.
   *
   * Not the same as the one requested, when a chunk fetch failed: `loadCatalog` falls the text
   * back to English, and everything else has to follow it. Rendering English prose under
   * `<html lang="es-ES">`, with Spanish thousands separators and a picker still saying "Espanol",
   * would be worse than the honest fallback - a screen reader would read English with Spanish
   * phonetics, and the numbers would be grouped the wrong way round.
   */
  const language: Language =
    requested !== DEFAULT_LANGUAGE && catalogFailed(requested) ? DEFAULT_LANGUAGE : requested;

  // Render-time, not effect-time: see the note above.
  setActiveLanguage(language);

  /**
   * Re-assert after commit.
   *
   * The render-time call above is what makes the *first* paint of a language change use the right
   * locale. But React may start a render and abandon it, and the formatters read module state, so
   * an abandoned render could leave the committed tree formatting in a language it is not showing.
   * A layout effect runs only for the render that actually committed, and before paint, so
   * re-asserting here means the last write always matches what is on screen.
   */
  useLayoutEffect(() => {
    setActiveLanguage(language);
  }, [language]);

  useEffect(() => {
    if (typeof document !== 'undefined') {
      document.documentElement.lang = localeFor(language);
    }
  }, [language]);

  useEffect(() => {
    if (isCatalogLoaded(language)) return;
    let cancelled = false;
    void loadCatalog(language).then(() => {
      if (!cancelled) setCatalogRevision((revision) => revision + 1);
    });
    return () => {
      cancelled = true;
    };
  }, [language]);

  const setLanguage = useCallback((next: Language) => {
    // State first: storage can throw on a browser with site data blocked (the property getter
    // itself does), and a language change must not be lost to a failed attempt to remember it.
    setRequestedLanguage(next);
    setActiveLanguage(next);
    storeLanguage(browserStorage(), next);
  }, []);

  const value = useMemo<I18nContextValue>(() => {
    const catalog = catalogFor(language);
    return {
      language,
      locale: localeFor(language),
      setLanguage,
      t: (key, values) => interpolate(lookup(catalog, EN_CATALOG, key), values),
      tNode: (key, values) => interpolateNodes(lookup(catalog, EN_CATALOG, key), values),
    };
    // catalogRevision is not read here, but it is what makes this recompute once the catalog for
    // `language` has arrived - `catalogFor` would otherwise keep returning the English fallback
    // captured on the first render.
  }, [language, setLanguage, catalogRevision]);

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

/**
 * The full i18n context.
 *
 * Falls back to an English context rather than throwing when there is no provider. A component
 * rendered in isolation by a unit test is the common case, and making every one of those tests
 * wrap in a provider buys nothing: English is exactly what they would get anyway.
 */
export function useI18n(): I18nContextValue {
  const context = useContext(I18nContext);
  return context ?? FALLBACK_CONTEXT;
}

/** The usual hook: `const t = useT();` then `t('key')`. */
export function useT(): TFunction {
  return useI18n().t;
}

/** For a sentence with an element in it: `tNode('key', { link: <a .../> })`. */
export function useTNode(): TNodeFunction {
  return useI18n().tNode;
}

const FALLBACK_CONTEXT: I18nContextValue = {
  language: DEFAULT_LANGUAGE,
  locale: localeFor(DEFAULT_LANGUAGE),
  setLanguage: () => {},
  t: (key, values) => interpolate(lookup(EN_CATALOG, EN_CATALOG, key), values),
  tNode: (key, values) => interpolateNodes(lookup(EN_CATALOG, EN_CATALOG, key), values),
};

/**
 * Resolves a key outside React - a chart axis callback, a CSV exporter, a sort comparator.
 *
 * Use the hook wherever there is a component to hang it on. This exists for the callbacks the
 * chart libraries invoke, where there is no component and no hook may be called.
 */
export function translateStatic(
  language: Language,
  key: TranslationKey,
  values?: TranslationValues,
): string {
  return interpolate(lookup(catalogFor(language), EN_CATALOG, key), values);
}

/** The name of a language, in that language - for a picker, which must not translate its options. */
export function languageName(language: Language): string {
  return languageDefinition(language).nativeName;
}
