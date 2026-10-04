import type { UserFilterJoin } from '../../types/userFilter';
import type { GlobalFilterClause, GlobalFilterDefinition } from '../../types/globalFilter';
import {
  MANAGEMENT_CHAIN_DIMENSION,
  MAX_CLAUSES,
  MAX_TEXT_TERMS,
  MAX_TEXT_TERMS_PER_CLAUSE,
  MAX_VALUES_PER_CLAUSE,
  MAX_VALUE_LENGTH,
  USER_NAME_DIMENSION,
  groupClauseIndexes,
  isKnownDimensionKey,
  isTextOperator,
} from '../userFilter/userFilterModel';

/**
 * The global filter's rules, as pure functions: which of the viewer's own attributes a condition may
 * compare with, what a complete condition is, and the wire form `GlobalFilterCodec` reads.
 *
 * Everything else - operators, limits, how AND and OR group - is the user filter's, so the two can
 * never disagree about what a condition means.
 */

export const MANAGER_DIMENSION = 'manager';

/**
 * Mirrors `UserFilterCodec.MaxEncodedLength`: the longest definition the server reads. It travels in a
 * request body rather than a link, so this - not the user filter's query-string budget - is the limit.
 */
export const MAX_GLOBAL_FILTER_LENGTH = 8000;

/**
 * Which of the viewer's attributes a condition on `dimension` may compare with, in the order the editor
 * offers them. Mirrors `GlobalFilterViewerAttributes.AllowedFor`:
 *
 * - the sign-in name compares with the viewer themselves ("only my own figures");
 * - the manager and the management chain compare with the viewer ("my team", "my organisation") or with
 *   the viewer's manager ("my peers");
 * - everything else compares with the viewer's own value of the same attribute ("my department").
 */
export function viewerAttributesFor(dimension: string): readonly string[] {
  if (!isKnownDimensionKey(dimension)) return [];
  if (dimension === USER_NAME_DIMENSION) return [USER_NAME_DIMENSION];
  if (dimension === MANAGER_DIMENSION || dimension === MANAGEMENT_CHAIN_DIMENSION) {
    return [USER_NAME_DIMENSION, MANAGER_DIMENSION];
  }
  return [dimension];
}

/** How a viewer value reads: the viewer's own value, the viewer themselves, or the viewer's manager. */
export type ViewerKind = 'own' | 'self' | 'manager';

export function viewerKind(viewerAttribute: string): ViewerKind {
  if (viewerAttribute === USER_NAME_DIMENSION) return 'self';
  if (viewerAttribute === MANAGER_DIMENSION) return 'manager';
  return 'own';
}

/** A condition the server would accept: a value, "not set", or the viewer's own value. */
export function globalClauseIsComplete(clause: GlobalFilterClause): boolean {
  return clause.values.length > 0 || clause.includeNotSet || !!clause.viewerAttribute;
}

export function isEmptyGlobalFilter(filter: GlobalFilterDefinition | null | undefined): boolean {
  return !filter || filter.clauses.filter(globalClauseIsComplete).length === 0;
}

export function newGlobalClause(dimension = '', join: UserFilterJoin = 'and'): GlobalFilterClause {
  return { join, dimension, operator: 'is', values: [], includeNotSet: false, viewerAttribute: null };
}

/** The first condition has nothing to join to, so its join is always AND. */
function normalise(clauses: GlobalFilterClause[]): GlobalFilterDefinition {
  return { clauses: clauses.map((c, i) => (i === 0 && c.join !== 'and' ? { ...c, join: 'and' } : c)) };
}

export function addGlobalClause(filter: GlobalFilterDefinition, clause: GlobalFilterClause): GlobalFilterDefinition {
  return normalise([...filter.clauses, clause]);
}

export function replaceGlobalClause(
  filter: GlobalFilterDefinition,
  index: number,
  clause: GlobalFilterClause,
): GlobalFilterDefinition {
  return normalise(filter.clauses.map((c, i) => (i === index ? clause : c)));
}

