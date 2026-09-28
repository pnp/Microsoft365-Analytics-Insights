import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { EMPTY_USER_FILTER, type UserFilter, type UserFilterClause } from '../../types/userFilter';
import {
  FIXED_VALUE_TOKENS,
  MAX_CLAUSES,
  MAX_TEXT_TERMS,
  MAX_TEXT_TERMS_PER_CLAUSE,
  MAX_VALUES_PER_CLAUSE,
  MAX_VALUE_LENGTH,
  addClause,
  encodedFilterLength,
  fitsLimits,
  groupClauseIndexes,
  hasTooManyTextTerms,
  hasTooManyValues,
  isEmptyFilter,
  parseUserFilter,
  removeClause,
  replaceClause,
  sameUserFilter,
  serializeUserFilter,
  setJoin,
  singleValueFor,
  withDimensionValue,
} from './userFilterModel';

function clause(dimension: string, values: string[], overrides: Partial<UserFilterClause> = {}): UserFilterClause {
  return { join: 'and', dimension, operator: 'is', values, includeNotSet: false, ...overrides };
}

const SALES = clause('department', ['Sales']);
const UK = clause('country', ['United Kingdom']);
const CONTOSO = clause('companyName', ['Contoso'], { join: 'or' });

describe('user filter wire format', () => {
  it('writes the short form the server reads, leaving the defaults out', () => {
    expect(serializeUserFilter({ clauses: [SALES] })).toBe('[{"d":"department","v":["Sales"]}]');
    expect(
      serializeUserFilter({
        clauses: [SALES, clause('org:12', ['CC-1'], { join: 'or', operator: 'isNot', includeNotSet: true })],
      }),
    ).toBe('[{"d":"department","v":["Sales"]},{"j":"or","d":"org:12","op":"isNot","v":["CC-1"],"n":true}]');
  });

  it('writes nothing for no filter, and leaves incomplete conditions out', () => {
    expect(serializeUserFilter(EMPTY_USER_FILTER)).toBeNull();
    expect(serializeUserFilter({ clauses: [clause('department', [])] })).toBeNull();
    expect(serializeUserFilter({ clauses: [clause('department', [], { includeNotSet: true })] })).toBe(
      '[{"d":"department","v":[],"n":true}]',
    );
  });

  it('never writes a join on the first condition, which has nothing to join to', () => {
    expect(serializeUserFilter({ clauses: [{ ...SALES, join: 'or' }] })).toBe('[{"d":"department","v":["Sales"]}]');
  });

  it('reads back what it writes, non-Latin values included', () => {
    const filter: UserFilter = {
      clauses: [SALES, clause('org:3', ['Καλημέρα κόσμε']), { ...CONTOSO, operator: 'contains', values: ['cont'] }],
    };

    expect(parseUserFilter(serializeUserFilter(filter))).toEqual(filter);
  });

  it('opens a malformed link on the unfiltered report rather than failing', () => {
    expect(parseUserFilter('not json')).toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter('{"d":"department"}')).toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter('[{"v":["Sales"]}]')).toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter('[{"d":"department","op":"startsWith","v":["S"]}]')).toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter(null)).toEqual(EMPTY_USER_FILTER);
  });

  it('refuses a link naming an attribute the server would reject, instead of sending it', () => {
    expect(parseUserFilter('[{"d":"favouriteColour","v":["Blue"]}]')).toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter('[{"d":"org:012","v":["x"]}]')).toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter('[{"d":"org:12","v":["x"]}]').clauses).toHaveLength(1);
  });

  it('measures the filter the way the query string will carry it', () => {
    const greek = { clauses: [clause('department', ['Καλημέρα'])] };
    // Every Greek letter is two UTF-8 bytes, six characters once percent-encoded.
    expect(encodedFilterLength(greek)).toBeGreaterThan(serializeUserFilter(greek)!.length * 2);
  });
});

