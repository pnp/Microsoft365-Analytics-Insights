// @vitest-environment node
import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import { EN_CATALOG } from '../catalog';
import { ENTRA_DIMENSION_LABEL_KEYS, USER_FILTER_TOKEN_LABEL_KEYS } from '../../components/userFilter/describeUserFilter';
import { ENTRA_DIMENSION_KEYS } from '../../components/userFilter/userFilterModel';

/**
 * The user filter's server-defined vocabulary, checked against the portal's.
 *
 * The API reports facts - a dimension key such as `jobTitle`, a token such as `guest` - and the portal
 * writes the words. That split is only safe while both sides agree on the keys: a dimension added to
 * `UserFilterDimensions` with no label here would appear in the property picker as the raw key, in
 * English, on a Spanish page, and nothing else would notice. So this reads the C# and fails on drift.
 *
 * It also holds the English wording to the server's: the Excel workbook describes a filter with
 * `UserFilterDimensions.EnglishName`, and the page it was exported from must not call the same
 * attribute something else.
 */
const DIMENSIONS_SOURCE = join(
  process.cwd(),
  '..',
  '..',
  '..',
  'Common',
  'Entities',
  'UserFilters',
  'UserFilterDimensions.cs',
);

function classBody(source: string, name: string): string {
  const start = source.indexOf(`class ${name}`);
  expect(start, `Could not find class ${name}`).toBeGreaterThanOrEqual(0);
  const brace = source.indexOf('{', start);

  let depth = 0;
  for (let i = brace; i < source.length; i++) {
    if (source[i] === '{') depth++;
    if (source[i] === '}') depth--;
    if (depth === 0) return source.slice(brace + 1, i);
  }

  throw new Error(`Could not find the end of class ${name}`);
}

/** Constant name to value, for every `public const string` in a class. */
function constants(body: string): Map<string, string> {
  return new Map([...body.matchAll(/public const string (\w+) = "([^"]*)";/g)].map((m) => [m[1], m[2]]));
}

function sorted(values: Iterable<string>): string[] {
  return [...values].sort();
}

describe('user filter vocabulary shared with the server', () => {
  const source = readFileSync(DIMENSIONS_SOURCE, 'utf8');
  const dimensions = classBody(source, 'UserFilterDimensions');
  const tokens = classBody(source, 'UserFilterTokens');
  const dimensionConstants = constants(dimensions);

  const entraList = /EntraKeys = new\[\]\s*\{([^}]*)\}/.exec(dimensions);
  const serverEntraKeys = (entraList?.[1] ?? '')
    .split(',')
    .map((name) => name.trim())
    .filter((name) => name.length > 0)
    .map((name) => dimensionConstants.get(name) ?? `<unknown constant ${name}>`);

  it('reads the server source', () => {
    expect(serverEntraKeys.length).toBeGreaterThan(0);
  });

  it('labels every standard Entra ID attribute the server offers, and no other', () => {
    expect(sorted(Object.keys(ENTRA_DIMENSION_LABEL_KEYS))).toEqual(sorted(serverEntraKeys));
  });

  it('offers the attributes in the order the server lists them', () => {
    expect([...ENTRA_DIMENSION_KEYS]).toEqual(serverEntraKeys);
  });

  it('knows the custom organisation prefix the server uses', () => {
    expect(dimensionConstants.get('CustomPrefix')).toBe('org:');
  });

  it('translates every fixed value the server sends', () => {
    const serverTokens = [...constants(tokens).values()];
    expect(sorted(Object.keys(USER_FILTER_TOKEN_LABEL_KEYS))).toEqual(sorted(serverTokens));
  });

  it('calls each attribute by the same English name as the Excel workbook', () => {
    const englishNames = new Map(
      [...dimensions.matchAll(/case (\w+): return "([^"]+)";/g)].map((m) => [dimensionConstants.get(m[1]), m[2]]),
    );

    const mismatched = serverEntraKeys
      .filter((key) => EN_CATALOG[ENTRA_DIMENSION_LABEL_KEYS[key as keyof typeof ENTRA_DIMENSION_LABEL_KEYS]] !== englishNames.get(key))
      .map((key) => `${key}: portal "${EN_CATALOG[ENTRA_DIMENSION_LABEL_KEYS[key as keyof typeof ENTRA_DIMENSION_LABEL_KEYS]]}", workbook "${englishNames.get(key)}"`);

    expect(mismatched).toEqual([]);
  });

  it('uses the same English words for the fixed values as the workbook', () => {
    const englishTokens = new Map(
      [...tokens.matchAll(/case (\w+): return "([^"]+)";/g)].map((m) => [constants(tokens).get(m[1]), m[2]]),
    );

    for (const [token, key] of Object.entries(USER_FILTER_TOKEN_LABEL_KEYS)) {
      expect(EN_CATALOG[key], token).toBe(englishTokens.get(token));
    }
  });
});
