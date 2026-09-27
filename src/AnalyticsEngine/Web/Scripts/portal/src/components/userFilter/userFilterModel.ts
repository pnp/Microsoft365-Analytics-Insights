import {
  EMPTY_USER_FILTER,
  type UserFilter,
  type UserFilterClause,
  type UserFilterJoin,
  type UserFilterOperator,
} from '../../types/userFilter';

/**
 * The user filter's rules, as pure functions: what a clause means, how clauses group, and the wire
 * format the server reads (`Common.Entities.UserFilters.UserFilterCodec`).
 *
 * Kept free of React so the rules are tested directly, and so any report that adopts the filter
 * shares one definition of what it means.
 */

/** The standard Entra ID attributes, in the order the property picker offers them. */
export const ENTRA_DIMENSION_KEYS = [
  'department',
  'jobTitle',
  'companyName',
  'officeLocation',
  'country',
  'stateOrProvince',
  'usageLocation',
  'emailDomain',
  'userType',
  'accountStatus',
  'manager',
  'managementChain',
] as const;

export type EntraDimensionKey = (typeof ENTRA_DIMENSION_KEYS)[number];

export const EMAIL_DOMAIN_DIMENSION: EntraDimensionKey = 'emailDomain';
export const MANAGEMENT_CHAIN_DIMENSION: EntraDimensionKey = 'managementChain';

/** A custom organisation type's key is this prefix and the org type id. */
export const CUSTOM_DIMENSION_PREFIX = 'org:';

/**
 * The longest filter the page will send, measured URL-encoded. It rides on GET query strings - the
 * exports are plain links - and the web app allows 16 KB for those, so this leaves room for every
 * other parameter while keeping the portal, not the platform, as the thing that says "too long".
 * The server's own limit (8,000 decoded characters) is never reached by a filter under this one.
 */
export const MAX_ENCODED_FILTER_LENGTH = 6000;

/** Mirrors `UserFilterCodec.MaxClauses`. */
export const MAX_CLAUSES = 25;

export function isEntraDimension(key: string): key is EntraDimensionKey {
  return (ENTRA_DIMENSION_KEYS as readonly string[]).includes(key);
}

export function isCustomDimension(key: string): boolean {
  return key.startsWith(CUSTOM_DIMENSION_PREFIX);
}

/**
 * A key the server will accept: a standard Entra attribute, or `org:` and a positive id with no
 * leading zero - the same rule as `UserFilterDimensions.TryParseOrgTypeId`. Checked when reading a
 * filter out of a link, so a hand-edited key opens the unfiltered report rather than a page the
 * server refuses to answer.
 */
export function isKnownDimensionKey(key: string): boolean {
  return isEntraDimension(key) || /^org:[1-9]\d{0,9}$/.test(key);
}

export function isTextOperator(operator: UserFilterOperator): boolean {
  return operator === 'contains' || operator === 'notContains';
}

export function isNegatedOperator(operator: UserFilterOperator): boolean {
  return operator === 'isNot' || operator === 'notContains';
}

/** A clause the server would accept: at least one value, or "not set". */
export function clauseIsComplete(clause: UserFilterClause): boolean {
  return clause.values.length > 0 || clause.includeNotSet;
}

export function isEmptyFilter(filter: UserFilter | null | undefined): boolean {
  return !filter || filter.clauses.filter(clauseIsComplete).length === 0;
}

/** A new, empty clause for a dimension - "is", no values yet. */
export function newClause(dimension: string, join: UserFilterJoin = 'and'): UserFilterClause {
  return { join, dimension, operator: 'is', values: [], includeNotSet: false };
}

/**
 * The filter with its first clause's join normalised to AND. There is nothing before the first clause
 * to join to - the server ignores it - but a stray OR left there by removing the clause before it
 * would otherwise be echoed back and shown in the readback as a connector to nothing.
 */
function normalise(clauses: UserFilterClause[]): UserFilter {
  return {
    clauses: clauses.map((c, i) => (i === 0 && c.join !== 'and' ? { ...c, join: 'and' } : c)),
  };
}

export function addClause(filter: UserFilter, clause: UserFilterClause): UserFilter {
  return normalise([...filter.clauses, clause]);
}

export function replaceClause(filter: UserFilter, index: number, clause: UserFilterClause): UserFilter {
  return normalise(filter.clauses.map((c, i) => (i === index ? clause : c)));
}

export function removeClause(filter: UserFilter, index: number): UserFilter {
  return normalise(filter.clauses.filter((_, i) => i !== index));
}

export function setJoin(filter: UserFilter, index: number, join: UserFilterJoin): UserFilter {
  return normalise(filter.clauses.map((c, i) => (i === index ? { ...c, join } : c)));
}

/**
 * The clauses as they are evaluated: each inner array holds the indexes of one AND-group, and a person
 * matches when they match every clause of at least one group. AND binds tighter than OR.
 */
