// @vitest-environment node
import { describe, it, expect } from 'vitest';
import ts from 'typescript';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

/**
 * No number, date or sort may use the browser's locale instead of the reader's language.
 *
 * `(1234).toLocaleString()` and `date.toLocaleDateString(undefined, …)` format with whatever locale
 * the machine is configured with. That is not the language the reader picked in the portal, and the
 * difference is not cosmetic: **`1,234` is one thousand two hundred and thirty-four to an English
 * reader and one point two three four to a Spanish one.** A figure in the wrong locale, on a page
 * used to justify licence spend, is wrong by a factor of a thousand. `localeCompare` without a
 * locale has the same problem for sorting - it puts every accented letter after every unaccented
 * one, which is not the Spanish alphabet.
 *
 * `formatNumber`, `formatDateParts` and `compareStrings` in `src/i18n/locale.ts` follow the
 * selected language, so every such call must go through them - or pass `activeLocale()` explicitly.
 *
 * This check exists because the class is invisible to the untranslated-text check: a
 * `.toLocaleString()` call contains no string at all. Three of them survived the initial conversion
 * in `components/shared/KpiGrid.tsx` - a module twelve Copilot Adoption components import from -
 * and were found by review rather than by a test, which is the gap this closes.
 *
 * Written against the TypeScript AST rather than line regexes, because a line-based check cannot
 * see `new Intl.NumberFormat(\n  undefined,\n  options,\n)`, and a guard with a
 * formatting-dependent blind spot is one the next person trips over without knowing.
 */

const SRC = join(process.cwd(), 'src');

/**
 * Methods that use the ambient locale when given no explicit one, and which argument carries it.
 *
 * `localeCompare` is the odd one out: its first argument is the string to compare against, so the
 * locale is second. Getting that wrong makes the check quietly accept every unlocalised sort.
 */
const LOCALE_METHODS = new Map<string, number>([
  ['toLocaleString', 0],
  ['toLocaleDateString', 0],
  ['toLocaleTimeString', 0],
  ['localeCompare', 1],
]);

/** `Intl` constructors that do the same. */
const INTL_CONSTRUCTORS = new Set([
  'NumberFormat',
  'DateTimeFormat',
  'ListFormat',
  'RelativeTimeFormat',
  'Collator',
  'PluralRules',
]);

/**
 * Date methods that print English day and month names whatever the language - "Thu, 25 Sep 2026" -
 * and take no locale, so there is nothing to pass. One printed a webhook's expiry on the Spanish
 * Service configuration page.
 */
const ENGLISH_ONLY_DATE_METHODS = new Set(['toUTCString', 'toGMTString', 'toDateString', 'toTimeString']);

/**
 * Files allowed to use them directly.
 *
 * The module that owns locale selection; the lint checks themselves, whose own sorting is for a
 * deterministic developer report rather than for a reader; and test code, which may legitimately
 * format for a fixed locale in order to assert on the result.
 */
const ALLOWED = [/^i18n\/locale\.ts$/, /^i18n\/lint\//, /\.test\.tsx?$/, /^test\//];

function sourceFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      sourceFiles(full, out);
      continue;
    }
    const rel = relative(SRC, full).split(sep).join('/');
    if (!/\.tsx?$/.test(rel) || /\.d\.ts$/.test(rel)) continue;
    if (ALLOWED.some((pattern) => pattern.test(rel))) continue;
    out.push(full);
  }
  return out;
}

/** True when the argument in the locale position is absent or literally `undefined`. */
function hasNoExplicitLocale(argument: ts.Expression | undefined): boolean {
  if (!argument) return true;
  return argument.kind === ts.SyntaxKind.UndefinedKeyword || argument.getText() === 'undefined';
}

