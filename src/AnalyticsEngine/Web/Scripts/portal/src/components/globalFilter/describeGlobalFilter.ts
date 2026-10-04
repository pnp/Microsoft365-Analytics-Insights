import { formatNumber, plural, type TFunction, type TranslationKey } from '../../i18n';
import type { GlobalFilterClause, GlobalFilterClauseEcho } from '../../types/globalFilter';
import {
  describeClause,
  dimensionLabel,
  joinConditions,
  valueLabel,
  type DimensionNameSource,
} from '../userFilter/describeUserFilter';
import { groupClauseIndexes, isTextOperator } from '../userFilter/userFilterModel';
import { viewerKind, type ViewerKind } from './globalFilterModel';

/**
 * Reading the administrator's global filter back in words.
 *
 * A condition is read from one of two sides:
 *
 * - `reader` - to the person the filter applies to, with their own value filled in: "Department is
 *   Sales (from your profile)". This is what every Insights page shows.
 * - `definition` - as the administrator wrote it, for nobody in particular: "Department is the
 *   viewer's own value". This is what the editor shows.
 *
 * The values - department names, sign-in names - are tenant data and appear exactly as stored, through
 * the user filter's own `valueLabel`, which translates only the product's fixed tokens.
 */
export type GlobalFilterPerspective = 'reader' | 'definition';

/** The viewer value as the editor offers it - a label in a list, or on a pill. */
export const VIEWER_OPTION_KEYS: Record<ViewerKind, TranslationKey> = {
  own: 'globalFilter.viewer.option.own',
  self: 'globalFilter.viewer.option.self',
  manager: 'globalFilter.viewer.option.manager',
};

/** The same, inside a sentence: "Department is the viewer's own value". */
const VIEWER_PHRASE_KEYS: Record<ViewerKind, TranslationKey> = {
  own: 'globalFilter.viewer.phrase.own',
  self: 'globalFilter.viewer.phrase.self',
  manager: 'globalFilter.viewer.phrase.manager',
};

/** The reader's own value, with a word on where it came from. */
const READER_VALUE_KEYS: Record<ViewerKind, TranslationKey> = {
  own: 'globalFilter.reader.own',
  self: 'globalFilter.reader.self',
  manager: 'globalFilter.reader.manager',
};

/** A condition that needed a value the reader does not have - and so matches nobody. */
const UNRESOLVED_KEYS: Record<ViewerKind, TranslationKey> = {
  own: 'globalFilter.reader.unresolved.own',
  self: 'globalFilter.reader.unresolved.self',
  manager: 'globalFilter.reader.unresolved.manager',
};

/**
 * The reader's own value when it is a sign-in name and they lack See PII: the server withholds the name, so
 * the condition says whose it is instead - "you", "your manager".
 */
const READER_HIDDEN_KEYS: Record<ViewerKind, TranslationKey> = {
  own: 'globalFilter.reader.hidden.own',
  self: 'globalFilter.reader.hidden.self',
  manager: 'globalFilter.reader.hidden.manager',
};

function isEcho(clause: GlobalFilterClause | GlobalFilterClauseEcho): clause is GlobalFilterClauseEcho {
  return 'unresolved' in clause;
}

/** True when the condition matches nobody for this reader, because it needed a value they do not have. */
export function isUnresolved(clause: GlobalFilterClause | GlobalFilterClauseEcho): boolean {
  return isEcho(clause) && clause.unresolved;
}

/** True when the reader's value resolved but the server withheld it - see {@link READER_HIDDEN_KEYS}. */
function viewerValueHidden(clause: GlobalFilterClause | GlobalFilterClauseEcho): boolean {
  return isEcho(clause) && clause.viewerValueHidden === true;
}

/**
 * The administrator's values the server withheld from this reader, as one phrase: "3 named people", or for a
 * text search "2 search terms". Null when nothing was withheld.
 */
function hiddenValuesPhrase(t: TFunction, clause: GlobalFilterClause | GlobalFilterClauseEcho): string | null {
  const count = isEcho(clause) ? clause.hiddenValues ?? 0 : 0;
  if (count <= 0) return null;
  const keys: [TranslationKey, TranslationKey] = isTextOperator(clause.operator)
    ? ['globalFilter.reader.hiddenTerms.one', 'globalFilter.reader.hiddenTerms.other']
    : ['globalFilter.reader.hiddenPeople.one', 'globalFilter.reader.hiddenPeople.other'];
  return t(plural(count, keys[0], keys[1]), { count: formatNumber(count) });
}

