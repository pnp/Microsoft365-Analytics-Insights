/**
 * The user filter: a page-wide condition on the people a report covers, built from their Entra ID
 * attributes and any custom organisation types an administrator has defined.
 *
 * Mirrors `Common.Entities.UserFilters` on the server. The wire format (see `userFilterApi.ts`) and
 * the echo the server sends back (`UserFilterEcho`) are both defined there.
 */

/** How a clause compares a person's value with the clause's values. */
export type UserFilterOperator = 'is' | 'isNot' | 'contains' | 'notContains';

/**
 * How a clause joins the one before it. AND binds tighter than OR, so `A and B or C` means
 * `(A and B) or C` - the filter is a list of AND-groups joined by OR, which is how it is read back.
 */
export type UserFilterJoin = 'and' | 'or';

/** One condition, e.g. "Department is Sales or Marketing". */
export interface UserFilterClause {
  /** Ignored on the first clause. */
  join: UserFilterJoin;
  /** A dimension key: an Entra attribute (`department`) or a custom organisation type (`org:12`). */
  dimension: string;
  operator: UserFilterOperator;
  /** The values - or, for the text operators, the terms to look for. */
  values: string[];
  /** Whether "has no value" counts as one of the values. */
  includeNotSet: boolean;
}

/** A complete filter. No clauses means everyone. */
export interface UserFilter {
  clauses: UserFilterClause[];
}

export const EMPTY_USER_FILTER: UserFilter = { clauses: [] };

/** Where a dimension's values come from. */
export type UserFilterDimensionKind = 'entra' | 'custom';

/** One attribute a filter can use, as `GET api/UserFilter/dimensions` lists it. */
export interface UserFilterDimension {
  key: string;
  kind: UserFilterDimensionKind;
  /** The administrator's name for a custom organisation type. Null for an Entra attribute. */
  name: string | null;
  orgTypeId: number | null;
  distinctValues: number;
  peopleWithValue: number;
  /** Whether "contains" and "does not contain" are offered. */
  supportsTextMatch: boolean;
  /** True when the values are product tokens (member/guest, enabled/disabled) to translate. */
  fixedValues: boolean;
}

export interface UserFilterDimensionList {
  people: number;
  loadedUtc: string;
  dimensions: UserFilterDimension[];
}

export interface UserFilterValue {
  value: string;
  /** People holding the value - for the management chain, people reporting to the manager at any level. */
  people: number;
}

export interface UserFilterValuePage {
  dimension: string;
  values: UserFilterValue[];
  totalMatching: number;
  /** More values matched than were returned: the picker should search on the server. */
  truncated: boolean;
  /** The size of the "(not set)" option. */
  peopleWithoutValue: number;
}

/** The filter a response was actually narrowed by, echoed back by the server. */
export interface UserFilterEcho {
  clauses: UserFilterClause[];
  matchedPeople: number;
  directoryPeople: number;
  /** Dimensions the filter names that no longer exist (an org type deleted or disabled since). */
  unknownDimensions: string[];
  /** The administrator's names for the custom organisation types used, keyed by dimension. */
  dimensionNames: Record<string, string>;
}
