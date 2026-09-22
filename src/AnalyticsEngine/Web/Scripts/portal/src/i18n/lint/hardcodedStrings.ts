import ts from 'typescript';

import { EN_CATALOG } from '../catalog';
import { ALLOWED_LITERALS, IGNORED_FILE_PATTERNS } from './allowList';

/**
 * Finds text that would reach a user without going through the translation catalog.
 *
 * This is the mechanism behind the release rule "no UI change ships without translations". The
 * compiler already guarantees that every key in the catalog exists in every language; what it
 * cannot see is a string that was never put in the catalog at all. That is the failure this
 * catches, and it is the one that actually happens - nobody deletes a Spanish key on purpose, but
 * everybody adds a panel with the labels typed straight into the JSX.
 *
 * Written against the TypeScript AST rather than regexes on purpose. A regex cannot tell
 * `<Text>Licensed users</Text>` from a string inside a comment, a CSS value, an import path or a
 * `className`, and a check with false positives gets switched off within a month.
 */

export type FindingKind =
  | 'jsx-text'
  | 'jsx-attribute'
  | 'jsx-expression'
  | 'object-property'
  | 'notification'
  | 'phrase';

export interface Finding {
  /** Path relative to the portal's `src`, using forward slashes. */
  file: string;
  line: number;
  kind: FindingKind;
  /** The offending text, collapsed onto one line. */
  text: string;
  /** The attribute or property it came from, where that is what made it user-facing. */
  via?: string;
}

/**
 * JSX attributes whose value is read by a human.
 *
 * A whitelist, not a blacklist: the overwhelming majority of string-valued props are Fluent enum
 * values (`appearance="subtle"`, `weight="semibold"`), CSS class names and test ids, none of which
 * are text. Listing the handful that are shown keeps the check free of the noise that would
 * otherwise make it useless.
 */
const USER_FACING_ATTRIBUTES = new Set([
  'alt',
  'aria-description',
  'aria-label',
  'aria-placeholder',
  'aria-roledescription',
  'aria-valuetext',
  'blurb',
  'buttonLabel',
  'caption',
  'centreLabel',
  'content',
  'description',
  'emptyMessage',
  'gapNote',
  'header',
  'heading',
  'hint',
  'label',
  'note',
  'placeholder',
  'segmentLabel',
  'sublabel',
  'subtitle',
  'title',
  'tooltip',
  'unit',
  'valueLabel',
]);

/**
 * Object-literal properties whose value is rendered.
 *
 * The portal is built around declarative tables - KPI definitions, chart series, column
 * descriptors, band scales - so most of its visible text is a property value in a constant array
 * rather than a JSX child. Leaving these out would let an entire page of labels through.
 */
const USER_FACING_PROPERTIES = new Set([
  'blurb',
  'caption',
  'description',
  'formula',
  'heading',
  'hint',
  'how',
  'label',
  'meaning',
  'message',
  'note',
  'placeholder',
  'source',
  'subtitle',
  'summary',
  'title',
  'tooltip',
  'what',
]);

/** Calls that put a string in front of the user without any JSX being involved. */
const NOTIFICATION_CALLEES = new Set([
  'notify',
  'notifyError',
  'notifySuccess',
  'toast',
  'toast.error',
  'toast.success',
]);

/**
 * Calls whose string arguments are catalog keys or other machinery, never text.
 *
 * Without this, `t('common.action.print')` would be reported as a hardcoded string - the key looks
 * like prose to any check that only sees characters.
 */
const TRANSLATION_CALLEES = new Set(['plural', 't', 'tNode', 'translateStatic', 'translateActive']);

/**
 * Calls whose string arguments are for a developer, not a reader.
 *
 * Console diagnostics are read in devtools by whoever is debugging the portal, and translating
 * them would make a stack trace harder to search, not easier.
 */
const IGNORED_CALLEES = new Set([
  'console.debug',
  'console.error',
  'console.info',
  'console.log',
  'console.trace',
  'console.warn',
  // The i18n layer's own de-duplicated console warning.
  'warnOnce',
]);

