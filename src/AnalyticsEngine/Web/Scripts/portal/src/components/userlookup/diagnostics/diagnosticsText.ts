import { formatDateParts, type TFunction, type TranslationKey } from '../../../i18n';
import type { ClassificationExplanation, DiagnosticFinding } from '../../../types/userDiagnostics';

/** An instant, always shown in UTC and labelled as such - these are compared with importer logs. */
export function formatUtc(iso: string | null | undefined): string {
  if (!iso) return '\u2014';
  // Explicit fields: Intl rejects dateStyle/timeStyle combined with timeZoneName.
  return formatDateParts(new Date(iso), {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    timeZone: 'UTC',
    timeZoneName: 'short',
  });
}

/** A date-only value (a usage-report day or last-activity date): no time, read in UTC so it never shifts a day. */
export function formatUtcDate(iso: string | null | undefined): string {
  if (!iso) return '\u2014';
  return formatDateParts(new Date(iso), { dateStyle: 'medium', timeZone: 'UTC' });
}

const REASON_KEYS: Record<string, TranslationKey> = {
  seatSkuPrefix: 'userDiagnostics.reason.seatSkuPrefix',
  excludedSkuPrefix: 'userDiagnostics.reason.excludedSkuPrefix',
  seatDisplayName: 'userDiagnostics.reason.seatDisplayName',
  excludedNameWord: 'userDiagnostics.reason.excludedNameWord',
  copilotNameWithoutQualifier: 'userDiagnostics.reason.copilotNameWithoutQualifier',
  noRuleMatched: 'userDiagnostics.reason.noRuleMatched',
  reportOverrideIncluded: 'userDiagnostics.reason.reportOverrideIncluded',
  reportOverrideExcluded: 'userDiagnostics.reason.reportOverrideExcluded',
};

/** Why a licence is or is not a Copilot seat, in the reader's language. The matched rule is product data. */
export function classificationReason(t: TFunction, classification: ClassificationExplanation | null | undefined): string {
  const key = classification ? REASON_KEYS[classification.reason] : undefined;
  if (!key) return t('userDiagnostics.reason.unknown');
  return t(key, { rule: classification?.matchedRule ?? '' });
}

const FINDING_KEYS: Record<string, TranslationKey> = {
  noStoredUser: 'userDiagnostics.finding.noStoredUser',
  noStoredLicences: 'userDiagnostics.finding.noStoredLicences',
  noStoredCopilotSeat: 'userDiagnostics.finding.noStoredCopilotSeat',
  accountDisabled: 'userDiagnostics.finding.accountDisabled',
  scopeExcluded: 'userDiagnostics.finding.scopeExcluded',
  scopeUnknown: 'userDiagnostics.finding.scopeUnknown',
  userImportDisabled: 'userDiagnostics.finding.userImportDisabled',
  userImportNotRecorded: 'userDiagnostics.finding.userImportNotRecorded',
  userImportOverdue: 'userDiagnostics.finding.userImportOverdue',
  licenceHistoryUnavailable: 'userDiagnostics.finding.licenceHistoryUnavailable',
  catalogueMissingSku: 'userDiagnostics.finding.catalogueMissingSku',
};

function stringValue(values: Record<string, unknown> | undefined, name: string): string {
  const value = values?.[name];
  return typeof value === 'string' ? value : '';
}

/**
 * A finding as a sentence. Returns null for a key this build does not know, so a newer server never puts a raw
 * key on screen.
 */
export function findingText(t: TFunction, finding: DiagnosticFinding): string | null {
  if (finding.key === 'possibleCopilotRuleGap') {
    const key: TranslationKey = finding.values?.counted === true
      ? 'userDiagnostics.finding.possibleCopilotRuleGapCounted'
      : 'userDiagnostics.finding.possibleCopilotRuleGapNotCounted';
    return t(key, { name: stringValue(finding.values, 'name'), sku: stringValue(finding.values, 'sku') });
  }

  const key = FINDING_KEYS[finding.key];
  if (!key) return null;
  return t(key, {
    when: formatUtc(stringValue(finding.values, 'lastCompletedUtc') || null),
    sku: stringValue(finding.values, 'sku'),
  });
}
