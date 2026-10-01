import { formatList, type TFunction, type TranslationKey } from '../../i18n';
import type { UserFilter, UserFilterClause, UserFilterDimension, UserFilterOperator } from '../../types/userFilter';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import {
  EMAIL_DOMAIN_DIMENSION,
  FIXED_VALUE_TOKENS,
  MANAGEMENT_CHAIN_DIMENSION,
  groupClauseIndexes,
  isCustomDimension,
  isEntraDimension,
  isTextOperator,
  type EntraDimensionKey,
} from './userFilterModel';

/**
 * Reading a filter back in words - on the pills, in the banner above the figures, and on paper.
 *
 * Only the product's own wording is translated. The values themselves - department names, cost
 * centres, managers' addresses - are tenant data and appear exactly as stored, as does the name an
 * administrator gave a custom organisation type. The only values translated are the fixed tokens the
 * server defines (member/guest, enabled/disabled).
 */

/** The label key for each standard Entra ID attribute. The server sends the key, never the words. */
export const ENTRA_DIMENSION_LABEL_KEYS: Record<EntraDimensionKey, TranslationKey> = {
  userName: 'userFilter.dimension.userName',
  department: 'userFilter.dimension.department',
  jobTitle: 'userFilter.dimension.jobTitle',
  companyName: 'userFilter.dimension.companyName',
  officeLocation: 'userFilter.dimension.officeLocation',
  country: 'userFilter.dimension.country',
  stateOrProvince: 'userFilter.dimension.stateOrProvince',
  usageLocation: 'userFilter.dimension.usageLocation',
  emailDomain: 'userFilter.dimension.emailDomain',
  userType: 'userFilter.dimension.userType',
  accountStatus: 'userFilter.dimension.accountStatus',
  manager: 'userFilter.dimension.manager',
  managementChain: 'userFilter.dimension.managementChain',
};

/** The label key for each fixed value (`UserFilterTokens` on the server). */
export const USER_FILTER_TOKEN_LABEL_KEYS: Record<string, TranslationKey> = {
  member: 'userFilter.token.member',
  guest: 'userFilter.token.guest',
  enabled: 'userFilter.token.enabled',
  disabled: 'userFilter.token.disabled',
};

/** The dimensions whose values are those tokens. */
const FIXED_VALUE_DIMENSIONS = new Set<string>(Object.keys(FIXED_VALUE_TOKENS));

/**
 * Where to find the administrator's name for a custom organisation type: the dimension list the
 * picker loaded, or the names the server echoed with the filter it applied. Either may be missing -
 * a filter opened from a link can render before the dimension list arrives.
 */
export interface DimensionNameSource {
  dimensions?: UserFilterDimension[] | null;
  names?: Record<string, string> | null;
}

export function dimensionLabel(t: TFunction, key: string, source?: DimensionNameSource): string {
  if (isEntraDimension(key)) return t(ENTRA_DIMENSION_LABEL_KEYS[key]);

  const name = source?.names?.[key] ?? source?.dimensions?.find((d) => d.key === key)?.name;
  if (name) return name;

  return isCustomDimension(key) ? t('userFilter.dimension.unknown') : key;
}

/** One value as shown: a fixed token translated, anything else verbatim. */
export function valueLabel(t: TFunction, dimension: string, value: string): string {
  if (FIXED_VALUE_DIMENSIONS.has(dimension)) {
    const key = USER_FILTER_TOKEN_LABEL_KEYS[value];
    if (key) return t(key);
  }
  // The domain breakdown's "(no domain)" row is the server's own placeholder, not a domain anyone has
  // - no real domain can be spelled that way. Chosen from the table it travels as that text, which the
  // server reads as "not set", and it is shown in the reader's language like the row it came from.
  if (dimension === EMAIL_DOMAIN_DIMENSION) return serverPlaceholderText(t, value);
  return value;
}

/** The operator as the picker offers it - the management chain reads "includes", not "is". */
export function operatorLabel(t: TFunction, dimension: string, operator: UserFilterOperator): string {
  if (dimension === MANAGEMENT_CHAIN_DIMENSION) {
    return operator === 'isNot' ? t('userFilter.operator.notIncludes') : t('userFilter.operator.includes');
  }

  switch (operator) {
    case 'isNot':
      return t('userFilter.operator.isNot');
    case 'contains':
      return t('userFilter.operator.contains');
    case 'notContains':
      return t('userFilter.operator.notContains');
    default:
      return t('userFilter.operator.is');
  }
}