describe('user filter structure', () => {
  it('groups conditions the way they are evaluated: AND binds tighter than OR', () => {
    expect(groupClauseIndexes({ clauses: [SALES, UK, CONTOSO, clause('jobTitle', ['Engineer'])] })).toEqual([
      [0, 1],
      [2, 3],
    ]);
  });

  it('keeps the first condition an AND whatever is removed or changed around it', () => {
    const filter = { clauses: [SALES, CONTOSO] };

    expect(removeClause(filter, 0).clauses[0].join).toBe('and');
    expect(setJoin(filter, 0, 'or').clauses[0].join).toBe('and');
    expect(addClause(EMPTY_USER_FILTER, { ...SALES, join: 'or' }).clauses[0].join).toBe('and');
  });

  it('keeps an OR group an OR group when the condition that opens it is removed', () => {
    // (Sales) or (UK and London): removing "UK" must leave (Sales) or (London) - not Sales and London,
    // which is what dropping the clause carrying the "or" would otherwise produce.
    const london = clause('officeLocation', ['London']);
    const filter = { clauses: [SALES, { ...UK, join: 'or' as const }, london] };

    const next = removeClause(filter, 1);

    expect(next.clauses).toEqual([SALES, { ...london, join: 'or' }]);
    expect(groupClauseIndexes(next)).toEqual([[0], [1]]);
  });

  it('drops a group that loses its only condition, and keeps the rest of the structure', () => {
    const next = removeClause({ clauses: [SALES, CONTOSO, clause('jobTitle', ['Engineer'], { join: 'or' })] }, 1);

    expect(next.clauses).toEqual([SALES, clause('jobTitle', ['Engineer'], { join: 'or' })]);
  });

  it('refuses a link longer than the server accepts rather than cutting conditions off it', () => {
    const many = Array.from({ length: 26 }, (_, i) => ({ d: 'department', v: [`D${i}`] }));
    expect(parseUserFilter(JSON.stringify(many))).toEqual(EMPTY_USER_FILTER);
  });

  it('opens a link past any other server limit unfiltered, rather than as an error on every panel', () => {
    const codes = (n: number) => Array.from({ length: n }, (_, i) => `${i}`);

    expect(parseUserFilter(JSON.stringify([{ d: 'department', v: codes(MAX_VALUES_PER_CLAUSE) }])).clauses).toHaveLength(1);
    expect(parseUserFilter(JSON.stringify([{ d: 'department', v: codes(MAX_VALUES_PER_CLAUSE + 1) }]))).toEqual(
      EMPTY_USER_FILTER,
    );
    expect(parseUserFilter(JSON.stringify([{ d: 'userName', op: 'contains', v: codes(MAX_TEXT_TERMS_PER_CLAUSE + 1) }]))).toEqual(
      EMPTY_USER_FILTER,
    );
    expect(parseUserFilter(JSON.stringify([{ d: 'department', v: ['x'.repeat(7000)] }]))).toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter(JSON.stringify([{ d: 'org:3', v: ['x'.repeat(MAX_VALUE_LENGTH)] }])).clauses).toHaveLength(1);
    expect(
      parseUserFilter(JSON.stringify([{ d: 'org:3', v: ['x'.repeat(MAX_VALUE_LENGTH + 1)] }])),
      'Wider than an organisation name, though far shorter than the link limit.',
    ).toEqual(EMPTY_USER_FILTER);
  });

  it('refuses a link carrying an incomplete condition, whose removal would move a group boundary', () => {
    expect(parseUserFilter('[{"d":"department","v":["Sales"]},{"j":"or","d":"country","v":[]},{"d":"jobTitle","v":["X"]}]')).toEqual(
      EMPTY_USER_FILTER,
    );
  });

  it('opens a link the server would refuse unfiltered, rather than quietly sending a different filter', () => {
    // Dropping the part the server objects to would change what the link means - a value gone from a
    // condition matches different people - so the whole link is treated as not made here.
    expect(parseUserFilter('[{"d":"department","v":[1,"Sales"]}]'), 'A value that is not text.').toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter('[{"d":"department","v":"Sales","n":true}]'), 'Values that are not a list.').toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter('[{"d":"accountStatus","op":"contains","v":["en"]}]'), 'Text search on a fixed value.').toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter('[{"d":"managementChain","op":"notContains","v":["a"]}]'), 'Text search on the hierarchy.').toEqual(EMPTY_USER_FILTER);
    expect(parseUserFilter('[{"d":"userType","v":["contractor"]}]'), 'A token the server does not define.').toEqual(EMPTY_USER_FILTER);

    expect(parseUserFilter('[{"d":"userType","v":["GUEST"]}]').clauses, 'Tokens compare case-insensitively, as on the server.').toHaveLength(1);
    expect(parseUserFilter('[{"d":"accountStatus","op":"isNot","v":["disabled"]}]').clauses).toHaveLength(1);
    expect(parseUserFilter('[{"d":"org:3","op":"contains","v":["retail"]}]').clauses).toHaveLength(1);
    expect(parseUserFilter('[{"d":"department","n":true}]').clauses, 'No values at all is still "not set".').toHaveLength(1);
    expect(parseUserFilter('[{"d":"department","v":null,"n":true}]').clauses, 'The server reads a null list as none.').toHaveLength(1);
  });

  it('replaces one condition without disturbing the others', () => {
    const next = replaceClause({ clauses: [SALES, UK] }, 1, clause('country', ['Ireland']));

    expect(next.clauses.map((c) => c.values[0])).toEqual(['Sales', 'Ireland']);
  });

  it('treats a filter with no complete conditions as no filter', () => {
    expect(isEmptyFilter(EMPTY_USER_FILTER)).toBe(true);
    expect(isEmptyFilter({ clauses: [clause('department', [])] })).toBe(true);
    expect(isEmptyFilter({ clauses: [SALES] })).toBe(false);
  });

  it('compares filters by what they mean, not by object identity', () => {
    expect(sameUserFilter({ clauses: [SALES] }, { clauses: [{ ...SALES }] })).toBe(true);
    expect(sameUserFilter({ clauses: [SALES] }, { clauses: [UK] })).toBe(false);
  });
});

