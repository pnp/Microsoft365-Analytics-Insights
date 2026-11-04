import type { TFunction, TranslationKey } from '../../i18n';

/**
 * Words a failed report query in the reader's language.
 *
 * The server never sends the exception's text, which names servers, databases, logins and objects: it sends a stable
 * reason (`ReportQueryFailure.Reason` in Common.Entities - `Timeout`, `Unavailable`, `SchemaMismatch`...) and an error
 * reference an administrator can find in the server log. `fallback` is the server's own English, shown only for a
 * reason this build doesn't know, or a response from a build that didn't send one.
 */
export function reportQueryFailureText(
  t: TFunction,
  reason: string | null | undefined,
  reference: string | null | undefined,
  fallback: string,
): string {
  if (!reason || !reference) return fallback;
  const key = `errors.reportQuery.reason.${reason}` as TranslationKey;
  const reasonText = t(key);
  if (reasonText === key) return fallback;
  return t('errors.reportQuery.failed', { reason: reasonText, reference });
}