const CATALOG_KEYS = new Set(Object.keys(EN_CATALOG));

/** Two consecutive letters somewhere - the cheapest test for "a human would read this". */
const HAS_WORD = /\p{L}\p{L}/u;

/** Three or more whitespace-separated runs: a phrase, not an identifier or a token. */
const IS_PHRASE = /(?:\S+\s+){2,}\S+/;

/**
 * Strings that look like prose to a word-counter but are not text.
 *
 * Almost all of them are CSS: this portal uses Griffel, so `gridTemplateColumns`, `transition`,
 * `boxShadow`, `clipPath` and `background` values are ordinary string literals sitting in the same
 * files as the labels. A phrase rule without these would report a hundred `repeat(auto-fit,
 * minmax(240px, 1fr))` values and be switched off the same day.
 */
const NOT_PROSE = [
  // CSS lengths, shorthand and functions.
  /^[\d\s.,%/()-]+$/,
  /\d(?:px|rem|em|vh|vw|fr|ms|s|deg|%)\b/,
  /\b(?:repeat|minmax|calc|rgba?|hsla?|var|url|translate[XY]?|rotate|scale|clamp|cubic-bezier|linear-gradient|repeating-linear-gradient|radial-gradient|rect|inset|blur)\(/,
  /\b(?:ease|ease-in|ease-out|ease-in-out|auto|inherit|initial|unset|nowrap|monospace|sans-serif|serif|transparent|currentColor)\b\s*[,;]?\s*$/,
  /^(?:[a-z-]+\s+)*(?:auto|1fr|max-content|min-content)(?:\s+[a-z0-9-]+)*$/,
  // SQL shown verbatim so an admin can reproduce a figure - the same in every language.
  /^\s*(?:SELECT|INSERT|UPDATE|DELETE|WITH|DECLARE|EXEC)\b/i,
];

/**
 * Is this string one a reader would see as text?
 *
 * Deliberately generous: something that only might be text is worth a line in the allow-list,
 * whereas something that is text and slips through is a Spanish page with an English label on it.
 */
export function looksLikeText(raw: string): boolean {
  const value = raw.trim();
  if (!isPossiblyText(value)) return false;

  // A lone identifier in camelCase, kebab-case or snake_case: a prop value, key or css token.
  // Applied only here, not in `looksLikeUserFacingText`: this predicate is used by the catch-all
  // rule, which sees every literal in the file and would otherwise be unusable.
  if (/^[a-z][a-zA-Z0-9]*$/.test(value)) return false;
  if (/^[a-z0-9]+(?:[-_][a-z0-9]+)+$/.test(value)) return false;

  return true;
}

/**
 * The same question, asked where the AST has already proved the string is shown to a reader -
 * a JSX child, a prop that is rendered, a value interpolated into a translated sentence.
 *
 * Deliberately stricter than `looksLikeText`, by dropping its identifier exclusions. Those exist
 * so the catch-all rule can look at every literal in a file without drowning in prop values and
 * CSS tokens; once the position is known to be text, they only hide real defects. `activity`,
 * `interactions` and `analysed` are ordinary English words that happen to be lower-case, and all
 * three were live in this portal - rendered beside a translated label, or interpolated into a
 * Spanish sentence.
 */
export function looksLikeUserFacingText(raw: string): boolean {
  return isPossiblyText(raw.trim());
}

/** The checks both predicates share: it has letters, and is not a URL, colour or CSS length. */
function isPossiblyText(value: string): boolean {
  if (value.length < 2) return false;
  if (!HAS_WORD.test(value)) return false;
  if (CATALOG_KEYS.has(value)) return false;
  if (ALLOWED_LITERALS.has(value)) return false;

  // Module specifiers, URLs, routes, data URIs and CSS/asset paths.
  if (/^(?:https?:|mailto:|data:|\/|\.{1,2}\/|#\/)/.test(value)) return false;
  // A CSS dimension, colour or font stack.
  if (/^[\d.]+(?:px|%|em|rem|vh|vw|fr|s|ms)$/.test(value)) return false;
  if (/^#[0-9a-fA-F]{3,8}$/.test(value)) return false;

  return true;
}

/**
 * Is this a phrase - something written for a person to read, wherever it happens to sit?
 *
 * The position-based rules below only see text in a place the checker already knows about: a JSX
 * child, a whitelisted prop, a known property name. That leaves the shape that actually got
 * through during this feature's own development - a sentence assembled inside a helper and
 * returned as a string, and a sentence passed to a prop nobody had thought to whitelist. Both were
 * real: `describeBands()` in GaugeRing built "below 40% needs attention, 40-70% is progressing"
 * and handed it to an otherwise-translated Spanish sentence, and seven `blurb=` section
 * descriptions rendered in English on the Spanish page.
 *
 * So anything three words or longer is treated as text no matter where it is written. That is a
 * blunt rule, which is why `NOT_PROSE` exists - but blunt in the safe direction: the cost of a
 * false positive is one line in the allow-list, and the cost of a false negative is an English
 * paragraph in the middle of a Spanish page.
 */
export function looksLikePhrase(raw: string): boolean {
  const value = raw.trim();
  if (!IS_PHRASE.test(value)) return false;
  if (NOT_PROSE.some((pattern) => pattern.test(value))) return false;
  return looksLikeText(value);
}

function collapse(value: string): string {
  return value.replace(/\s+/g, ' ').trim();
}

/** The text parts of a template literal, ignoring its `${}` holes. */
function templateText(node: ts.TemplateLiteral): string {
  if (ts.isNoSubstitutionTemplateLiteral(node)) return node.text;
  return [node.head.text, ...node.templateSpans.map((span) => span.literal.text)].join(' ');
}

function calleeName(node: ts.CallExpression): string {
  const expression = node.expression;
  if (ts.isIdentifier(expression)) return expression.text;
  if (ts.isPropertyAccessExpression(expression)) {
    const target = ts.isIdentifier(expression.expression) ? expression.expression.text : '';
    return target ? `${target}.${expression.name.text}` : expression.name.text;
  }
  return '';
}

/**
 * True when the node is a translation **key** argument, so it is machinery rather than text.
 *
 * Scoped to the key positions on purpose. Exempting everything beneath a `t()` call - which is the
 * obvious implementation - creates a hole big enough to drive the whole feature through: the
 * *values* passed to `t()` are interpolated straight into the translated sentence, so
 * `t('key', { word: 'analysed' })` puts an English word into the middle of a Spanish paragraph and
 * the checker never sees it. That is not hypothetical; it is what was found in this portal.
 *
 * `t`/`tNode`/`translateActive` take the key first. `plural(count, oneKey, otherKey)` takes two
 * keys, in the second and third positions. `translateStatic(language, key, values)` takes the
 * language first and the key second.
 */
function isTranslationKeyArgument(node: ts.Node): boolean {
  for (let current: ts.Node = node; current.parent; current = current.parent) {
    const parent: ts.Node = current.parent;
    if (!ts.isCallExpression(parent)) continue;

    const callee = calleeName(parent);
    const index = parent.arguments.indexOf(current as ts.Expression);
    if (index < 0) continue;

    if (callee === 'plural') return index === 1 || index === 2;
    if (callee === 'translateStatic') return index === 1;
    if (TRANSLATION_CALLEES.has(callee)) return index === 0;
  }
  return false;
}

/** True when the node sits inside a call whose text is for a developer, such as `console.warn`. */
function insideIgnoredCall(node: ts.Node): boolean {
  for (let current: ts.Node | undefined = node.parent; current; current = current.parent) {
    if (ts.isCallExpression(current) && IGNORED_CALLEES.has(calleeName(current))) return true;
  }
  return false;
}

function attributeName(node: ts.JsxAttribute): string {
  return ts.isIdentifier(node.name)
    ? node.name.text
    : `${node.name.namespace.text}:${node.name.name.text}`;
}

function propertyName(node: ts.PropertyAssignment): string | null {
  if (ts.isIdentifier(node.name) || ts.isStringLiteral(node.name)) return node.name.text;
  return null;
}

/**
 * True when a literal is inside a JSX expression container, i.e. it is being rendered.
 *
 * Walks up rather than down so a literal reached through a ternary, a `??` or an inline `.map()`
 * is still recognised - all of which the portal uses. Stops at a declaration or a function
 * boundary, so a string bound to a variable for logic is left to the property rule instead.
 */
function isRenderedExpression(node: ts.Node): boolean {
  for (let current: ts.Node | undefined = node.parent; current; current = current.parent) {
    if (ts.isJsxExpression(current)) {
      // An attribute's `{...}` is also a JsxExpression, but the attribute rule owns it - and it
      // applies only to the handful of attributes that are read as text. Claiming it here too
      // would report every one of those twice, and would also drag in every `onClick`, `style`
      // and `key` expression, which are not text at all.
      return !(current.parent && ts.isJsxAttribute(current.parent));
    }
    if (ts.isJsxAttribute(current)) return false;
    if (ts.isVariableDeclaration(current) || ts.isPropertyAssignment(current)) return false;
    if (ts.isReturnStatement(current)) return false;
    if (
      ts.isFunctionDeclaration(current) ||
      ts.isMethodDeclaration(current) ||
      ts.isClassDeclaration(current)
    ) {
      return false;
    }
  }
  return false;
}

/** Scans one file's source text. `fileName` is only used for reporting. */
export function findHardcodedStrings(sourceText: string, fileName: string): Finding[] {
  const source = ts.createSourceFile(
    fileName,
    sourceText,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX,
  );
  const findings: Finding[] = [];

  const report = (node: ts.Node, kind: FindingKind, text: string, via?: string) => {
    const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
    findings.push({ file: fileName, line: line + 1, kind, text: collapse(text), via });
  };

  const visit = (node: ts.Node): void => {
    // 1. Text written directly between JSX tags.
    if (ts.isJsxText(node)) {
      if (looksLikeUserFacingText(node.text)) report(node, 'jsx-text', node.text);
      return;
    }

    // 2. A user-facing attribute given text.
    //
    // The whole expression is walked, not just a bare literal or template. Text reaches these
    // props in more shapes than that: `axes={['Frequency', 'Depth', 'Breadth']}` renders three
    // words on a chart, `aria-label={'Hide warnings'}` is a braced literal, and a ternary picks
    // between two of them. Inspecting only the direct initializer missed all three.
    if (ts.isJsxAttribute(node)) {
      const name = attributeName(node);
      const initializer = node.initializer;
      if (initializer && USER_FACING_ATTRIBUTES.has(name)) {
        if (ts.isStringLiteral(initializer)) {
          if (looksLikeUserFacingText(initializer.text)) {
            report(initializer, 'jsx-attribute', initializer.text, name);
          }
        } else if (ts.isJsxExpression(initializer) && initializer.expression) {
          const walk = (expression: ts.Node): void => {
            if (isTranslationKeyArgument(expression)) return;
            // Nested JSX has its own rules - descending into it would report an enum value such
            // as `appearance="filled"` on a Badge inside a `header={...}` slot.
            if (
              ts.isJsxElement(expression) ||
              ts.isJsxSelfClosingElement(expression) ||
              ts.isJsxFragment(expression)
            ) {
              return;
            }
            if (ts.isStringLiteral(expression) && looksLikeUserFacingText(expression.text)) {
              report(expression, 'jsx-attribute', expression.text, name);
              return;
            }
            if (ts.isTemplateLiteral(expression)) {
              const text = templateText(expression);
              if (looksLikeUserFacingText(text)) report(expression, 'jsx-attribute', text, name);
              // Still descend: a template's holes can contain literals of their own.
            }
            ts.forEachChild(expression, walk);
          };
          walk(initializer.expression);
        }
      }
      // Fall through, so an expression attribute's own subtree is still walked by later rules.
    }

    // 3. A literal rendered through an expression container: {'Yes'} or {`${n} users`}.
    if (
      (ts.isStringLiteral(node) || ts.isTemplateLiteral(node)) &&
      !isTranslationKeyArgument(node) &&
      isRenderedExpression(node)
    ) {
      const text = ts.isStringLiteral(node) ? node.text : templateText(node);
      if (looksLikeText(text)) report(node, 'jsx-expression', text);
    }

    // 4. A literal assigned to a property that gets rendered.
    if (ts.isPropertyAssignment(node)) {
      const name = propertyName(node);
      const value = node.initializer;
      if (name && USER_FACING_PROPERTIES.has(name) && !isTranslationKeyArgument(value)) {
        if (ts.isStringLiteral(value) && looksLikeUserFacingText(value.text)) {
          report(value, 'object-property', value.text, name);
        } else if (ts.isTemplateLiteral(value) && looksLikeUserFacingText(templateText(value))) {
          report(value, 'object-property', templateText(value), name);
        }
      }
    }

    // 5. A toast, which is text with no JSX anywhere near it.
    //
    // Only the first argument: the second is a Fluent `ToastIntent` enum ('success', 'error'),
    // which is machinery and not text.
    if (ts.isCallExpression(node) && NOTIFICATION_CALLEES.has(calleeName(node))) {
      const argument = node.arguments[0];
      if (argument && !isTranslationKeyArgument(argument)) {
        if (ts.isStringLiteral(argument) && looksLikeUserFacingText(argument.text)) {
          report(argument, 'notification', argument.text, calleeName(node));
        } else if (ts.isTemplateLiteral(argument) && looksLikeUserFacingText(templateText(argument))) {
          report(argument, 'notification', templateText(argument), calleeName(node));
        }
      }
    }

    // 6. An English literal passed as a *value* to a translation call.
    //
    // The values are interpolated straight into the translated sentence, so a literal here puts
    // English in the middle of a Spanish paragraph. It needs its own rule rather than relying on
    // the phrase rule below, because these are usually one or two words - `{ word: 'analysed' }` -
    // and a word count cannot tell them from an identifier.
    if (ts.isCallExpression(node)) {
      const callee = calleeName(node);
      if (TRANSLATION_CALLEES.has(callee)) {
        const values = node.arguments[node.arguments.length - 1];
        if (values && ts.isObjectLiteralExpression(values)) {
          for (const property of values.properties) {
            if (!ts.isPropertyAssignment(property)) continue;
            const value = property.initializer;
            if (ts.isStringLiteral(value) && looksLikeText(value.text)) {
              report(value, 'jsx-expression', value.text, `${callee}() value`);
            } else if (ts.isTemplateLiteral(value) && looksLikeText(templateText(value))) {
              report(value, 'jsx-expression', templateText(value), `${callee}() value`);
            }
          }
        }
      }
    }

    // 7. A phrase anywhere at all - the catch-all for text in a place no rule above knows about.
    if (
      (ts.isStringLiteral(node) ||
        ts.isNoSubstitutionTemplateLiteral(node) ||
        ts.isTemplateExpression(node)) &&
      !isTranslationKeyArgument(node) &&
      !insideIgnoredCall(node)
    ) {
      const text = ts.isTemplateExpression(node) ? templateText(node) : node.text;
      if (looksLikePhrase(text)) report(node, 'phrase', text);
    }

    ts.forEachChild(node, visit);
  };

  ts.forEachChild(source, visit);

  // A literal can satisfy two rules at once - a whitelisted attribute that is also a phrase, a
  // template both rendered and assigned. Keep the first, which is the most specific: the rules run
  // in order, and the phrase rule is deliberately last because it is the catch-all.
  const seen = new Set<string>();
  const unique = findings.filter((finding) => {
    const id = `${finding.line}|${finding.text}`;
    if (seen.has(id)) return false;
    seen.add(id);
    return true;
  });

  unique.sort((a, b) => a.line - b.line || a.text.localeCompare(b.text));
  return unique;
}

/** Should this file be scanned at all? */
export function shouldScan(relativePath: string): boolean {
  const normalised = relativePath.replace(/\\/g, '/');
  if (!/\.tsx?$/.test(normalised)) return false;
  if (/\.d\.ts$/.test(normalised)) return false;
  return !IGNORED_FILE_PATTERNS.some((pattern) => pattern.test(normalised));
}
