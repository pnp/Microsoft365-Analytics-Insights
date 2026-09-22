// @vitest-environment node
import { describe, it, expect, beforeAll } from 'vitest';

import { CATALOG_MODULE_NAMES, EN_CATALOG, loadCatalog } from './index';
import { LANGUAGES, type Language } from '../languages';
import { catalogModulesFor, languagesWithModules } from '../../test/catalogModules';
import type { Catalog } from '../translate';
import { ALLOWED_LITERALS } from '../lint/allowList';

/**
 * The catalog's own invariants.
 *
 * TypeScript already proves that each translated module covers every English key - each
 * `catalog/<language>/*.ts` is typed against its English counterpart, so a missing translation is
 * a build failure. These cover the things the type system cannot see:
 *
 * - a key claimed by two modules, where the merge silently picks one;
 * - a key that does not belong to the module it is in, which makes the first problem likely;
 * - a `{placeholder}` that exists in one language and not the other, which renders as literal
 *   `{count}` on screen;
 * - a sentence chopped into fragments that no translator can reassemble;
 * - an HTML entity, which JSX would decode but a translated string does not;
 * - a "translation" that is just the English text pasted across to satisfy the compiler. That is
 *   the realistic way a half-translated portal gets shipped while every other check is green.
 *
 * Run for every language the portal ships, not only Spanish, so a third language is covered the
 * moment it is registered rather than whenever somebody remembers to widen this file.
 */

const PLACEHOLDER = /\{(\w+)\}/g;

function placeholders(text: string): string[] {
  return [...text.matchAll(PLACEHOLDER)].map((m) => m[1]).sort();
}

/** Every language other than the one the catalog is authored in. */
const TRANSLATED: Language[] = LANGUAGES.map((l) => l.id).filter((id) => id !== 'en');

const catalogs = new Map<Language, Catalog>();
let enModules: Record<string, Catalog>;

beforeAll(async () => {
  // Each translated catalog is a separately fetched chunk in the browser, so it is awaited here.
  for (const language of TRANSLATED) catalogs.set(language, await loadCatalog(language));
  enModules = catalogModulesFor('en');
});

describe('Translation catalog', () => {
  it('breaks every shipped language down by module', () => {
    // Without this, a language registered in LANGUAGES but missing from the test helper would make
    // the per-module checks throw something unreadable instead of reporting the real problem.
    expect(languagesWithModules().sort()).toEqual(LANGUAGES.map((l) => l.id).sort());
  });

  it('namespaces every key by its module, so two areas cannot claim the same key', () => {
    const wrong: string[] = [];
    for (const module of CATALOG_MODULE_NAMES) {
      for (const key of Object.keys(enModules[module])) {
        if (!key.startsWith(`${module}.`)) wrong.push(`${key} (in ${module})`);
      }
    }
    expect(
      wrong,
      `Keys must start with their module name, e.g. "health.title" in health.ts:\n  ${wrong.join('\n  ')}`,
    ).toEqual([]);
  });

  it('defines each key in exactly one module', () => {
    const owners = new Map<string, string[]>();
    for (const module of CATALOG_MODULE_NAMES) {
      for (const key of Object.keys(enModules[module])) {
        owners.set(key, [...(owners.get(key) ?? []), module]);
      }
    }
    const duplicated = [...owners.entries()]
      .filter(([, modules]) => modules.length > 1)
      .map(([key, modules]) => `${key}: ${modules.join(', ')}`);
    expect(
      duplicated,
      `Duplicate keys would be silently merged:\n  ${duplicated.join('\n  ')}`,
    ).toEqual([]);
  });

  it('keeps every module non-empty, so an area cannot quietly stop being translated', () => {
    const empty = CATALOG_MODULE_NAMES.filter(
      (module) => Object.keys(enModules[module]).length === 0,
    );
    expect(empty, `Catalog modules with no keys: ${empty.join(', ')}`).toEqual([]);
  });

  /**
   * A catalog value that begins or ends mid-sentence cannot be translated.
   *
   * The failing shape is a sentence chopped into adjacent `t()` calls around an element:
   * `{t('showing')}{domain}{t('only')}` with `'Showing '` and `' only.'` in the catalog. It reads
   * correctly in English and is impossible in Spanish, which puts the words in a different order.
   * The fix is always the same: one key for the whole sentence, with the element substituted in
   * through `useTNode()`.
   */
  it('has no sentence fragments in English, which cannot be translated', () => {
    const fragments = Object.entries(EN_CATALOG)
      .filter(([, value]) => value !== value.trim())
      .map(([key, value]) => `${key}: "${value}"`);

    expect(
      fragments,
      'These begin or end mid-sentence, so they are pieces of a sentence split across several\n' +
        't() calls. Word order differs between languages, so the pieces cannot be reassembled.\n' +
        'Make each one a single key for the whole sentence and substitute the element with\n' +
        "useTNode(): tNode('area.key', { domain: <strong>{value}</strong> }).\n  " +
        fragments.join('\n  '),
    ).toEqual([]);
  });

  it('has no empty English text', () => {
    const empty = Object.entries(EN_CATALOG)
      .filter(([, value]) => value.trim().length === 0)
      .map(([key]) => key);
    expect(empty, `Empty English text for: ${empty.join(', ')}`).toEqual([]);
  });
});

