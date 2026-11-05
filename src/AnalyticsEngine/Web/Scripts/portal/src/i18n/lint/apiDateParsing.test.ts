// @vitest-environment node
import { describe, it, expect } from 'vitest';
import ts from 'typescript';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';

/**
 * No date string is parsed with `new Date(string)` or `Date.parse(string)`: a date from the API goes
 * through `parseApiDate` in `src/i18n/apiDate.ts`.
 *
 * The API writes a `DateTime` read from SQL with no offset, and `new Date("2026-10-19T00:00:00")` is
 * the browser's local midnight - the previous UTC day for every reader east of UTC. Helpers that then
 * format in UTC printed every Teams Explorer week, agent-cost day and Copilot Adoption snapshot date
 * one day early, for every Spanish reader, all year. Nothing at the call site says whether a string
 * carries its zone, so the rule is about the parse rather than the endpoint; a string that already
 * ends in `Z` passes through `parseApiDate` unchanged.
 *
 * The check asks the TypeScript checker for the argument's type, so copying a `Date`, a time in
 * milliseconds and `new Date(Date.UTC(...))` are never reported. Packages are left out of the
 * program to keep it fast, so a value whose type comes from one (React state, say) has no type here
 * and is treated like a string - which, for something handed to `new Date`, is what it usually is.
 */

const SRC = join(process.cwd(), 'src');

/**
 * Values that are local wall-clock time with no zone recorded. Reading one as UTC would move it by an
 * offset nobody can recover, so these keep `new Date`. Keyed on the file and the exact argument, so
 * any other parse in the same file is still checked.
 */
const LOCAL_WALL_CLOCK = new Map<string, string>([
  ['pages/InstallLogPage.tsx|e.dateApplied', "The installer's DateTime.Now on whichever machine ran it; that machine's zone is not stored."],
]);

/** The parser itself, the lint checks, and tests, which build dates for fixed instants on purpose. */
const SKIPPED = [/^i18n\/apiDate\.ts$/, /^i18n\/lint\//, /\.test\.tsx?$/, /^test\//];

const OPTIONS: ts.CompilerOptions = {
  target: ts.ScriptTarget.ES2022,
  lib: ['lib.es2022.d.ts', 'lib.dom.d.ts', 'lib.dom.iterable.d.ts'],
  module: ts.ModuleKind.ESNext,
  moduleResolution: ts.ModuleResolutionKind.Bundler,
  allowImportingTsExtensions: true,
  jsx: ts.JsxEmit.ReactJSX,
  strict: true,
  noEmit: true,
  skipLibCheck: true,
  types: [],
};

const toPosix = (path: string) => path.split(sep).join('/');

/** A program over `files` (absolute path -> text) and TypeScript's own libs, and nothing from node_modules. */
function programOf(files: Map<string, string>): ts.Program {
  const libDir = toPosix(dirname(ts.getDefaultLibFilePath(OPTIONS)));
  const isLib = (file: string) => toPosix(file).startsWith(`${libDir}/`);
  const host = ts.createCompilerHost(OPTIONS, true);
  host.fileExists = (file) => files.has(toPosix(file)) || (isLib(file) && ts.sys.fileExists(file));
  host.readFile = (file) => files.get(toPosix(file)) ?? (isLib(file) ? ts.sys.readFile(file) : undefined);
  host.directoryExists = (dir) => toPosix(dir).startsWith(toPosix(SRC)) || toPosix(dir).startsWith(libDir);
  host.getDirectories = () => [];
  host.getSourceFile = (file, languageVersion) => {
    const text = host.readFile(file);
    return text === undefined ? undefined : ts.createSourceFile(file, text, languageVersion, true);
  };
  return ts.createProgram([...files.keys()], OPTIONS, host);
}

/** The one argument of `new Date(x)` or `Date.parse(x)`; anything else is not a parse of a string. */
function parsedArgument(node: ts.Node): ts.Expression | undefined {
  if (ts.isNewExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'Date') {
    return node.arguments?.length === 1 ? node.arguments[0] : undefined;
  }
  if (
    ts.isCallExpression(node) &&
    ts.isPropertyAccessExpression(node.expression) &&
    ts.isIdentifier(node.expression.expression) &&
    node.expression.expression.text === 'Date' &&
    node.expression.name.text === 'parse'
  ) {
    return node.arguments.length === 1 ? node.arguments[0] : undefined;
  }
  return undefined;
}

/** True when the checker says the argument is or may be a string, or cannot say what it is. */
function mayBeString(checker: ts.TypeChecker, argument: ts.Expression): boolean {
  const type = checker.getTypeAtLocation(argument);
  const parts = type.isUnion() ? type.types : [type];
  return parts.some((part) => (part.flags & (ts.TypeFlags.StringLike | ts.TypeFlags.Any | ts.TypeFlags.Unknown)) !== 0);
}

/** A literal that ends by stating its zone, like `${day}T00:00:00Z`, says what it means. */
function statesItsZone(argument: ts.Expression): boolean {
  const tail = ts.isTemplateExpression(argument)
    ? argument.templateSpans[argument.templateSpans.length - 1].literal.text
    : ts.isStringLiteral(argument) || ts.isNoSubstitutionTemplateLiteral(argument)
      ? argument.text
      : undefined;
  return tail !== undefined && /(?:Z|[+-]\d{2}:\d{2})$/.test(tail);
}

interface DateParseScan {
  findings: string[];
  /** The `LOCAL_WALL_CLOCK` entries that matched a parse, so a stale entry can be reported. */
  exemptionsUsed: Set<string>;
}

/** Every parse of a date string in `files` (portal-relative path -> source text). */
function findStringDateParses(files: Map<string, string>): DateParseScan {
  const absolute = new Map([...files].map(([rel, text]) => [toPosix(join(SRC, rel)), text]));
  const program = programOf(absolute);
  const checker = program.getTypeChecker();
  const findings: string[] = [];
  const exemptionsUsed = new Set<string>();

  for (const source of program.getSourceFiles()) {
    if (!absolute.has(source.fileName)) continue;
    const rel = toPosix(relative(SRC, source.fileName));
    if (SKIPPED.some((pattern) => pattern.test(rel))) continue;

    const visit = (node: ts.Node): void => {
      const argument = parsedArgument(node);
      if (argument && mayBeString(checker, argument) && !statesItsZone(argument)) {
        const key = `${rel}|${argument.getText(source)}`;
        if (LOCAL_WALL_CLOCK.has(key)) {
          exemptionsUsed.add(key);
        } else {
          const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
          findings.push(`${rel}:${line + 1}: ${node.getText(source)}`);
        }
      }
      ts.forEachChild(node, visit);
    };
    visit(source);
  }

  return { findings, exemptionsUsed };
}

/** The portal's own source, as portal-relative path -> text; tests are left out, they never reach a reader. */
function portalSources(dir: string = SRC, out: Map<string, string> = new Map()): Map<string, string> {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const rel = toPosix(relative(SRC, full));
    if (statSync(full).isDirectory()) {
      if (rel !== 'test') portalSources(full, out);
    } else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry)) {
      out.set(rel, readFileSync(full, 'utf8'));
    }
  }
  return out;
}

