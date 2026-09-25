import { EN_CATALOG, type TFunction, type TranslationKey } from '../../i18n';

/**
 * Placeholder labels the SERVER writes into data, as opposed to the tenant's own names beside them:
 * the bucket for people with no department, the roll-up of every site outside the top N, the name
 * of a browser nobody could identify.
 *
 * They are product text, so a Spanish reader should not find "(no department)" in a table whose own
 * explanation, a line above it, says people without one are grouped as "(sin departamento)". But the
 * API sends them as the value itself, with no key beside it, so the SPA can only recognise them by
 * their exact English - which is why each English catalog value below is word for word what the C#
 * or SQL writes, and why serverAuthoredText.test.ts reads that C# and SQL and fails when the two lists
 * disagree.
 *
 * Only a whole-value, exact match is replaced, and only where the value is displayed. A tenant's own
 * department or site name is never looked up; filters, selections and joins keep the server's value.
 */
export const SERVER_PLACEHOLDER_KEYS: readonly TranslationKey[] = [
  'common.serverPlaceholder.noDepartment',
  'common.serverPlaceholder.noDepartmentCapitalised',
  'common.serverPlaceholder.noCountry',
  'common.serverPlaceholder.noOffice',
  'common.serverPlaceholder.noCompany',
  'common.serverPlaceholder.noManager',
  'common.serverPlaceholder.noDomain',
  'common.serverPlaceholder.noDomainCapitalised',
  'common.serverPlaceholder.noReasonRecorded',
  'common.serverPlaceholder.unknown',
  'common.serverPlaceholder.unknownSite',
  'common.serverPlaceholder.otherSites',
  'common.serverPlaceholder.unknownCountry',
  'common.serverPlaceholder.otherCountries',
  'common.serverPlaceholder.unknownDevice',
  'common.serverPlaceholder.otherDevices',
  'common.serverPlaceholder.untitledElement',
  'common.serverPlaceholder.unnamedAgent',
  'common.serverPlaceholder.notSet',
  'common.serverPlaceholder.notStated',
  'common.serverPlaceholder.none',
  'common.serverPlaceholder.disabled',
  'common.serverPlaceholder.redisNotConfigured',
];

const KEY_BY_SERVER_TEXT: ReadonlyMap<string, TranslationKey> = new Map(
  SERVER_PLACEHOLDER_KEYS.map((key) => [EN_CATALOG[key], key]),
);

/**
 * The value to display: the translation when the server wrote one of its own placeholders, and the
 * value exactly as it arrived otherwise.
 */
export function serverPlaceholderText(t: TFunction, value: string): string;
export function serverPlaceholderText(t: TFunction, value: string | null | undefined): string | null | undefined;
export function serverPlaceholderText(t: TFunction, value: string | null | undefined): string | null | undefined {
  if (!value) return value;
  const key = KEY_BY_SERVER_TEXT.get(value);
  return key ? t(key) : value;
}