export function groupClauseIndexes(filter: UserFilter): number[][] {
  const groups: number[][] = [];
  filter.clauses.forEach((clause, index) => {
    if (groups.length === 0 || clause.join === 'or') groups.push([]);
    groups[groups.length - 1].push(index);
  });
  return groups;
}

/**
 * Narrows the whole filter to one value of a dimension - what "show only this domain" on a breakdown
 * table means once other conditions are in play.
 *
 * Any existing condition on the dimension is replaced, and the new one is ANDed into every OR-group:
 * `(A) or (B)` narrowed to domain X becomes `(A and X) or (B and X)`, which is "the same people, only on
 * X". Appending one clause to the end would only have narrowed the last group, because AND binds
 * tighter than OR. Passing `null` removes the dimension's conditions altogether.
 */
export function withDimensionValue(filter: UserFilter, dimension: string, value: string | null): UserFilter {
  const remainder = normalise(filter.clauses.filter((c) => c.dimension !== dimension));
  if (value === null) return remainder;

  const condition: UserFilterClause = { join: 'and', dimension, operator: 'is', values: [value], includeNotSet: false };
  if (remainder.clauses.length === 0) return { clauses: [condition] };

  const narrowed: UserFilterClause[] = [];
  for (const group of groupClauseIndexes(remainder)) {
    group.forEach((index) => narrowed.push(remainder.clauses[index]));
    narrowed.push(condition);
  }
  return normalise(narrowed);
}

/**
 * The one value the filter narrows a dimension to, when every group requires exactly that value -
 * used to highlight the selected row of a breakdown table. Null otherwise.
 */
export function singleValueFor(filter: UserFilter, dimension: string): string | null {
  const groups = groupClauseIndexes(filter);
  if (groups.length === 0) return null;

  let found: string | null = null;
  for (const group of groups) {
    const conditions = group.map((i) => filter.clauses[i]).filter((c) => c.dimension === dimension);
    if (conditions.length !== 1) return null;

    const [condition] = conditions;
    if (condition.operator !== 'is' || condition.includeNotSet || condition.values.length !== 1) return null;
    if (found !== null && found.toLowerCase() !== condition.values[0].toLowerCase()) return null;
    found = condition.values[0];
  }
  return found;
}

/**
 * The wire form `UserFilterCodec` reads: an array of clauses with short property names, defaults
 * omitted. Incomplete clauses are left out. Null when nothing is left.
 */
export function serializeUserFilter(filter: UserFilter | null | undefined): string | null {
  const clauses = (filter?.clauses ?? []).filter(clauseIsComplete);
  if (clauses.length === 0) return null;

  return JSON.stringify(
    clauses.map((clause, index) => {
      const item: Record<string, unknown> = {};
      if (index > 0 && clause.join === 'or') item.j = 'or';
      item.d = clause.dimension;
      if (clause.operator !== 'is') item.op = clause.operator;
      item.v = clause.values;
      if (clause.includeNotSet) item.n = true;
      return item;
    }),
  );
}

const OPERATORS: readonly UserFilterOperator[] = ['is', 'isNot', 'contains', 'notContains'];

/**
 * Reads the wire form back, for a filter carried in the page's own URL. Anything malformed is no
 * filter at all: a hand-edited link should open the unfiltered report, not an error.
 */
export function parseUserFilter(text: string | null | undefined): UserFilter {
  if (!text) return EMPTY_USER_FILTER;

  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return EMPTY_USER_FILTER;
  }

  if (!Array.isArray(parsed)) return EMPTY_USER_FILTER;

  const clauses: UserFilterClause[] = [];
  for (const raw of parsed.slice(0, MAX_CLAUSES)) {
    if (!raw || typeof raw !== 'object') return EMPTY_USER_FILTER;
    const item = raw as Record<string, unknown>;

    if (typeof item.d !== 'string' || !isKnownDimensionKey(item.d)) return EMPTY_USER_FILTER;
    const operator = (item.op ?? 'is') as UserFilterOperator;
    if (!OPERATORS.includes(operator)) return EMPTY_USER_FILTER;
    const values = Array.isArray(item.v) ? item.v.filter((v): v is string => typeof v === 'string') : [];

    const clause: UserFilterClause = {
      join: item.j === 'or' ? 'or' : 'and',
      dimension: item.d,
      operator,
      values,
      includeNotSet: item.n === true,
    };
    if (clauseIsComplete(clause)) clauses.push(clause);
  }

  return normalise(clauses);
}

/** How long the filter is on a query string - see `MAX_ENCODED_FILTER_LENGTH`. */
export function encodedFilterLength(filter: UserFilter): number {
  return encodeURIComponent(serializeUserFilter(filter) ?? '').length;
}

/** A stable identity for a filter, for effect dependencies and React keys. */
export function userFilterKey(filter: UserFilter | null | undefined): string {
  return serializeUserFilter(filter) ?? '';
}

/** Two filters that would narrow a report identically. */
export function sameUserFilter(a: UserFilter | null | undefined, b: UserFilter | null | undefined): boolean {
  return userFilterKey(a) === userFilterKey(b);
}
