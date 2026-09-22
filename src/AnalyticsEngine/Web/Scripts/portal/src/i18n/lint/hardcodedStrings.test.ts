// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

import { findHardcodedStrings, looksLikeText, shouldScan, type Finding } from './hardcodedStrings';

/**
 * The gate: no text reaches a user without going through the translation catalog.
 *
 * This is what makes "every UI change ships with its translations" a rule rather than an
 * aspiration. The TypeScript build already proves that every catalog key exists in Spanish; this
 * proves that every string a user can read *is* a catalog key.
 *
 * When it fails, the message is the worklist. Move each string into the right
 * `src/i18n/catalog/en/<area>.ts`, translate it in `../es/<area>.ts`, and call it through `t()`.
 * If - and only if - the text genuinely reads the same in Spanish (a Microsoft product name, a
 * file format, a unit), add it to `ALLOWED_LITERALS` in `./allowList.ts` with the others.
 */

const SRC = join(process.cwd(), 'src');

function sourceFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      sourceFiles(full, out);
      continue;
    }
    const rel = relative(SRC, full).split(sep).join('/');
    if (shouldScan(rel)) out.push(full);
  }
  return out;
}

function scanPortal(): Finding[] {
  const findings: Finding[] = [];
  for (const file of sourceFiles(SRC)) {
    const rel = relative(SRC, file).split(sep).join('/');
    findings.push(...findHardcodedStrings(readFileSync(file, 'utf8'), rel));
  }
  return findings;
}

function describeFindings(findings: Finding[]): string {
  const byFile = new Map<string, Finding[]>();
  for (const finding of findings) {
    const list = byFile.get(finding.file) ?? [];
    list.push(finding);
    byFile.set(finding.file, list);
  }

  const lines: string[] = [];
  for (const file of [...byFile.keys()].sort()) {
    lines.push(`  ${file}`);
    for (const finding of byFile.get(file)!) {
      const via = finding.via ? ` (${finding.via})` : '';
      const text = finding.text.length > 90 ? `${finding.text.slice(0, 87)}...` : finding.text;
      lines.push(`    ${finding.line}: [${finding.kind}${via}] ${text}`);
    }
  }
  return lines.join('\n');
}

describe('Untranslated text', () => {
  it('finds no user-facing string outside the translation catalog', () => {
    const findings = scanPortal();

    expect(
      findings.length,
      findings.length === 0
        ? ''
        : `${findings.length} user-facing string(s) are not translated.\n` +
            'Move each into src/i18n/catalog/en/<area>.ts, translate it in ../es/<area>.ts and\n' +
            "render it with t('<key>'). Only add to ALLOWED_LITERALS when the Spanish is identical.\n\n" +
            describeFindings(findings),
    ).toBe(0);
  });
});

/**
 * The check has to be believed before it can be relied on, so these prove it in both directions:
 * that it catches the mistakes it claims to catch, and that it stays quiet on the things the
 * portal legitimately does.
 *
 * Without the negative half, the whole gate could be silently disabled by a bad regex and every
 * run would still be green.
 */
describe('The untranslated-text check itself', () => {
  const scan = (code: string) => findHardcodedStrings(code, 'components/Sample.tsx');

  it('catches text typed straight into JSX', () => {
    const findings = scan('export const A = () => <Text>Licensed users</Text>;');
    expect(findings.map((f) => f.text)).toEqual(['Licensed users']);
    expect(findings[0].kind).toBe('jsx-text');
  });

  it('catches a user-facing attribute given a literal', () => {
    const findings = scan('export const A = () => <Button aria-label="Hide these warnings" />;');
    expect(findings.map((f) => f.text)).toEqual(['Hide these warnings']);
    expect(findings[0].via).toBe('aria-label');
  });

  it('catches an interpolated string built in a template literal', () => {
    const findings = scan('export const A = ({ n }) => <Text title={`${n} unused licences`} />;');
    expect(findings.map((f) => f.text)).toEqual(['unused licences']);
  });

  it('catches a label in a constant table, which is where most of this portal keeps its text', () => {
    const findings = scan("export const BANDS = [{ key: 'daily', label: 'Daily user' }];");
    expect(findings.map((f) => f.text)).toEqual(['Daily user']);
    expect(findings[0].via).toBe('label');
  });

  it('catches a toast, which never goes near JSX', () => {
    const findings = scan("import toast from './toast'; export const go = () => toast.success('Saved your changes');");
    expect(findings.map((f) => f.text)).toEqual(['Saved your changes']);
  });

  it('catches a literal rendered through a ternary', () => {
    const findings = scan('export const A = ({ on }) => <Text>{on ? "Switched on" : "Switched off"}</Text>;');
    expect(findings.map((f) => f.text).sort()).toEqual(['Switched off', 'Switched on']);
  });

  it('stays quiet on a translated call, whose argument is a key rather than text', () => {
    expect(scan("export const A = () => <Text>{t('common.action.print')}</Text>;")).toEqual([]);
  });

  it('stays quiet on Fluent prop values, class names and style tokens', () => {
    const findings = scan(
      'export const A = () => <Button appearance="transparent" weight="semibold" className="brand" data-print="hide" style={{ padding: "12px" }} />;',
    );
    expect(findings).toEqual([]);
  });

  it('stays quiet on an import path and a route', () => {
    expect(
      findHardcodedStrings(
        "import x from '../components/toast'; export const HOME = '/insights/overview';",
        'navigation.tsx',
      ),
    ).toEqual([]);
  });

  it('stays quiet on prose inside a comment', () => {
    expect(scan('// Licensed users are the ones we bill for.\nexport const A = () => <div />;')).toEqual([]);
  });

  it('treats a Microsoft product name as language-neutral, but not an ordinary English word', () => {
    expect(looksLikeText('SharePoint')).toBe(false);
    expect(looksLikeText('Licence')).toBe(true);
  });

  it('scans components and pages, and skips the catalog and the tests', () => {
    expect(shouldScan('pages/HealthPage.tsx')).toBe(true);
    expect(shouldScan('components/shared/KpiGrid.tsx')).toBe(true);
    expect(shouldScan('i18n/catalog/en/common.ts')).toBe(false);
    expect(shouldScan('pages/HealthPage.test.tsx')).toBe(false);
    expect(shouldScan('test/renderWithProvider.tsx')).toBe(false);
  });

  it('reads every page and component in the portal, so nothing is skipped by accident', () => {
    const files = sourceFiles(SRC).map((f) => relative(SRC, f).split(sep).join('/'));
    expect(files).toContain('App.tsx');
    expect(files).toContain('navigation.tsx');
    // A floor rather than an exact count: this must not need editing every time a page is added,
    // but it must notice if the walk stops finding the tree.
    expect(files.filter((f) => f.startsWith('pages/')).length).toBeGreaterThanOrEqual(14);
    expect(files.filter((f) => f.startsWith('components/')).length).toBeGreaterThanOrEqual(80);
  });
});