describe.each(TRANSLATED)('Translation catalog: %s', (language) => {
  const catalog = () => catalogs.get(language)!;
  const modules = () => catalogModulesFor(language);

  it('gives every English key a translation, and adds none of its own', () => {
    expect(Object.keys(catalog()).sort()).toEqual(Object.keys(EN_CATALOG).sort());
  });

  it('has no empty translations', () => {
    const empty = Object.entries(catalog())
      .filter(([, value]) => value.trim().length === 0)
      .map(([key]) => key);
    expect(empty, `Empty ${language} text for: ${empty.join(', ')}`).toEqual([]);
  });

  /**
   * The modules must line up with English module by module, not just in total. A key moved between
   * areas without moving its translation would still merge into an identical flat catalog, and
   * would then be impossible to find when that area is next edited.
   */
  it('splits into the same modules as English', () => {
    const translated = modules();
    const mismatched = CATALOG_MODULE_NAMES.filter(
      (module) =>
        Object.keys(enModules[module]).sort().join('\u0000') !==
        Object.keys(translated[module] ?? {}).sort().join('\u0000'),
    );
    expect(mismatched, `Module contents differ from English: ${mismatched.join(', ')}`).toEqual([]);
  });

  it('keeps the same placeholders as the English text', () => {
    const mismatched: string[] = [];
    for (const [key, english] of Object.entries(EN_CATALOG)) {
      const translated = catalog()[key];
      const en = placeholders(english);
      const other = placeholders(translated);
      if (en.join(',') !== other.join(',')) {
        mismatched.push(`${key}: en {${en.join(', ')}} vs ${language} {${other.join(', ')}}`);
      }
    }
    expect(
      mismatched,
      `A placeholder present in one language and not the other renders literally:\n  ${mismatched.join('\n  ')}`,
    ).toEqual([]);
  });

  it('has no sentence fragments', () => {
    const fragments = Object.entries(catalog())
      .filter(([, value]) => value !== value.trim())
      .map(([key, value]) => `${key}: "${value}"`);
    expect(fragments, `Fragments in ${language}:\n  ${fragments.join('\n  ')}`).toEqual([]);
  });

  /**
   * An HTML entity in a catalog value renders as itself.
   *
   * A real trap when extracting text from JSX. `<p>don&apos;t</p>` renders "don't", because JSX
   * text is parsed as HTML - but `{t('key')}` renders a JavaScript string, which React escapes, so
   * the same `&apos;` appears on screen verbatim.
   */
  it('contains no HTML entities, which would be shown to the reader as-is', () => {
    const entity = /&(?:[a-zA-Z]+|#\d+|#x[0-9a-fA-F]+);/;
    const offenders = [
      ...Object.entries(EN_CATALOG).map(([key, value]) => ['en', key, value] as const),
      ...Object.entries(catalog()).map(([key, value]) => [language, key, value] as const),
    ]
      .filter(([, , value]) => entity.test(value))
      .map(([lang, key, value]) => `${lang} ${key}: "${value}"`);

    expect(
      offenders,
      'JSX decodes an entity; a translated string does not. Replace each with the character it\n' +
        'stands for:\n  ' +
        offenders.join('\n  '),
    ).toEqual([]);
  });

  /**
   * The compiler is satisfied by any string, including the English one. Copying English across is
   * therefore the path of least resistance when a translation is hard, and it produces exactly the
   * outcome this whole feature exists to prevent - so it is checked rather than trusted.
   *
   * Identical text is legitimate for a product name, a file format or a symbol, which is what
   * `ALLOWED_LITERALS` already lists for the untranslated-text gate. Reusing that list means a
   * term is declared language-neutral in one place, not two.
   */
  it('does not pass English off as a translation', () => {
    const copied = Object.entries(EN_CATALOG)
      .filter(([key, english]) => {
        if (catalog()[key] !== english) return false;
        if (ALLOWED_LITERALS.has(english.trim())) return false;
        // An identifier rather than prose, and identical in every language by definition: an
        // example sign-in name, or a Graph permission scope such as CallRecords.Read.All.
        if (/^\S+@\S+$/.test(english)) return false;
        if (/^[A-Za-z][A-Za-z0-9]*(?:\.[A-Za-z][A-Za-z0-9]*)+$/.test(english)) return false;
        // A string with no letters of its own - "{count}", "%", "-" - cannot be translated.
        return /\p{L}{2}/u.test(english.replace(PLACEHOLDER, ''));
      })
      .map(([key, english]) => `${key}: "${english}"`);

    expect(
      copied,
      `These are identical in English and ${language}. Translate them, or - if the translation ` +
        'really is the same word - add the term to ALLOWED_LITERALS in\n' +
        'src/i18n/lint/allowList.ts:\n  ' +
        copied.join('\n  '),
    ).toEqual([]);
  });
});
