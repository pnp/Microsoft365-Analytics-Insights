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

/**
 * The standard Entra ID attributes, in the order the property picker offers them - the server's
 * `UserFilterDimensions.EntraKeys` order: who someone is (their name, and the organisation their
 * address belongs to) first, then where they sit in the business, then account and hierarchy.
 */
export const ENTRA_DIMENSION_KEYS = [
  'userName',
  'emailDomain',
  'department',
  'jobTitle',
  'companyName',
  'officeLocation',
  'country',
  'stateOrProvince',
  'usageLocation',
  'userType',
  'accountStatus',
  'manager',
  'managementChain',
] as const;

export type EntraDimensionKey = (typeof ENTRA_DIMENSION_KEYS)[number];

export const USER_NAME_DIMENSION: EntraDimensionKey = 'userName';
export const EMAIL_DOMAIN_DIMENSION: EntraDimensionKey = 'emailDomain';
export const MANAGEMENT_CHAIN_DIMENSION: EntraDimensionKey = 'managementChain';

/** A custom organisation type's key is this prefix and the org type id. */
export const CUSTOM_DIMENSION_PREFIX = 'org:';

/**
 * The values a fixed-value dimension accepts - `UserFilterTokens` on the server, which compares them
 * case-insensitively. These dimensions' values are product tokens, not tenant data.
 */
export const FIXED_VALUE_TOKENS: Readonly<Record<string, readonly string[]>> = {
  userType: ['member', 'guest'],
  accountStatus: ['enabled', 'disabled'],
};

function fixedValueTokens(dimension: string): readonly string[] | null {
  return Object.prototype.hasOwnProperty.call(FIXED_VALUE_TOKENS, dimension) ? FIXED_VALUE_TOKENS[dimension] : null;
}

/** Mirrors `UserFilterDimensions.SupportsTextMatch`: not on product tokens, nor on a hierarchy. */
function supportsTextMatch(dimension: string): boolean {
  return fixedValueTokens(dimension) === null && dimension !== MANAGEMENT_CHAIN_DIMENSION;
}

/**
 * The longest filter the page will send, measured URL-encoded. It rides on GET query strings - the
 * exports are plain links - and the web app allows 16 KB for those, so this leaves room for every
 * other parameter while keeping the portal, not the platform, as the thing that says "too long".
 * The server's own limit (8,000 decoded characters) is never reached by a filter under this one.
 */
export const MAX_ENCODED_FILTER_LENGTH = 6000;

/** Mirrors `UserFilterCodec.MaxClauses`. */
export const MAX_CLAUSES = 25;

/**
 * Mirrors `UserFilterCodec.MaxValuesPerClause`. Values are picked one at a time, so a link is the
 * likelier way to reach it than a hand - but a filter the server would refuse is refused here, where
 * the page can say why.
 */
export const MAX_VALUES_PER_CLAUSE = 500;

/** Mirrors `UserFilterCodec.MaxValueLength`: the widest value a condition may carry - a typed term included. */
export const MAX_VALUE_LENGTH = 848;

/**
 * Mirrors `UserFilterCodec.MaxTextTermsPerClause` and `MaxTextTerms`: each piece of text a "contains"
 * condition looks for is searched for in every distinct value - one per person for the user name - so
 * these are far lower than the number of values a condition may pick from a list.
 */
export const MAX_TEXT_TERMS_PER_CLAUSE = 10;
export const MAX_TEXT_TERMS = 10;

export function isEntraDimension(key: string): key is EntraDimensionKey {
  return (ENTRA_DIMENSION_KEYS as readonly string[]).includes(key);
}

export function isCustomDimension(key: string): boolean {
  return key.startsWith(CUSTOM_DIMENSION_PREFIX);
}

/** The largest id `int.TryParse` reads - ten digits would otherwise pass for `org:9999999999`. */
const MAX_ORG_TYPE_ID = 2147483647;

/**
 * A key the server will accept: a standard Entra attribute, or `org:` and a positive id with no
 * leading zero that fits the server's `int` - the same rule as `UserFilterDimensions.TryParseOrgTypeId`.
 * Checked when reading a filter out of a link, so a hand-edited key opens the unfiltered report rather
 * than a page the server refuses to answer.
 */