export function setGlobalJoin(filter: GlobalFilterDefinition, index: number, join: UserFilterJoin): GlobalFilterDefinition {
  return normalise(filter.clauses.map((c, i) => (i === index ? { ...c, join } : c)));
}

/**
 * Removes a condition without moving any other condition into a different group: dropping the one that
 * opens an OR group would otherwise leave the rest of that group ANDed to the group before it. The same
 * rule as `removeClause` for the user filter.
 */
export function removeGlobalClause(filter: GlobalFilterDefinition, index: number): GlobalFilterDefinition {
  const groups = groupClauseIndexes(filter)
    .map((group) => group.filter((i) => i !== index).map((i) => filter.clauses[i]))
    .filter((group) => group.length > 0);

  const clauses: GlobalFilterClause[] = [];
  groups.forEach((group, g) =>
    group.forEach((clause, i) => clauses.push({ ...clause, join: g > 0 && i === 0 ? 'or' : 'and' })),
  );
  return { clauses };
}

/**
 * The wire form `GlobalFilterCodec` reads: the user filter's, plus `vu` for a condition that compares
 * with the viewer. Incomplete conditions are left out. An empty string - which removes the filter - when
 * nothing is left.
 */
export function serializeGlobalFilter(filter: GlobalFilterDefinition | null | undefined): string {
  const clauses = (filter?.clauses ?? []).filter(globalClauseIsComplete);
  if (clauses.length === 0) return '';

  return JSON.stringify(
    clauses.map((clause, index) => {
      const item: Record<string, unknown> = {};
      if (index > 0 && clause.join === 'or') item.j = 'or';
      item.d = clause.dimension;
      if (clause.operator !== 'is') item.op = clause.operator;
      item.v = clause.values;
      if (clause.includeNotSet) item.n = true;
      if (clause.viewerAttribute) item.vu = clause.viewerAttribute;
      return item;
    }),
  );
}

/** Two definitions that would narrow every report identically. */
export function sameGlobalFilter(
  a: GlobalFilterDefinition | null | undefined,
  b: GlobalFilterDefinition | null | undefined,
): boolean {
  return serializeGlobalFilter(a) === serializeGlobalFilter(b);
}

/** The conditions the server returned, as the editor holds them. */
export function fromClauseModels(clauses: readonly GlobalFilterClause[] | null | undefined): GlobalFilterDefinition {
  return normalise(
    (clauses ?? []).map((c) => ({
      join: c.join === 'or' ? 'or' : 'and',
      dimension: c.dimension,
      operator: c.operator,
      values: [...(c.values ?? [])],
      includeNotSet: !!c.includeNotSet,
      viewerAttribute: c.viewerAttribute || null,
    })),
  );
}

/** Why the server would refuse a definition, checked where it is made so the editor can say so. */
export type GlobalFilterProblem = 'tooManyClauses' | 'tooManyValues' | 'valueTooLong' | 'tooManyTerms' | 'tooLong' | 'viewerText';

export function globalFilterProblem(filter: GlobalFilterDefinition): GlobalFilterProblem | null {
  const clauses = filter.clauses;
  if (clauses.length > MAX_CLAUSES) return 'tooManyClauses';
  if (clauses.some((c) => c.values.length > MAX_VALUES_PER_CLAUSE)) return 'tooManyValues';
  if (clauses.some((c) => c.values.some((v) => v.trim().length > MAX_VALUE_LENGTH))) return 'valueTooLong';

  const text = clauses.filter((c) => isTextOperator(c.operator));
  if (text.some((c) => !!c.viewerAttribute)) return 'viewerText';
  if (
    text.some((c) => c.values.length > MAX_TEXT_TERMS_PER_CLAUSE) ||
    text.reduce((total, c) => total + c.values.length, 0) > MAX_TEXT_TERMS
  ) {
    return 'tooManyTerms';
  }

  return serializeGlobalFilter(filter).length > MAX_GLOBAL_FILTER_LENGTH ? 'tooLong' : null;
}

/** True when what the filter matches depends on who is viewing. */
export function usesViewer(clauses: readonly GlobalFilterClause[]): boolean {
  return clauses.some((c) => !!c.viewerAttribute);
}
