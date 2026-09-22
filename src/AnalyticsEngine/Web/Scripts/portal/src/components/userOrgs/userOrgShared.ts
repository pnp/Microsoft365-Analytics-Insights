import type { TranslationKey } from '../../i18n';
import type { UserOrgImportStatus } from '../../types/userOrgs';

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