export function isKnownDimensionKey(key: string): boolean {
  if (isEntraDimension(key)) return true;
  const id = /^org:([1-9]\d{0,9})$/.exec(key);
  return id !== null && Number(id[1]) <= MAX_ORG_TYPE_ID;
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

/** The clauses of each AND-group, in order. */
function toGroups(filter: UserFilter): UserFilterClause[][] {
  return groupClauseIndexes(filter).map((group) => group.map((index) => filter.clauses[index]));
}

/**
 * Flattens AND-groups back into clauses: each group after the first opens with OR, every other clause
 * joins with AND, and a group left empty disappears.
 *
 * Every edit that removes a clause goes through here. The join lives on each clause, so dropping the
 * clause that OPENS an OR group without this would leave the rest of that group joined by AND to the
 * group before it - silently turning "(Sales) or (UK and London)" into "Sales and London".
 */
function fromGroups(groups: UserFilterClause[][]): UserFilter {
  const clauses: UserFilterClause[] = [];
  groups
    .filter((group) => group.length > 0)
    .forEach((group, g) =>
      group.forEach((clause, i) => clauses.push({ ...clause, join: g > 0 && i === 0 ? 'or' : 'and' })),
    );
  return { clauses };
}

export function removeClause(filter: UserFilter, index: number): UserFilter {
  return fromGroups(
    groupClauseIndexes(filter).map((group) => group.filter((i) => i !== index).map((i) => filter.clauses[i])),
  );
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
 * Within each OR-group an existing condition on the dimension is replaced where it stands, and a group
 * without one gets the new condition added: `(A) or (B)` narrowed to domain X becomes
 * `(A and X) or (B and X)`, which is "the same people, only on X". Appending one clause to the end would
 * only have narrowed the last group, because AND binds tighter than OR. Passing `null` removes the
 * dimension's conditions altogether, dropping any group that held nothing else.
 */
export function withDimensionValue(filter: UserFilter, dimension: string, value: string | null): UserFilter {
  const groups = toGroups(filter);

  if (value === null) return fromGroups(groups.map((group) => group.filter((c) => c.dimension !== dimension)));

  const condition: UserFilterClause = { join: 'and', dimension, operator: 'is', values: [value], includeNotSet: false };
  if (groups.length === 0) return { clauses: [condition] };

  return fromGroups(
    groups.map((group) => {
      // Everything before the first condition on the dimension is something else, so its position in
      // the group is also its position among the survivors.
      const at = group.findIndex((c) => c.dimension === dimension);
      const others = group.filter((c) => c.dimension !== dimension);
      if (at < 0) return [...others, condition];

      others.splice(at, 0, condition);
      return others;
    }),
  );
}

/**
 * Whether a filter can be sent at all: within the server's clause and text-search limits and short
 * enough for a query string. An edit that would break any of them is refused where it is made, never
 * sent to be rejected.
 */
export function fitsLimits(filter: UserFilter): boolean {
  return (
    filter.clauses.length <= MAX_CLAUSES &&
    !hasTooManyValues(filter) &&
    !hasTooWideValue(filter) &&
    !hasTooManyTextTerms(filter) &&
    encodedFilterLength(filter) <= MAX_ENCODED_FILTER_LENGTH
  );
}

/** Whether a condition has more values than the server accepts for one condition. */
export function hasTooManyValues(filter: UserFilter): boolean {
  return filter.clauses.some((c) => c.values.length > MAX_VALUES_PER_CLAUSE);
}

/** Whether any value is wider than the server accepts - measured as it does, trimmed, in UTF-16 units. */
function hasTooWideValue(filter: UserFilter): boolean {
  return filter.clauses.some((c) => c.values.some((v) => v.trim().length > MAX_VALUE_LENGTH));
}

/** Whether the filter looks for more pieces of text than the server will search for. */
export function hasTooManyTextTerms(filter: UserFilter): boolean {
  const textClauses = filter.clauses.filter((c) => isTextOperator(c.operator));
  return (
    textClauses.some((c) => c.values.length > MAX_TEXT_TERMS_PER_CLAUSE) ||
    textClauses.reduce((total, c) => total + c.values.length, 0) > MAX_TEXT_TERMS
  );
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

  // Refused rather than truncated: dropping the last clauses of a filter changes who it matches -
  // cutting a group's domain condition off would widen that group to every domain.
  if (parsed.length > MAX_CLAUSES) return EMPTY_USER_FILTER;

  const clauses: UserFilterClause[] = [];
  for (const raw of parsed) {
    if (!raw || typeof raw !== 'object') return EMPTY_USER_FILTER;
    const item = raw as Record<string, unknown>;

    if (typeof item.d !== 'string' || !isKnownDimensionKey(item.d)) return EMPTY_USER_FILTER;
    const operator = (item.op ?? 'is') as UserFilterOperator;
    if (!OPERATORS.includes(operator)) return EMPTY_USER_FILTER;

    // Read as the server reads them (`UserFilterCodec.ParseJoin`, `ReadBool`): absent is the default,
    // and anything the server would refuse - "xor", or "true" as a string - is a link this page did
    // not write, not one to be read as AND, or as false.
    if (item.j != null && item.j !== 'and' && item.j !== 'or') return EMPTY_USER_FILTER;
    if (item.n != null && typeof item.n !== 'boolean') return EMPTY_USER_FILTER;

    // Refused rather than tidied, for the same reason as a truncation: the server rejects each of
    // these, and quietly dropping the part it would reject sends a different filter from the link's.
    if (item.v != null && !Array.isArray(item.v)) return EMPTY_USER_FILTER;
    const rawValues: unknown[] = Array.isArray(item.v) ? item.v : [];
    if (rawValues.some((v) => typeof v !== 'string')) return EMPTY_USER_FILTER;
    // Blank values are skipped, as the server skips them - so a condition of nothing but blanks is
    // the incomplete one refused below, not one the server answers with an error on every panel.
    const values = (rawValues as string[]).filter((v) => v.trim() !== '');
    if (isTextOperator(operator) && !supportsTextMatch(item.d)) return EMPTY_USER_FILTER;
    const tokens = fixedValueTokens(item.d);
    if (tokens && values.some((v) => !tokens.includes(v.trim().toLowerCase()))) return EMPTY_USER_FILTER;

    const clause: UserFilterClause = {
      join: item.j === 'or' ? 'or' : 'and',
      dimension: item.d,
      operator,
      values,
      includeNotSet: item.n === true,
    };

    // The page never writes an incomplete condition, and dropping one would move the group
    // boundary it carried - so a link holding one was not made here, and opens unfiltered.
    if (!clauseIsComplete(clause)) return EMPTY_USER_FILTER;
    clauses.push(clause);
  }

  // The page never writes a filter the server would refuse - the editor stops the edit that would
  // make one - so a link past any of its limits was not made here either, and opens unfiltered
  // rather than as an error on every panel.
  const filter = normalise(clauses);
  return fitsLimits(filter) ? filter : EMPTY_USER_FILTER;
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
