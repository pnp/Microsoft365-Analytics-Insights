import type { TranslationKey } from '../../i18n';
import type { UserOrgImportStatus, UserOrgType } from '../../types/userOrgs';

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
