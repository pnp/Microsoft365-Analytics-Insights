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
    explanation: 'Measured with complete coverage of the period.',
  },
  partial: {
    tone: 'warning',
    label: 'Partial',
    explanation: 'Some reporting samples were missing, so this is a partial view and activity may be understated.',
  },
  missingCoverage: {
    tone: 'warning',
    label: 'Missing coverage',
    explanation: 'The period was not fully covered by snapshots, so users cannot be ranked as inactive here.',
  },
  unmatchableIdentity: {
    tone: 'warning',
    label: 'Unmatchable identity',
    explanation: 'The user could not be matched to this workload\u2019s identity, so their activity is unknown.',
  },
  notImported: {
    tone: 'subtle',
    label: 'Not imported',
    explanation: 'This workload\u2019s usage source is not imported on this deployment.',
  },
  disabled: {
    tone: 'subtle',
    label: 'Import disabled',
    explanation: 'This workload\u2019s import is switched off.',
  },
  unknown: {
    tone: 'subtle',
    label: 'Unknown',
    explanation: 'Not measured for this user - this is not the same as measured zero activity.',
  },
};

/** Tone/label/explanation for a status string, with a neutral fallback for anything unlisted. */
export function statusMeta(status: string | null | undefined): StatusMeta {
  if (!status) return { tone: 'informative', label: 'Unknown', explanation: '' };
  return STATUS_META[status] ?? { tone: 'informative', label: status, explanation: '' };
}