describe('Dates from the API', () => {
  // Type-checks the portal's source with the TypeScript compiler; seconds on a laptop, more on a
  // loaded CI runner.
  it('parses every date string with parseApiDate, never new Date(string)', { timeout: 60000 }, () => {
    const { findings, exemptionsUsed } = findStringDateParses(portalSources());

    expect(
      findings,
      'These parse a date string with new Date() or Date.parse(), which reads a date the API sent with\n' +
        "no offset as the browser's local time - the day before, east of UTC. Use parseApiDate from\n" +
        'src/i18n instead (a value that already ends in Z is unchanged by it):\n  ' +
        findings.join('\n  '),
    ).toEqual([]);

    expect(
      [...LOCAL_WALL_CLOCK.keys()].filter((key) => !exemptionsUsed.has(key)),
      'These LOCAL_WALL_CLOCK entries no longer match any parse; remove them.',
    ).toEqual([]);
  });

  /**
   * Proved in both directions, so the check cannot be green because it is looking at nothing, and
   * cannot be turned off by someone tired of a false positive.
   */
  describe('the check itself', () => {
    const scan = (code: string, file = 'components/Sample.tsx') =>
      findStringDateParses(new Map([[file, code]])).findings;

    it('catches a date string from an API payload', () => {
      expect(scan('type Row = { weekStart: string };\nexport const f = (row: Row) => new Date(row.weekStart);')).toHaveLength(1);
      expect(scan('export const f = (iso: string | null) => (iso ? new Date(iso) : null);')).toHaveLength(1);
      expect(scan('export const f = (iso: string) => Date.parse(iso);')).toHaveLength(1);
      expect(scan('export const f = (iso: string) => new Date(\n  iso,\n);')).toHaveLength(1);
    });

    it('catches a value whose type it cannot see, such as one from a package', () => {
      expect(scan("import { payload } from 'some-package';\nexport const f = () => new Date(payload.when);")).toHaveLength(1);
    });

    it('accepts dates that are not parsed from a string', () => {
      expect(
        scan(
          [
            'export const a = new Date();',
            'export const b = new Date(Date.UTC(2026, 9, 19));',
            'export const c = (d: Date) => new Date(d);',
            'export const d = (ms: number) => new Date(ms);',
            'export const e = (d: Date) => new Date(d.getTime() - 86_400_000);',
            'export const f = new Date(2026, 9, 19);',
          ].join('\n'),
        ),
      ).toEqual([]);
    });

    it('accepts a literal that states its zone, and parseApiDate', () => {
      expect(scan('export const f = (day: string) => new Date(`${day}T00:00:00Z`);')).toEqual([]);
      expect(scan('declare function parseApiDate(v: string): Date;\nexport const f = (iso: string) => parseApiDate(iso);')).toEqual([]);
    });

    it('excuses a local wall-clock value only for its own argument', () => {
      const code = 'type E = { dateApplied: string; other: string };\nexport const f = (e: E) => [new Date(e.dateApplied), new Date(e.other)];';
      expect(scan(code, 'pages/InstallLogPage.tsx')).toEqual(['pages/InstallLogPage.tsx:2: new Date(e.other)']);
      expect(scan(code, 'pages/OtherPage.tsx')).toHaveLength(2);
    });
  });
});
