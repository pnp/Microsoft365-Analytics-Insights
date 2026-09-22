// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

/**
 * No number or date may be formatted in the browser's locale instead of the reader's language.
 *
 * `(1234).toLocaleString()` and `date.toLocaleDateString(undefined, …)` use whatever locale the
 * machine is configured with. That is not the same thing as the language the reader picked in the
 * portal, and the difference is not cosmetic: **`1,234` is one thousand two hundred and thirty-four
 * to an English reader and one point two three four to a Spanish one.** A figure formatted in the
 * wrong locale on a page used to justify licence spend is wrong by a factor of a thousand.
 *
 * `formatNumber` and `formatDateParts` in `src/i18n/locale.ts` follow the selected language, so
 * every such call must go through them.
 *
 * This exists because the class is invisible to the untranslated-text check: a `.toLocaleString()`
 * call contains no string at all. Three of these survived the initial conversion in
 * `components/shared/KpiGrid.tsx` - a module imported by twelve Copilot Adoption components - and
 * were found by review rather than by a test, which is exactly the gap this closes.
 */

const SRC = join(process.cwd(), 'src');

/** `toLocaleString`, `toLocaleDateString`, `toLocaleTimeString`, and a bare `Intl` constructor. */
const OFFENDERS = [
  /\.toLocale(?:String|DateString|TimeString)\s*\(/,
  /new\s+Intl\.(?:NumberFormat|DateTimeFormat|ListFormat|RelativeTimeFormat)\s*\(\s*(?:undefined|\)|'|")/,
];

/**
 * Files allowed to call them directly.
 *
 * Only the module that owns locale selection, and the tests that assert on what it produces.
 */
const ALLOWED = [/^i18n\/locale\.ts$/, /\.test\.tsx?$/, /^test\//];

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

describe('Locale-aware formatting', () => {
  it('formats every number and date through the portal language, not the browser locale', () => {
    const offenders: string[] = [];

    for (const file of sourceFiles(SRC)) {
      const rel = relative(SRC, file).split(sep).join('/');
      const lines = readFileSync(file, 'utf8').split('\n');
      lines.forEach((line, index) => {
        // Skip comments: locale.ts's rationale is quoted in several doc comments.
        if (/^\s*(?:\/\/|\*|\/\*)/.test(line)) return;
        if (OFFENDERS.some((pattern) => pattern.test(line))) {
          offenders.push(`${rel}:${index + 1}: ${line.trim()}`);
        }
      });
    }

    expect(
      offenders,
      'These format in the browser locale, not the reader\'s language. Use formatNumber /\n' +
        'formatDateParts / compareStrings from src/i18n:\n  ' +
        offenders.join('\n  '),
    ).toEqual([]);
  });

  /**
   * The check has to be believed before it is relied on, so it is proved in both directions on
   * sample text rather than only on the tree, which is green by construction once it passes.
   */
  it('recognises the calls it is looking for, and ignores an explicit locale', () => {
    const matches = (line: string) => OFFENDERS.some((pattern) => pattern.test(line));

    expect(matches('return value.toLocaleString();')).toBe(true);
    expect(matches("return d.toLocaleDateString(undefined, { day: 'numeric' });")).toBe(true);
    expect(matches('new Intl.NumberFormat(undefined, options)')).toBe(true);
    // Given a real locale, these are how locale.ts itself is implemented.
    expect(matches("new Intl.NumberFormat(activeLocaleTag, options)")).toBe(false);
    expect(matches('return formatNumber(value);')).toBe(false);
  });
});
