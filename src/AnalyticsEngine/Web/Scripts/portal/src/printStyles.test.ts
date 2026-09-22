import { describe, it, expect } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

/**
 * The print stylesheet contract.
 *
 * Printing is split across two files that cannot see each other: components mark themselves up
 * with `data-print` attributes, and the `@media print` block in index.css decides what those
 * attributes mean. Vitest runs with `css: false`, so a component test can prove the attribute is
 * there but never that it does anything - and nobody proof-reads a printout on every change. That
 * gap is what these tests close.
 *
 * Asserted against the stylesheet text rather than the CSSOM because jsdom's CSS parser is
 * incomplete (notably around `@page` and `print-color-adjust`) and silently drops what it does not
 * understand, which would turn a missing rule into a passing test.
 */

// Resolved from the working directory rather than import.meta.url: Vite rewrites module URLs to
// its own scheme, so fileURLToPath on them fails. Vitest runs from the package root (where
// vitest.config.ts lives), which makes this stable.
const SRC_DIR = join(process.cwd(), 'src');
const CSS_PATH = join(SRC_DIR, 'index.css');

/** index.css with comments removed and quotes normalised, so selectors compare by meaning. */
function stylesheet(): string {
  return readFileSync(CSS_PATH, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/"/g, "'");
}

/** The body of the `@media print` block, found by brace matching rather than a greedy regex. */
function printBlock(): string {
  const css = stylesheet();
  const at = css.indexOf('@media print');
  if (at < 0) throw new Error('index.css has no @media print block.');

  const open = css.indexOf('{', at);
  let depth = 0;
  for (let i = open; i < css.length; i++) {
    if (css[i] === '{') depth += 1;
    else if (css[i] === '}') {
      depth -= 1;
      if (depth === 0) return css.slice(open + 1, i);
    }
  }
  throw new Error('index.css has an unterminated @media print block.');
}

/**
 * Every declaration that applies to `selector` when printing.
 *
 * Selectors are matched against each comma-separated part of a rule's prelude, so a rule shared by
 * several selectors counts for all of them - that is how the stylesheet is actually written.
 */
function printDeclarationsFor(selector: string): string {
  const matched = [...printBlock().matchAll(/([^{}]+)\{([^{}]*)\}/g)].filter((rule) =>
    rule[1].split(',').some((part) => part.trim() === selector),
  );
  return matched.map((rule) => rule[2].replace(/\s+/g, ' ').trim()).join(' ');
}

/** Every `data-print` value the components actually use. */
function attributeValuesUsedInComponents(): Set<string> {
  const values = new Set<string>();
  for (const entry of readdirSync(SRC_DIR, { recursive: true, withFileTypes: true })) {
    if (!entry.isFile() || !/\.tsx?$/.test(entry.name) || entry.name.endsWith('.test.tsx')) continue;
    const source = readFileSync(join(entry.parentPath, entry.name), 'utf8');
    for (const match of source.matchAll(/data-print=['"]([a-z-]+)['"]/g)) values.add(match[1]);
  }
  return values;
}

describe('print stylesheet', () => {
  it('removes anything marked as chrome from the printed page', () => {
    // `!important` is not optional here: Griffel injects the app's own styles into <head> at
    // runtime, so they come after this stylesheet at equal specificity and would otherwise win.
    expect(printDeclarationsFor("[data-print='hide']")).toMatch(/display:\s*none\s*!important/);
  });

  it('gives the report the whole sheet by flattening the layout wrappers', () => {
    // The screen shell centres the page in a 1120px column inside a 24px gutter, beside a nav
    // rail, under a shell floored at the viewport height. On paper each of those only makes the
    // report narrower or pushes it down the page.
    const declarations = printDeclarationsFor("[data-print='content']");
    expect(declarations).toMatch(/max-width:\s*none\s*!important/);
    expect(declarations).toMatch(/padding:\s*0\s*!important/);
    expect(declarations).toMatch(/margin:\s*0\s*!important/);
    expect(declarations).toMatch(/min-height:\s*0\s*!important/);
    expect(declarations).toMatch(/display:\s*block\s*!important/);
  });

  it('reveals the print-only caption that says what the printout is a report of', () => {
    expect(printDeclarationsFor("[data-print='only']")).toMatch(/display:\s*block\s*!important/);
  });

  it('drops the viewport-height floor so the report does not start on page two', () => {
    const declarations = printDeclarationsFor('#root') + printDeclarationsFor('#root > *');
    expect(declarations).toMatch(/min-height:\s*0\s*!important/);
  });

  it('keeps colour, because colour is what the bands and chart series mean', () => {
    // Browsers strip background colours when printing. In this report that is not a cosmetic loss:
    // the adoption band of a segment and the series in every chart are colour alone.
    const declarations = printDeclarationsFor('*');
    expect(declarations).toMatch(/[^-]print-color-adjust:\s*exact/);
    expect(declarations).toMatch(/-webkit-print-color-adjust:\s*exact/);
  });

  it('repeats table headers across pages instead of forbidding tables to break', () => {
    // The user lists run to hundreds of rows. A table told not to break would overflow the sheet
    // and lose everything past the first page, so it is the rows that are kept whole.
    expect(printDeclarationsFor('thead')).toMatch(/display:\s*table-header-group/);
    expect(printDeclarationsFor('tr')).toMatch(/[^-]break-inside:\s*avoid/);
    expect(printDeclarationsFor('tr')).toMatch(/page-break-inside:\s*avoid/);
  });

  it('starts each act of the report on a new sheet, and never strands a heading', () => {
    // The legacy `page-break-*` aliases matter: Safari still only implements those, and a print
    // stylesheet that silently no-ops on one browser looks exactly like one nobody wrote.
    expect(printDeclarationsFor("[data-print='page-break']")).toMatch(/break-before:\s*page/);
    expect(printDeclarationsFor("[data-print='page-break']")).toMatch(/page-break-before:\s*always/);
    expect(printDeclarationsFor("[data-print='page-break']")).toMatch(/[^-]break-after:\s*avoid/);
    // Act 1 does not start a sheet of its own - it belongs with the KPI tiles on the front page -
    // but it must still not be the last thing printed on that page.
    expect(printDeclarationsFor("[data-print='keep-with-next']")).toMatch(/[^-]break-after:\s*avoid/);
    expect(printDeclarationsFor("[data-print='keep-with-next']")).not.toMatch(/break-before:\s*page/);
  });

  it('returns the section stack to block flow, without which those breaks do nothing', () => {
    // Fragmentation inside a flex or grid container is unreliable across browsers, and the report's
    // sections are flex items of a column stack, so `break-before` on them is simply ignored.
    expect(printDeclarationsFor("[data-print='flow']")).toMatch(/display:\s*block\s*!important/);
    // The stack spaced itself with `gap`, which block flow does not have.
    expect(printDeclarationsFor("[data-print='flow'] > *")).toMatch(/margin-bottom:\s*16px/);
  });

  it('does not print an open tooltip over the report', () => {
    // Fluent portals tooltips, popovers, menus and toasts to <body>, outside the app root. Clicking
    // Print leaves the pointer on the button, so its tooltip is open at the moment the page is
    // captured: without this the printout carried a tooltip over the top of the report every time.
    const declarations =
      printDeclarationsFor('body > *:not(#root)') + printDeclarationsFor("[role='tooltip']");
    expect(declarations).toMatch(/display:\s*none\s*!important/);
  });

  it('hides the overlays by the id the app actually mounts on', () => {
    // `body > *:not(#root)` is only "everything except the app" for as long as the app mounts on
    // #root. Renaming it would print an empty page - the app itself would be the thing hidden.
    const main = readFileSync(join(SRC_DIR, 'main.tsx'), 'utf8');
    expect(main).toMatch(/getElementById\(['"]root['"]\)/);
  });

  it('repeats the product, build and repository at the foot of every page', () => {
    // A running footer has to repeat on every page *and* have room reserved for it. `position:
    // fixed` gives the first and not the second - and Chromium mis-resolves a negative `bottom` in
    // paged media, which is how this shipped printing the footer across the top of each sheet.
    // Measured in Chromium 153 by printing to PDF and reading the text positions back: at
    // `bottom: -11mm` the footer sat 50pt from the top of the page; at `bottom: 0` it sat at the
    // foot but the last body line ran to 743.6pt under a footer starting at 737.7pt. A real table
    // section is the only construct that does both, so the footer is a <tfoot>.
    const declarations = printDeclarationsFor("[data-print='footer']");
    expect(declarations).toMatch(/display:\s*table-footer-group\s*!important/);
    expect(declarations).not.toMatch(/position:\s*fixed/);

    // A table section only repeats if its ancestor is actually a table when printing - on screen
    // the whole thing is flattened to block flow, so this is what switches it on.
    expect(printDeclarationsFor("[data-print='shell']")).toMatch(/display:\s*table\s*!important/);
    expect(printDeclarationsFor("[data-print='shell'] > tbody")).toMatch(
      /display:\s*table-row-group\s*!important/,
    );

    // Padding and borders do not apply to a table-footer-group box, so the rule and the gap that
    // separate the footer from the report have to be on the cell.
    const cell = printDeclarationsFor("[data-print='footer'] td");
    expect(cell).toMatch(/padding-top:/);
    expect(cell).toMatch(/border-top:/);
  });

  it('exempts the shell row from the rule that keeps table rows whole', () => {
    // `tr { break-inside: avoid }` keeps a report table's rows from splitting across a page. The
    // shell's single row *is* the whole report, so leaving it in scope would forbid the very break
    // every page after the first depends on.
    const declarations = printDeclarationsFor("[data-print='shell'] > tbody > tr");
    expect(declarations).toMatch(/[^-]break-inside:\s*auto\s*!important/);
    expect(declarations).toMatch(/page-break-inside:\s*auto\s*!important/);
  });

  it('has a rule for every data-print value the components use', () => {
    // The drift guard: an attribute the stylesheet has never heard of is dead markup, and reads in
    // review as though printing has been handled when it has not.
    const used = attributeValuesUsedInComponents();
    expect(used.size).toBeGreaterThan(0);

    for (const value of used) {
      expect(printDeclarationsFor(`[data-print='${value}']`), `No @media print rule for data-print="${value}"`)
        .not.toBe('');
    }
  });
});
