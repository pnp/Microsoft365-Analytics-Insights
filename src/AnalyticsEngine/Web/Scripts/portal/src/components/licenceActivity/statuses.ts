import type { TFunction, TranslationKey } from '../../i18n';

// Friendly rendering of the backend coverage/evidence status vocabulary
// (LicenceActivityCoverage.Status / LicenceActivityEvidence.Status):
// available | partial | missingCoverage | unmatchableIdentity | notImported | disabled.
// Shared so the coverage panel and the per-user evidence detail explain a status the same way.

export type StatusTone = 'success' | 'warning' | 'informative' | 'subtle';

export interface StatusMeta {
  tone: StatusTone;
  label: string;
  /** Why the data is in this state, so an "Unknown"/partial figure is never shown without a reason. */
  explanation: string;
}

interface StatusMetaDef {
  tone: StatusTone;
  labelKey: TranslationKey;
  explanationKey: TranslationKey;
}

const STATUS_META: Record<string, StatusMetaDef> = {
  available: {
    tone: 'success',
    labelKey: 'licenceActivity.status.available.label',
    explanationKey: 'licenceActivity.status.available.explanation',
  },
  partial: {
    tone: 'warning',
    labelKey: 'licenceActivity.status.partial.label',
    explanationKey: 'licenceActivity.status.partial.explanation',
  },
  missingCoverage: {
    tone: 'warning',
    labelKey: 'licenceActivity.status.missingCoverage.label',
    explanationKey: 'licenceActivity.status.missingCoverage.explanation',
  },
  unmatchableIdentity: {
    tone: 'warning',
    labelKey: 'licenceActivity.status.unmatchableIdentity.label',
    explanationKey: 'licenceActivity.status.unmatchableIdentity.explanation',
  },
  notImported: {
    tone: 'subtle',
    labelKey: 'licenceActivity.status.notImported.label',
    explanationKey: 'licenceActivity.status.notImported.explanation',
  },
  disabled: {
    tone: 'subtle',
    labelKey: 'licenceActivity.status.disabled.label',
    explanationKey: 'licenceActivity.status.disabled.explanation',
  },
  unknown: {
    tone: 'subtle',
    labelKey: 'licenceActivity.status.unknown.label',
    explanationKey: 'licenceActivity.status.unknown.explanation',
  },
};

/** Tone/label/explanation for a status string, with a neutral fallback for anything unlisted. */
export function statusMeta(status: string | null | undefined, t: TFunction): StatusMeta {
  if (!status) return { tone: 'informative', label: t('licenceActivity.common.unknown'), explanation: '' };
  const meta = STATUS_META[status];
  return meta
    ? { tone: meta.tone, label: t(meta.labelKey), explanation: t(meta.explanationKey) }
    : { tone: 'informative', label: status, explanation: '' };
}
