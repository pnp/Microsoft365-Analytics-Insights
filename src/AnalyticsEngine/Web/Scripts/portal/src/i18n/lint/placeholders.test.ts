// @vitest-environment node
import { describe, it, expect } from 'vitest';
import ts from 'typescript';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

import { EN_CATALOG } from '../catalog';
import { shouldScan } from './hardcodedStrings';

/**
 * Every `{placeholder}` a catalog string contains must be supplied at the call site, and every
 * value supplied must be one the string contains.
 *
 * Neither the type system nor the other checks can see this. `TFunction`'s `values` argument is
 * optional, so `t('common.unit.user.one')` compiles, passes the untranslated-text check and the
 * catalog checks, and renders the literal text `{count}` on screen. The mirror image is just as
 * quiet: `t('health.lastRun', { when, extra })` drops `extra` silently, which usually means a
 * figure the sentence was supposed to carry has vanished from it.
 *
 * `catalog.test.ts` already proves the two languages agree about which placeholders exist. This
 * proves the call sites agree with them.
 */

const SRC = join(process.cwd(), 'src');

const PLACEHOLDER = /\{(\w+)\}/g;

/** Helpers whose first argument is a catalog key and whose second is the values object. */
const KEY_THEN_VALUES = new Set(['t', 'tNode', 'translateActive']);

/**
 * The catalog keys a `t()` key argument can resolve to.
 *
 * Usually one literal. But `t(plural(n, 'x.one', 'x.other'), { count })` passes a *call* as the
 * key, and both of its keys have to be checked - a missing `{ count }` there renders the literal
 * text `{count}` just as surely, and this is the most common shape in the portal for a string that
 * has a placeholder at all. Skipping it, which an implementation that only accepts a string
 * literal does, leaves the check green over exactly the calls most likely to be wrong.
 */
function resolvedKeys(argument: ts.Expression | undefined): string[] {
  if (!argument) return [];
  if (ts.isStringLiteral(argument)) {
    return argument.text in EN_CATALOG ? [argument.text] : [];
  }
  if (ts.isCallExpression(argument) && calleeName(argument) === 'plural') {
    return argument.arguments
      .slice(1, 3)
      .filter((a): a is ts.StringLiteral => ts.isStringLiteral(a) && a.text in EN_CATALOG)
      .map((a) => a.text);
  }
  // A key chosen at runtime - `t(row.labelKey)`, `t(STATUS_KEYS[status])` - cannot be resolved
  // statically. Those are nearly always simple labels with no placeholders.
  return [];
}

function placeholdersOf(key: string): Set<string> {
  const text = EN_CATALOG[key];
  if (typeof text !== 'string') return new Set();
  return new Set([...text.matchAll(PLACEHOLDER)].map((m) => m[1]));
}

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

function calleeName(node: ts.CallExpression): string {
  const expression = node.expression;
  if (ts.isIdentifier(expression)) return expression.text;
  if (ts.isPropertyAccessExpression(expression)) return expression.name.text;
  return '';
}

/**
 * Checks one file's `t()` calls.
 *
 * Only calls with a literal key are checked. A key chosen at runtime - `t(row.labelKey)`,
 * `t(STATUS_KEYS[status])` - cannot be resolved statically, and those are nearly always simple
 * labels with no placeholders anyway. Calls whose values are spread (`t(key, { ...values })`) are
 * skipped rather than guessed at.
 */
