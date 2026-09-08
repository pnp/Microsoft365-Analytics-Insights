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

const SOURCE_LABELS: Record<string, string> = {
  microsoftGraphUsageReport: 'Microsoft 365 usage reports',
  microsoftGraphCopilotUsageReport: 'Microsoft 365 Copilot usage report',
  copilotAudit: 'Copilot audit log',
  copilotInteractions: 'Copilot chat history',
};

const GRANULARITY_LABELS: Record<string, string> = {
  weeklySupportingSnapshot: 'one reading per week',
  singleRollingWindow: 'a single rolling report',
  weeklySampleOfRolling7DayReport: 'one 7-day report read per week',
  eventPositiveOnly: 'recorded activity only',
  unknown: 'not applicable',
};

/** Where a workload's figures came from, named the way Microsoft 365 admins would recognise it. */
export function sourceLabel(source: string | null | undefined): string | null {
  if (!source) return null;
  return SOURCE_LABELS[source] ?? source;
}

/** How often that source was read, in plain English. */
export function granularityLabel(granularity: string | null | undefined): string | null {
  if (!granularity) return null;
  return GRANULARITY_LABELS[granularity] ?? granularity;
}
