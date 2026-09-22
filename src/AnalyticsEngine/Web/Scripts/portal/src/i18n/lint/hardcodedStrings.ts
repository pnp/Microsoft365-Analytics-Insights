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
  | 'notification';

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
  'buttonLabel',
  'caption',
  'content',
  'description',
  'header',
  'heading',
  'hint',
  'label',
  'placeholder',
  'subtitle',
  'title',
  'tooltip',
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
const TRANSLATION_CALLEES = new Set(['plural', 't', 'tNode', 'translateStatic']);

const CATALOG_KEYS = new Set(Object.keys(EN_CATALOG));

/** Two consecutive letters somewhere - the cheapest test for "a human would read this". */
const HAS_WORD = /\p{L}\p{L}/u;

/**
 * Is this string one a reader would see as text?
 *
 * Deliberately generous: something that only might be text is worth a line in the allow-list,
 * whereas something that is text and slips through is a Spanish page with an English label on it.
 */
export function looksLikeText(raw: string): boolean {
  const value = raw.trim();
  if (value.length < 2) return false;
  if (!HAS_WORD.test(value)) return false;
  if (CATALOG_KEYS.has(value)) return false;
  if (ALLOWED_LITERALS.has(value)) return false;

  // Module specifiers, URLs, routes, data URIs and CSS/asset paths.
  if (/^(?:https?:|mailto:|data:|\/|\.{1,2}\/|#\/)/.test(value)) return false;
  // A lone identifier in camelCase, kebab-case or snake_case: a prop value, key or css token.
  if (/^[a-z][a-zA-Z0-9]*$/.test(value)) return false;
  if (/^[a-z0-9]+(?:[-_][a-z0-9]+)+$/.test(value)) return false;
  // A CSS dimension, colour or font stack.
  if (/^[\d.]+(?:px|%|em|rem|vh|vw|fr|s|ms)$/.test(value)) return false;
  if (/^#[0-9a-fA-F]{3,8}$/.test(value)) return false;

  return true;
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

/** True when the node sits inside a `t(...)`/`plural(...)` call, so its strings are keys. */
function insideTranslationCall(node: ts.Node): boolean {
  for (let current: ts.Node | undefined = node.parent; current; current = current.parent) {
    if (ts.isCallExpression(current) && TRANSLATION_CALLEES.has(calleeName(current))) return true;
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
      if (looksLikeText(node.text)) report(node, 'jsx-text', node.text);
      return;
    }

    // 2. A user-facing attribute given a literal.
    if (ts.isJsxAttribute(node)) {
      const name = attributeName(node);
      const initializer = node.initializer;
      if (initializer && USER_FACING_ATTRIBUTES.has(name)) {
        if (ts.isStringLiteral(initializer) && looksLikeText(initializer.text)) {
          report(initializer, 'jsx-attribute', initializer.text, name);
        } else if (
          ts.isJsxExpression(initializer) &&
          initializer.expression &&
          ts.isTemplateLiteral(initializer.expression) &&
          !insideTranslationCall(initializer.expression) &&
          looksLikeText(templateText(initializer.expression))
        ) {
          report(
            initializer.expression,
            'jsx-attribute',
            templateText(initializer.expression),
            name,
          );
        }
      }
      // Fall through, so an expression attribute's own subtree is still walked.
    }

    // 3. A literal rendered through an expression container: {'Yes'} or {`${n} users`}.
    if (
      (ts.isStringLiteral(node) || ts.isTemplateLiteral(node)) &&
      !insideTranslationCall(node) &&
      isRenderedExpression(node)
    ) {
      const text = ts.isStringLiteral(node) ? node.text : templateText(node);
      if (looksLikeText(text)) report(node, 'jsx-expression', text);
    }

    // 4. A literal assigned to a property that gets rendered.
    if (ts.isPropertyAssignment(node)) {
      const name = propertyName(node);
      const value = node.initializer;
      if (name && USER_FACING_PROPERTIES.has(name) && !insideTranslationCall(value)) {
        if (ts.isStringLiteral(value) && looksLikeText(value.text)) {
          report(value, 'object-property', value.text, name);
        } else if (ts.isTemplateLiteral(value) && looksLikeText(templateText(value))) {
          report(value, 'object-property', templateText(value), name);
        }
      }
    }

    // 5. A toast, which is text with no JSX anywhere near it.
    if (ts.isCallExpression(node) && NOTIFICATION_CALLEES.has(calleeName(node))) {
      for (const argument of node.arguments) {
        if (insideTranslationCall(argument)) continue;
        if (ts.isStringLiteral(argument) && looksLikeText(argument.text)) {
          report(argument, 'notification', argument.text, calleeName(node));
        } else if (ts.isTemplateLiteral(argument) && looksLikeText(templateText(argument))) {
          report(argument, 'notification', templateText(argument), calleeName(node));
        }
      }
    }

    ts.forEachChild(node, visit);
  };

  ts.forEachChild(source, visit);

  // A literal can satisfy two rules at once - an attribute that is also an expression container,
  // a template both rendered and assigned. One line in the report per place in the file.
  const seen = new Set<string>();
  const unique = findings.filter((finding) => {
    const id = `${finding.line}|${finding.kind}|${finding.text}`;
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