export function checkPlaceholders(sourceText: string, fileName: string): string[] {
  const source = ts.createSourceFile(
    fileName,
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX,
  );
  const problems: string[] = [];

  const visit = (node: ts.Node): void => {
    if (ts.isCallExpression(node) && KEY_THEN_VALUES.has(calleeName(node))) {
      const [keyArgument, valuesArgument] = node.arguments;
      const keys = resolvedKeys(keyArgument);
      if (keys.length > 0) {
        let supplied: Set<string> | null = new Set();
        if (valuesArgument) {
          if (ts.isObjectLiteralExpression(valuesArgument)) {
            for (const property of valuesArgument.properties) {
              if (ts.isSpreadAssignment(property)) {
                supplied = null;
                break;
              }
              const name = property.name;
              if (name && (ts.isIdentifier(name) || ts.isStringLiteral(name))) {
                supplied.add(name.text);
              } else {
                supplied = null;
                break;
              }
            }
          } else {
            supplied = null;
          }
        }

        if (supplied) {
          const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
          for (const key of keys) {
            const expected = placeholdersOf(key);
            const where = `${fileName}:${line + 1} ${key}`;
            const missing = [...expected].filter((name) => !supplied.has(name));
            if (missing.length > 0) {
              problems.push(`${where}: renders literally, no value for {${missing.join('}, {')}}`);
            }
          }
          // An extra value is only wrong if NO resolved key wants it - with `plural()` the two
          // forms can legitimately differ, and supplying a name only one of them uses is fine.
          const wanted = new Set(keys.flatMap((key) => [...placeholdersOf(key)]));
          const extra = [...supplied].filter((name) => !wanted.has(name));
          if (extra.length > 0) {
            const { line: at } = source.getLineAndCharacterOfPosition(node.getStart(source));
            problems.push(
              `${fileName}:${at + 1} ${keys.join(' / ')}: silently dropped, not in the text: ${extra.join(', ')}`,
            );
          }
        }
      }
    }
    ts.forEachChild(node, visit);
  };

  ts.forEachChild(source, visit);
  return problems;
}

describe('Translation placeholders at the call site', () => {
  it('supplies every placeholder each string needs, and none it does not', { timeout: 30000 }, () => {
    const problems: string[] = [];
    for (const file of sourceFiles(SRC)) {
      const rel = relative(SRC, file).split(sep).join('/');
      problems.push(...checkPlaceholders(readFileSync(file, 'utf8'), rel));
    }

    expect(
      problems,
      'A placeholder with no value renders as literal text such as "{count}"; a value with no\n' +
        'placeholder is dropped, so a figure disappears from the sentence:\n  ' +
        problems.join('\n  '),
    ).toEqual([]);
  });

  /** Proved in both directions, so the check cannot be green because it is looking at nothing. */
  describe('the check itself', () => {
    const scan = (code: string) => checkPlaceholders(code, 'components/Sample.tsx');

    it('catches a placeholder the call site never supplies', () => {
      const problems = scan("const x = t('common.unit.user.one');");
      expect(problems).toHaveLength(1);
      expect(problems[0]).toContain('{count}');
    });

    it('catches a value the string has no placeholder for', () => {
      const problems = scan("const x = t('common.action.print', { count: 3 });");
      expect(problems).toHaveLength(1);
      expect(problems[0]).toContain('silently dropped');
    });

    it('accepts a call that supplies exactly the right values', () => {
      expect(scan("const x = t('common.unit.user.one', { count: 1 });")).toEqual([]);
      expect(scan("const x = t('common.action.print');")).toEqual([]);
    });

    /**
     * `t(plural(...))` is the shape most likely to need a placeholder and the one an
     * implementation that only accepts a string-literal key silently skips - so it is checked in
     * both directions explicitly.
     */
    it('checks both keys of a plural call', () => {
      const problems = scan(
        "const x = t(plural(n, 'common.unit.user.one', 'common.unit.user.other'));",
      );
      expect(problems).toHaveLength(2);
      expect(problems[0]).toContain('{count}');
    });

    it('accepts a plural call that supplies the count', () => {
      expect(
        scan("const x = t(plural(n, 'common.unit.user.one', 'common.unit.user.other'), { count: n });"),
      ).toEqual([]);
    });

    it('does not guess at a key or a values object it cannot resolve', () => {
      expect(scan('const x = t(route.labelKey);')).toEqual([]);
      expect(scan("const x = t('common.unit.user.one', values);")).toEqual([]);
      expect(scan("const x = t('common.unit.user.one', { ...values });")).toEqual([]);
    });
  });
});
