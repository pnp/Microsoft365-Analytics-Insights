import { formatNumber, type TFunction, type TranslationKey } from '../../i18n';
import type { UserOrgImportStatus, UserOrgType } from '../../types/userOrgs';

/**
 * The messages the API sends as a code - `UserOrgMessageCodes` on the server - as catalogue keys.
 *
 * The API reports facts and the portal writes the sentences: a validation refusal, a live attribute
 * test's outcome and a discovery warning each arrive with a stable code, and the server's English is
 * shown only for a code this build does not know. `src/i18n/lint/serverAuthoredText.test.ts` reads
 * the C# and fails when this map and the codes drift apart.
 */
export const USER_ORG_MESSAGE_KEYS: Record<string, TranslationKey> = {
  noType: 'userOrgs.message.noType',
  nameRequired: 'userOrgs.message.nameRequired',
  nameTooLong: 'userOrgs.message.nameTooLong',
  duplicateName: 'userOrgs.message.duplicateName',
  invalidSource: 'userOrgs.message.invalidSource',
  typeGone: 'userOrgs.message.typeGone',
  importRunningChange: 'userOrgs.message.importRunningChange',
  importRunningDelete: 'userOrgs.message.importRunningDelete',
  attributeRequired: 'userOrgs.message.attributeRequired',
  attributeTooLong: 'userOrgs.message.attributeTooLong',
  openExtension: 'userOrgs.message.openExtension',
  attributeHasSpaces: 'userOrgs.message.attributeHasSpaces',
  badDirectoryExtension: 'userOrgs.message.badDirectoryExtension',
  tooManyDots: 'userOrgs.message.tooManyDots',
  nothingAfterDot: 'userOrgs.message.nothingAfterDot',
  notEmployeeOrgDataProperty: 'userOrgs.message.notEmployeeOrgDataProperty',
  directoryExtensionSubProperty: 'userOrgs.message.directoryExtensionSubProperty',
  unknownContainer: 'userOrgs.message.unknownContainer',
  badSchemaProperty: 'userOrgs.message.badSchemaProperty',
  employeeOrgDataContainer: 'userOrgs.message.employeeOrgDataContainer',
  onPremisesContainer: 'userOrgs.message.onPremisesContainer',
  unsupportedAttribute: 'userOrgs.message.unsupportedAttribute',
  badOnPremisesAttribute: 'userOrgs.message.badOnPremisesAttribute',
  noTestRequest: 'userOrgs.message.noTestRequest',
  upnRequired: 'userOrgs.message.upnRequired',
  graphAuthFailed: 'userOrgs.message.graphAuthFailed',
  userNotFound: 'userOrgs.message.userNotFound',
  propertyRejected: 'userOrgs.message.propertyRejected',
  notAuthorised: 'userOrgs.message.notAuthorised',
  throttled: 'userOrgs.message.throttled',
  graphError: 'userOrgs.message.graphError',
  unreadableResponse: 'userOrgs.message.unreadableResponse',
  multiValued: 'userOrgs.message.multiValued',
  noValue: 'userOrgs.message.noValue',
  wouldTruncate: 'userOrgs.message.wouldTruncate',
  discoveryAuthFailed: 'userOrgs.message.discoveryAuthFailed',
  discoveryForbidden: 'userOrgs.message.discoveryForbidden',
  discoveryGraphError: 'userOrgs.message.discoveryGraphError',
  discoveryNoneReturned: 'userOrgs.message.discoveryNoneReturned',
  discoveryUnreachable: 'userOrgs.message.discoveryUnreachable',
};

/**
 * A server message worded in the reader's language: from its code when this build knows it, otherwise
 * the server's English, otherwise nothing.
 */
export function userOrgMessage(
  code: string | null | undefined,
  values: Record<string, string | number | null> | null | undefined,
  fallback: string | null | undefined,
  t: TFunction,
): string | null {
  if (code && Object.prototype.hasOwnProperty.call(USER_ORG_MESSAGE_KEYS, code)) {
    const facts = values ?? {};
    return t(USER_ORG_MESSAGE_KEYS[code], {
      name: String(facts.name ?? ''),
      attribute: String(facts.attribute ?? ''),
      property: String(facts.property ?? ''),
      container: String(facts.container ?? ''),
      max: formatNumber(Number(facts.max ?? 0)),
      status: String(facts.status ?? ''),
    });
  }
  return fallback || null;
}

/**
 * An error from the user organisation API, worded in the reader's language when it carries a code
 * this build knows. Recognised by its `code` rather than by class, so it reads any error shaped like
 * `UserOrgApiError` without this module depending on the API client.
 */
export function userOrgErrorMessage(error: unknown, t: TFunction, fallbackKey: TranslationKey): string {
  if (error instanceof Error) {
    const coded = error as Error & { code?: unknown; values?: unknown };
    const code = typeof coded.code === 'string' ? coded.code : null;
    const values =
      coded.values && typeof coded.values === 'object' ? (coded.values as Record<string, string | number | null>) : null;
    return userOrgMessage(code, values, error.message, t) ?? t(fallbackKey);
  }
  return t(fallbackKey);
}

/**
 * A CSV import's status as a catalogue key.
 *
 * The status arrives from the server as a fixed token, not as text, so it has to be mapped to a
 * translated word rather than rendered. Shared because both the types table and the import panel
 * show it, and two copies would drift the moment a status is added - the compiler catches a missing
 * one here because the record is keyed by the status union itself.
 */
export const STATUS_KEYS: Record<UserOrgImportStatus, TranslationKey> = {
  pending: 'userOrgs.status.pending',
  running: 'userOrgs.status.running',
  succeeded: 'userOrgs.status.succeeded',
  failed: 'userOrgs.status.failed',
  cancelled: 'userOrgs.status.cancelled',
  interrupted: 'userOrgs.status.interrupted',
};

/**
 * What to show in the "Last refreshed" column for a type that has never been refreshed, as a
 * catalogue key.
 *
 * Says what the admin is waiting for rather than just "never", because the next step differs by
 * source - and for a disabled type there is nothing to wait for, since it is never imported. The CSV
 * wording is about a successful import, not an upload: the Source column beside it can already say a
 * file was imported and failed, and the two must not contradict each other.
 *
 * The same goes for a CSV type whose last import succeeded. A successful apply records its time in the
 * same transaction, and only a change of source clears it again - so a succeeded import with no time
 * means a later switch to Entra and back discarded what it applied.
 */
export function neverRefreshedKey(
  type: Pick<UserOrgType, 'source' | 'isEnabled' | 'lastImport'>,
): TranslationKey {
  if (type.source === 'csv' && type.lastImport?.status === 'succeeded') {
    return 'userOrgs.lastRefreshed.clearedBySourceChange';
  }
  if (!type.isEnabled) return 'userOrgs.lastRefreshed.never';
  return type.source === 'entra'
    ? 'userOrgs.lastRefreshed.waitingForImport'
    : 'userOrgs.lastRefreshed.noImportYet';
}
