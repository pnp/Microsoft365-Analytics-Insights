import { translateActive } from '../i18n/runtime';
import { apiFetch } from './http';
import type { ReportAreaData, ReportAreaKey, ReportAreas } from '../types/reports';
import type { TranslationKey } from '../i18n/catalog';

const baseUrl = (): string =>
  window.o365AnalyticsReportsAPI ?? `${window.location.origin}/api/Reports`;

const REPORT_AREA_FAILURE_KEYS: Record<ReportAreaKey, TranslationKey> = {
  copilot: 'errors.reports.copilotReportFailed',
  'copilot-agents': 'errors.reports.copilotAgentsReportFailed',
  usage: 'errors.reports.usageReportFailed',
  'office-apps': 'errors.reports.officeAppsReportFailed',
  'spo-audit': 'errors.reports.spoAuditReportFailed',
  'web-traffic': 'errors.reports.webTrafficReportFailed',
  calls: 'errors.reports.callsReportFailed',
  emails: 'errors.reports.emailsReportFailed',
};

/** Which report areas are enabled for this deployment (drives the visible sub-tabs). */
export async function fetchReportAreas(): Promise<ReportAreas> {
  const response = await apiFetch(`${baseUrl()}/areas`, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(translateActive('errors.reports.areasFailed', { status: response.status }));
  }

  return response.json() as Promise<ReportAreas>;
}

export type ReportAreaOptions = {
  topAgents?: number;
  agentName?: string;
};

/** Fetch the weekly charts for one report area over the given window (months). */
export async function fetchReportArea(
  area: ReportAreaKey,
  months: number,
  options?: ReportAreaOptions,
): Promise<ReportAreaData> {
  const params = new URLSearchParams({ months: String(months) });
  if (options?.topAgents !== undefined) params.set('top', String(options.topAgents));
  if (options?.agentName) params.set('agentName', options.agentName);

  const url = `${baseUrl()}/${area}?${params.toString()}`;
  const response = await apiFetch(url, {
    method: 'GET',
    headers: { Accept: 'application/json' },
  });

  if (!response.ok) {
    throw new Error(translateActive(REPORT_AREA_FAILURE_KEYS[area], { status: response.status }));
  }

  return response.json() as Promise<ReportAreaData>;
}