/**
 * The viewer's value as a phrase, or null for a condition with fixed values only. `form` is where it
 * appears: in a sentence, or on its own as a label.
 */
export function viewerPhrase(
  t: TFunction,
  clause: GlobalFilterClause | GlobalFilterClauseEcho,
  perspective: GlobalFilterPerspective,
  form: 'sentence' | 'label' = 'sentence',
): string | null {
  if (!clause.viewerAttribute) return null;
  const kind = viewerKind(clause.viewerAttribute);

  if (perspective === 'definition' || !isEcho(clause)) {
    return t(form === 'label' ? VIEWER_OPTION_KEYS[kind] : VIEWER_PHRASE_KEYS[kind]);
  }
  if (clause.unresolved) return null;
  if (viewerValueHidden(clause)) return t(READER_HIDDEN_KEYS[kind]);
  if (!clause.viewerValue) return null;
  return t(READER_VALUE_KEYS[kind], { value: valueLabel(t, clause.dimension, clause.viewerValue) });
}

/** One condition as a phrase: "Department is Sales (from your profile)". */
export function describeGlobalClause(
  t: TFunction,
  clause: GlobalFilterClause | GlobalFilterClauseEcho,
  perspective: GlobalFilterPerspective,
  source?: DimensionNameSource,
): string {
  const label = dimensionLabel(t, clause.dimension, source);

  if (
    perspective === 'reader' &&
    clause.viewerAttribute &&
    isEcho(clause) &&
    (clause.unresolved || (!clause.viewerValue && !viewerValueHidden(clause)))
  ) {
    return t(UNRESOLVED_KEYS[viewerKind(clause.viewerAttribute)], { dimension: label });
  }

  const phrases = [hiddenValuesPhrase(t, clause), viewerPhrase(t, clause, perspective)].filter(
    (phrase): phrase is string => phrase !== null,
  );
  return describeClause(t, clause, label, phrases);
}

/** The filter as AND-groups of condition phrases, in evaluation order. */
export function describeGlobalGroups(
  t: TFunction,
  clauses: readonly (GlobalFilterClause | GlobalFilterClauseEcho)[],
  perspective: GlobalFilterPerspective,
  source?: DimensionNameSource,
): string[][] {
  return groupClauseIndexes({ clauses: [...clauses] }).map((group) =>
    group.map((index) => describeGlobalClause(t, clauses[index], perspective, source)),
  );
}

/**
 * The whole filter as one line, bracketing each group once there is more than one - the same shape as
 * `describeUserFilter`, so the two read alike when they appear one above the other.
 */
export function describeGlobalFilter(
  t: TFunction,
  clauses: readonly (GlobalFilterClause | GlobalFilterClauseEcho)[],
  perspective: GlobalFilterPerspective,
  source?: DimensionNameSource,
): string {
  const groups = describeGlobalGroups(t, clauses, perspective, source);
  if (groups.length === 0) return '';
  if (groups.length === 1) return joinConditions(t, groups[0], 'and');

  return joinConditions(
    t,
    groups.map((conditions) => t('userFilter.describe.group', { conditions: joinConditions(t, conditions, 'and') })),
    'or',
  );
}

/** How many values a pill spells out before summarising the rest as "+N more". */
const PILL_VALUES = 2;

/**
 * The values as a pill shows them: "not set", the fixed values, then the viewer's, the first few in full.
 * The viewer's value is worded as in a sentence - a pill reads "Management chain includes the viewer" -
 * while the capitalised option labels are kept for the value list itself.
 */
export function globalPillValues(
  t: TFunction,
  clause: GlobalFilterClause | GlobalFilterClauseEcho,
  perspective: GlobalFilterPerspective,
): string {
  if (perspective === 'reader' && isUnresolved(clause)) return t('globalFilter.pill.unresolved');

  const phrase = viewerPhrase(t, clause, perspective, 'sentence');
  const hidden = hiddenValuesPhrase(t, clause);
  const labels = [
    ...(clause.includeNotSet ? [t('userFilter.editor.notSet')] : []),
    ...clause.values.map((v) =>
      isTextOperator(clause.operator) ? t('userFilter.describe.quoted', { term: v }) : valueLabel(t, clause.dimension, v),
    ),
    ...(hidden ? [hidden] : []),
    ...(phrase ? [phrase] : []),
  ];

  const shown = labels.slice(0, PILL_VALUES).join(', ');
  const rest = labels.length - PILL_VALUES;
  return rest > 0 ? `${shown} ${t('userFilter.bar.moreValues', { count: rest })}` : shown;
}