export function findAmbientLocaleFormatting(sourceText: string, fileName: string): string[] {
  const source = ts.createSourceFile(
    fileName,
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX,
  );
  const findings: string[] = [];

  const report = (node: ts.Node, what: string) => {
    const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
    findings.push(`${fileName}:${line + 1}: ${what}`);
  };

  const visit = (node: ts.Node): void => {
    // Matched on the method name rather than the shape of the call, so
    // `Number.prototype.toLocaleString.call(x)` is caught as well as `x.toLocaleString()`.
    if (ts.isPropertyAccessExpression(node) && LOCALE_METHODS.has(node.name.text)) {
      const localeIndex = LOCALE_METHODS.get(node.name.text)!;
      const call =
        ts.isCallExpression(node.parent) && node.parent.expression === node ? node.parent : undefined;
      if (!call || hasNoExplicitLocale(call.arguments[localeIndex])) {
        report(node, `${node.name.text}() with no explicit locale`);
      }
    }

    if (ts.isPropertyAccessExpression(node) && ENGLISH_ONLY_DATE_METHODS.has(node.name.text)) {
      report(node, `${node.name.text}() prints English in every language`);
    }

    if (
      ts.isNewExpression(node) &&
      ts.isPropertyAccessExpression(node.expression) &&
      ts.isIdentifier(node.expression.expression) &&
      node.expression.expression.text === 'Intl' &&
      INTL_CONSTRUCTORS.has(node.expression.name.text) &&
      hasNoExplicitLocale(node.arguments?.[0])
    ) {
      report(node, `new Intl.${node.expression.name.text}() with no explicit locale`);
    }

    ts.forEachChild(node, visit);
  };

  ts.forEachChild(source, visit);
  return findings;
}

describe('Locale-aware formatting', () => {
  it('formats every number and date through the portal language, not the browser locale', () => {
    const offenders: string[] = [];
    for (const file of sourceFiles(SRC)) {
      const rel = relative(SRC, file).split(sep).join('/');
      offenders.push(...findAmbientLocaleFormatting(readFileSync(file, 'utf8'), rel));
    }

    expect(
      offenders,
      "These use the browser locale, not the reader's language. Use formatNumber /\n" +
        'formatDateParts / compareStrings from src/i18n, or pass activeLocale() explicitly:\n  ' +
        offenders.join('\n  '),
    ).toEqual([]);
  });

  /**
   * Proved in both directions, so the check cannot be green because it is looking at nothing. The
   * multi-line case is the one a line-based regex silently misses.
   */
  describe('the check itself', () => {
    const scan = (code: string) => findAmbientLocaleFormatting(code, 'components/Sample.tsx');

    it('catches a bare toLocaleString', () => {
      expect(scan('export const f = (v) => v.toLocaleString();')).toHaveLength(1);
    });

    it('catches an explicit undefined locale', () => {
      expect(
        scan("export const f = (d) => d.toLocaleDateString(undefined, { day: 'numeric' });"),
      ).toHaveLength(1);
    });

    it('catches a call wrapped across several lines', () => {
      expect(
        scan('export const f = new Intl.NumberFormat(\n  undefined,\n  { style: "percent" },\n);'),
      ).toHaveLength(1);
    });

    it('catches localeCompare, which sorts a Spanish list into an English alphabet', () => {
      expect(scan('export const f = (a, b) => a.localeCompare(b);')).toHaveLength(1);
    });

    it('catches toUTCString, which prints English day and month names in every language', () => {
      expect(scan('export const f = (d) => new Date(d).toUTCString();')).toHaveLength(1);
      expect(scan('export const f = (d) => d.toDateString();')).toHaveLength(1);
      // A helper that happens to share the name is a plain call, not a Date method.
      expect(scan('export const f = (d) => toDateString(d);')).toEqual([]);
    });

    it('accepts a call given a real locale', () => {
      expect(scan('export const f = new Intl.NumberFormat(activeLocale(), options);')).toEqual([]);
      expect(scan('export const f = (a, b) => a.localeCompare(b, activeLocale());')).toEqual([]);
    });

    it('accepts the locale-aware helpers', () => {
      expect(scan('export const f = (v) => formatNumber(v);')).toEqual([]);
    });
  });
});
