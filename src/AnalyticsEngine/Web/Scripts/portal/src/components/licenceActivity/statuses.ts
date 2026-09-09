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

const STATUS_META: Record<string, StatusMeta> = {
  available: {
    tone: 'success',
    label: 'Available',
    explanation: 'Measured across the whole period.',
  },
  partial: {
    tone: 'warning',
    label: 'Partial',
    explanation: 'Part of this period could not be measured in full, so activity here may be understated.',
  },
  missingCoverage: {
    tone: 'warning',
    label: 'Missing coverage',
    explanation:
      'Part of the chosen period has no measurement behind it, so nobody can be shown as inactive for this service.',
  },
  unmatchableIdentity: {
    tone: 'warning',
    label: 'Identities could not be matched',
    explanation:
      'Microsoft hid the identities in this report, so its activity cannot be tied back to the people holding the licence.',
  },
  notImported: {
    tone: 'subtle',
    label: 'Not imported',
    explanation: 'This service\u2019s usage data has never been collected on this deployment.',
  },
  disabled: {
    tone: 'subtle',
    label: 'Import switched off',
    explanation: 'Collection for this service is switched off in the installer.',
  },
  unknown: {
    tone: 'subtle',
    label: 'Unknown',
    explanation: 'Not measured for this person \u2013 which is not the same as measured as no activity.',
  },
};

/** Tone/label/explanation for a status string, with a neutral fallback for anything unlisted. */
export function statusMeta(status: string | null | undefined): StatusMeta {
  if (!status) return { tone: 'informative', label: 'Unknown', explanation: '' };
  return STATUS_META[status] ?? { tone: 'informative', label: status, explanation: '' };
}