/** The operator as a pill shows it: "=" and "≠" where a symbol is clearer than a word. */
export function operatorShortLabel(t: TFunction, dimension: string, operator: UserFilterOperator): string {
  if (dimension === MANAGEMENT_CHAIN_DIMENSION) return operatorLabel(t, dimension, operator);

  switch (operator) {
    case 'isNot':
      return t('userFilter.operator.short.isNot');
    case 'contains':
      return t('userFilter.operator.short.contains');
    case 'notContains':
      return t('userFilter.operator.short.notContains');
    default:
      return t('userFilter.operator.short.is');
  }
}

/** Each value as it reads in a sentence: text terms quoted, tokens translated. */
function valuePhrases(t: TFunction, clause: UserFilterClause): string[] {
  return clause.values.map((value) =>
    isTextOperator(clause.operator)
      ? t('userFilter.describe.quoted', { term: value })
      : valueLabel(t, clause.dimension, value),
  );
}

function orList(values: string[]): string {
  return formatList(values, { style: 'long', type: 'disjunction' });
}

/**
 * One condition as a phrase, e.g. "Department is Sales or Marketing".
 *
 * Worded so it never reads as a double negative: "is not" combined with "not set" becomes "is set and
 * is not". The server's English description (`UserFilterDescriber`) follows the same rules, so the
 * Excel workbook and the page say the same thing.
 *
 * `extraPhrases` are values already worded by the caller - the administrator's global filter adds "the
 * viewer's own value" this way - and are listed after the condition's own values.
 */
export function describeClause(t: TFunction, clause: UserFilterClause, label: string, extraPhrases: readonly string[] = []): string {
  const values = [...valuePhrases(t, clause), ...extraPhrases];

  if (clause.dimension === MANAGEMENT_CHAIN_DIMENSION) {
    const list = orList(values.length > 0 ? values : [t('userFilter.describe.notSet')]);
    return clause.operator === 'isNot'
      ? t('userFilter.describe.chainIsNot', { dimension: label, values: list })
      : t('userFilter.describe.chainIs', { dimension: label, values: list });
  }

  switch (clause.operator) {
    case 'isNot':
      if (values.length === 0) return t('userFilter.describe.setOnly', { dimension: label });
      return clause.includeNotSet
        ? t('userFilter.describe.isNotAndSet', { dimension: label, values: orList(values) })
        : t('userFilter.describe.isNot', { dimension: label, values: orList(values) });

    case 'contains':
      return clause.includeNotSet
        ? t('userFilter.describe.containsOrNotSet', { dimension: label, values: orList(values) })
        : t('userFilter.describe.contains', { dimension: label, values: orList(values) });

    case 'notContains':
      return clause.includeNotSet
        ? t('userFilter.describe.notContainsAndSet', { dimension: label, values: orList(values) })
        : t('userFilter.describe.notContains', { dimension: label, values: orList(values) });

    default:
      if (values.length === 0) return t('userFilter.describe.notSetOnly', { dimension: label });
      return t('userFilter.describe.is', {
        dimension: label,
        values: orList(clause.includeNotSet ? [...values, t('userFilter.describe.notSet')] : values),
      });
  }
}

/** The filter as AND-groups of condition phrases, in evaluation order. */
export function describeGroups(t: TFunction, filter: UserFilter, source?: DimensionNameSource): string[][] {
  return groupClauseIndexes(filter).map((group) =>
    group.map((index) => {
      const clause = filter.clauses[index];
      return describeClause(t, clause, dimensionLabel(t, clause.dimension, source));
    }),
  );
}

/**
 * Joins phrases with "and" or "or" through a whole-sentence template, so each language places the
 * conjunction itself rather than having it glued on as a fragment.
 */
export function joinConditions(t: TFunction, conditions: string[], join: 'and' | 'or'): string {
  if (conditions.length === 0) return '';
  const key = join === 'or' ? 'userFilter.describe.or' : 'userFilter.describe.and';
  return conditions.slice(1).reduce((left, right) => t(key, { left, right }), conditions[0]);
}

/**
 * The whole filter as one line: "Department is Sales and Country or region is United Kingdom", or with
 * OR groups "(Department is Sales and Company is Contoso) or (Cost centre is CC-12)". Every group is
 * bracketed once there is more than one, because a condition can itself contain "or" - "Cost centre is
 * CC-12 or not set" - and without brackets the reader cannot tell which "or" joins what.
 */
export function describeUserFilter(t: TFunction, filter: UserFilter, source?: DimensionNameSource): string {
  const groups = describeGroups(t, filter, source);

  if (groups.length === 1) return joinConditions(t, groups[0], 'and');

  return joinConditions(
    t,
    groups.map((conditions) => t('userFilter.describe.group', { conditions: joinConditions(t, conditions, 'and') })),
    'or',
  );
}