describe('narrowing the whole filter to one value', () => {
  it('adds the condition to an empty filter', () => {
    expect(withDimensionValue(EMPTY_USER_FILTER, 'emailDomain', 'fabrikam.com')).toEqual({
      clauses: [clause('emailDomain', ['fabrikam.com'])],
    });
  });

  it('replaces an existing condition on the same attribute', () => {
    const next = withDimensionValue(
      { clauses: [SALES, clause('emailDomain', ['contoso.com'])] },
      'emailDomain',
      'fabrikam.com',
    );

    expect(next.clauses).toEqual([SALES, clause('emailDomain', ['fabrikam.com'])]);
  });

  it('ANDs the condition into every OR group, so the same people are kept, only on that domain', () => {
    // (Sales and UK) or Contoso, narrowed to fabrikam.com, must become
    // (Sales and UK and fabrikam.com) or (Contoso and fabrikam.com). Appending one clause at the end
    // would have narrowed only the last group.
    const next = withDimensionValue({ clauses: [SALES, UK, CONTOSO] }, 'emailDomain', 'fabrikam.com');
    const fabrikam = clause('emailDomain', ['fabrikam.com']);

    expect(next.clauses).toEqual([SALES, UK, fabrikam, CONTOSO, fabrikam]);
    expect(groupClauseIndexes(next)).toEqual([
      [0, 1, 2],
      [3, 4],
    ]);
  });

  it('removes the attribute altogether when given no value', () => {
    expect(withDimensionValue({ clauses: [clause('emailDomain', ['contoso.com']), SALES] }, 'emailDomain', null)).toEqual({
      clauses: [SALES],
    });
  });

  it('keeps every OR group when a domain condition opening one of them is replaced or cleared', () => {
    // (Sales) or (contoso.com and UK). Choosing fabrikam.com must give
    // (Sales and fabrikam.com) or (fabrikam.com and UK); clearing it must give (Sales) or (UK).
    const contoso = clause('emailDomain', ['contoso.com'], { join: 'or' });
    const filter = { clauses: [SALES, contoso, UK] };

    const narrowed = withDimensionValue(filter, 'emailDomain', 'fabrikam.com');
    expect(narrowed.clauses).toEqual([
      SALES,
      clause('emailDomain', ['fabrikam.com']),
      clause('emailDomain', ['fabrikam.com'], { join: 'or' }),
      UK,
    ]);
    expect(groupClauseIndexes(narrowed)).toEqual([
      [0, 1],
      [2, 3],
    ]);

    const cleared = withDimensionValue(filter, 'emailDomain', null);
    expect(cleared.clauses).toEqual([SALES, { ...UK, join: 'or' }]);
  });

  it('refuses nothing itself, but says when the result is too big to send', () => {
    const groups = Array.from({ length: 13 }, (_, i) => clause('department', [`D${i}`], { join: i === 0 ? 'and' : 'or' }));
    const narrowed = withDimensionValue({ clauses: groups }, 'emailDomain', 'fabrikam.com');

    expect(narrowed.clauses).toHaveLength(26);
    expect(fitsLimits(narrowed)).toBe(false);
    expect(fitsLimits({ clauses: groups })).toBe(true);
  });

  it('keeps text searches within what the server will run, per condition and in all', () => {
    // Each piece of text is searched for in every distinct value - one per person for the user name -
    // so these limits are far below the 500 values a condition may pick from a list.
    const terms = (n: number, prefix: string) => Array.from({ length: n }, (_, i) => `${prefix}${i}`);
    const contains = (values: string[], join: 'and' | 'or' = 'and') =>
      clause('userName', values, { operator: 'contains', join });

    expect(hasTooManyTextTerms({ clauses: [contains(terms(MAX_TEXT_TERMS_PER_CLAUSE, 'a'))] })).toBe(false);
    expect(hasTooManyTextTerms({ clauses: [contains(terms(MAX_TEXT_TERMS_PER_CLAUSE + 1, 'a'))] })).toBe(true);
    expect(
      hasTooManyTextTerms({ clauses: [contains(terms(6, 'a')), contains(terms(MAX_TEXT_TERMS - 5, 'b'), 'or')] }),
    ).toBe(true);
    expect(fitsLimits({ clauses: [contains(terms(MAX_TEXT_TERMS + 1, 'a'))] })).toBe(false);
    expect(
      hasTooManyTextTerms({ clauses: [clause('department', terms(400, 'D'))] }),
      'Picked values are exact lookups, not searches.',
    ).toBe(false);
  });

  it('keeps a condition within the values the server accepts, however short they are', () => {
    // 501 short codes fit comfortably under the URL limit, so length alone would let them through to
    // a server that refuses every report request carrying them.
    const codes = (n: number) => Array.from({ length: n }, (_, i) => `${i}`);
    const within = { clauses: [clause('department', codes(MAX_VALUES_PER_CLAUSE))] };
    const over = { clauses: [clause('department', codes(MAX_VALUES_PER_CLAUSE + 1))] };

    expect(hasTooManyValues(within)).toBe(false);
    expect(hasTooManyValues(over)).toBe(true);
    expect(encodedFilterLength(over), 'Short enough that length is not what refuses it.').toBeLessThan(6000);
    expect(fitsLimits(over)).toBe(false);
  });

  it('mirrors the server\u2019s limits exactly', () => {
    // A portal limit above the server's lets through a filter every report request then refuses; one
    // below it refuses a filter the server would run.
    const codec = readFileSync(
      join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'UserFilters', 'UserFilterCodec.cs'),
      'utf8',
    );
    const server = (name: string) => Number(new RegExp(`public const int ${name} = (\\d+);`).exec(codec)?.[1]);

    expect({ MAX_CLAUSES, MAX_VALUES_PER_CLAUSE, MAX_VALUE_LENGTH, MAX_TEXT_TERMS_PER_CLAUSE, MAX_TEXT_TERMS }).toEqual({
      MAX_CLAUSES: server('MaxClauses'),
      MAX_VALUES_PER_CLAUSE: server('MaxValuesPerClause'),
      MAX_VALUE_LENGTH: server('MaxValueLength'),
      MAX_TEXT_TERMS_PER_CLAUSE: server('MaxTextTermsPerClause'),
      MAX_TEXT_TERMS: server('MaxTextTerms'),
    });
  });

  it('knows the fixed values exactly as the server defines them', () => {
    const dimensions = readFileSync(
      join(process.cwd(), '..', '..', '..', 'Common', 'Entities', 'UserFilters', 'UserFilterDimensions.cs'),
      'utf8',
    );
    const constant = (name: string) => new RegExp(`public const string ${name} = "([^"]+)";`).exec(dimensions)?.[1];
    const list = (name: string) =>
      (new RegExp(`${name} = new\\[\\] \\{ ([^}]+) \\}`).exec(dimensions)?.[1] ?? '').split(',').map((n) => constant(n.trim()));

    expect(FIXED_VALUE_TOKENS).toEqual({
      [constant('UserType') ?? 'missing']: list('UserTypes'),
      [constant('AccountStatus') ?? 'missing']: list('AccountStatuses'),
    });
  });

  it('reports the single value only when every group requires exactly it', () => {
    const fabrikam = clause('emailDomain', ['fabrikam.com']);

    expect(singleValueFor({ clauses: [fabrikam] }, 'emailDomain')).toBe('fabrikam.com');
    expect(singleValueFor({ clauses: [SALES, fabrikam, CONTOSO, fabrikam] }, 'emailDomain')).toBe('fabrikam.com');
    expect(singleValueFor({ clauses: [SALES, fabrikam, CONTOSO] }, 'emailDomain')).toBeNull();
    expect(singleValueFor({ clauses: [clause('emailDomain', ['a.com', 'b.com'])] }, 'emailDomain')).toBeNull();
    expect(singleValueFor({ clauses: [{ ...fabrikam, operator: 'isNot' }] }, 'emailDomain')).toBeNull();
    expect(singleValueFor(EMPTY_USER_FILTER, 'emailDomain')).toBeNull();
  });
});
