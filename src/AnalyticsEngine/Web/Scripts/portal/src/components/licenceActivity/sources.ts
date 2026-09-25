import type { TFunction, TranslationKey } from '../../i18n';

// Plain-English rendering of the backend's data-source and sampling vocabulary.
//
// The backend emits STABLE IDENTIFIERS for `LicenceActivityCoverage.source` and `.granularity`
// (LicenceActivitySql.M365ReportSource etc.) because its own SQL matches on them. Those identifiers
// are developer-facing API names - "microsoftGraphUsageReport", "weeklySupportingSnapshot" - and this
// report is read by business leaders and Microsoft 365 administrators, not by people who call Graph.
// So the identifier stays on the wire and is translated here, at the point of display.
//
// Mirrors `statuses.ts`, which does the same for the status vocabulary. Both fall through to the raw
// value for anything unlisted, so a new backend source degrades to showing its id rather than blank.

const SOURCE_LABELS: Record<string, TranslationKey> = {
  microsoftGraphUsageReport: 'licenceActivity.source.microsoftGraphUsageReport',
  microsoftGraphCopilotUsageReport: 'licenceActivity.source.microsoftGraphCopilotUsageReport',
  copilotAudit: 'licenceActivity.source.copilotAudit',
  copilotInteractions: 'licenceActivity.source.copilotInteractions',
};

const GRANULARITY_LABELS: Record<string, TranslationKey> = {
  weeklySupportingSnapshot: 'licenceActivity.granularity.weeklySupportingSnapshot',
  singleRollingWindow: 'licenceActivity.granularity.singleRollingWindow',
  weeklySampleOfRolling7DayReport: 'licenceActivity.granularity.weeklySampleOfRolling7DayReport',
  eventPositiveOnly: 'licenceActivity.granularity.eventPositiveOnly',
  unknown: 'licenceActivity.granularity.unknown',
};

// The coverage `measure` and `message` are English sentences the licence activity SQL writes. The server
// also sends a stable `measureKey` / `messageKey` beside each (LicenceActivityDisplayKeys in C#), and the
// sentence is shown from the catalog by that key; the server's English is only the fallback for a key this
// build does not know. serverAuthoredText.test.ts reads the C# and fails when these maps and the server's
// keys disagree in either direction.
export const MEASURE_LABELS: Record<string, TranslationKey> = {
  'm365.teams': 'licenceActivity.measure.m365.teams',
  'm365.outlook': 'licenceActivity.measure.m365.outlook',
  'm365.files': 'licenceActivity.measure.m365.files',
  'm365.published': 'licenceActivity.measure.m365.published',
  'copilot.microsoftReportPrompts': 'licenceActivity.measure.copilot.microsoftReportPrompts',
  'copilot.singleRollingReport': 'licenceActivity.measure.copilot.singleRollingReport',
  'copilot.recordedActivity': 'licenceActivity.measure.copilot.recordedActivity',
  'copilot.auditActiveWeeks': 'licenceActivity.measure.copilot.auditActiveWeeks',
  'copilot.interactionActiveWeeks': 'licenceActivity.measure.copilot.interactionActiveWeeks',
};

export const MESSAGE_LABELS: Record<string, TranslationKey> = {
  'm365.disabled': 'licenceActivity.coverageMessage.m365.disabled',
  'm365.available': 'licenceActivity.coverageMessage.m365.available',
  'm365.partial': 'licenceActivity.coverageMessage.m365.partial',
  'm365.notImported': 'licenceActivity.coverageMessage.m365.notImported',
  'm365.missingCoverage': 'licenceActivity.coverageMessage.m365.missingCoverage',
  'copilotReport.available': 'licenceActivity.coverageMessage.copilotReport.available',
  'copilotReport.partial': 'licenceActivity.coverageMessage.copilotReport.partial',
  'copilotReport.singleWindowAvailable': 'licenceActivity.coverageMessage.copilotReport.singleWindowAvailable',
  'copilotReport.singleWindowLonger': 'licenceActivity.coverageMessage.copilotReport.singleWindowLonger',
  'copilotReport.unmatchableIdentity': 'licenceActivity.coverageMessage.copilotReport.unmatchableIdentity',
  'copilotReport.notImported': 'licenceActivity.coverageMessage.copilotReport.notImported',
  'copilotReport.failed': 'licenceActivity.coverageMessage.copilotReport.failed',
  'copilotReport.missingCoverage': 'licenceActivity.coverageMessage.copilotReport.missingCoverage',
  'copilotAudit.unmatchableIdentity': 'licenceActivity.coverageMessage.copilotAudit.unmatchableIdentity',
  'copilotAudit.partial': 'licenceActivity.coverageMessage.copilotAudit.partial',
  'copilotAudit.missingCoverage': 'licenceActivity.coverageMessage.copilotAudit.missingCoverage',
  'copilotInteractions.unmatchableIdentity': 'licenceActivity.coverageMessage.copilotInteractions.unmatchableIdentity',
  'copilotInteractions.partial': 'licenceActivity.coverageMessage.copilotInteractions.partial',
  'copilotInteractions.missingCoverage': 'licenceActivity.coverageMessage.copilotInteractions.missingCoverage',
};

/** Where a workload's figures came from, named the way Microsoft 365 admins would recognise it. */
export function sourceLabel(source: string | null | undefined, t: TFunction): string | null {
  if (!source) return null;
  return SOURCE_LABELS[source] ? t(SOURCE_LABELS[source]) : source;
}

/** How often that source was read, in plain English. */
export function granularityLabel(granularity: string | null | undefined, t: TFunction): string | null {
  if (!granularity) return null;
  return GRANULARITY_LABELS[granularity] ? t(GRANULARITY_LABELS[granularity]) : granularity;
}

export function measureLabel(key: string | null | undefined, fallback: string | null | undefined, t: TFunction): string | null {
  if (key && MEASURE_LABELS[key]) return t(MEASURE_LABELS[key]);
  return fallback ?? null;
}

export function coverageMessage(key: string | null | undefined, fallback: string | null | undefined, t: TFunction): string | null {
  if (key && MESSAGE_LABELS[key]) return t(MESSAGE_LABELS[key]);
  return fallback ?? null;
}
