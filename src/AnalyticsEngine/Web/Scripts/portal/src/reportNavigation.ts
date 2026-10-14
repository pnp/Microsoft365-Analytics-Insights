import type { TranslationKey } from './i18n';
import type { ReportAreaKey, ReportAreas } from './types/reports';

export type WorkloadId = 'copilot' | 'agents' | 'apps' | 'sharepoint' | 'teams';

export interface ReportPageDefinition {
  path: string;
  area: ReportAreaKey;
  labelKey: TranslationKey;
  titleKey: TranslationKey;
  blurbKey: TranslationKey;
  workload?: WorkloadId;
  prompts?: boolean;
}

export const REPORT_PAGES: ReportPageDefinition[] = [
  { path: '/insights/reports/copilot', area: 'copilot', workload: 'copilot', labelKey: 'reports.navigation.usage', titleKey: 'reports.page.copilot', blurbKey: 'reports.area.copilot.blurb' },
  { path: '/insights/reports/copilot-prompts', area: 'copilot', workload: 'copilot', prompts: true, labelKey: 'reports.page.prompts', titleKey: 'reports.page.prompts', blurbKey: 'reports.page.promptsBlurb' },
  { path: '/insights/reports/copilot-agents', area: 'copilot-agents', workload: 'agents', labelKey: 'reports.navigation.usage', titleKey: 'reports.area.copilotAgents.label', blurbKey: 'reports.area.copilotAgents.blurb' },
  { path: '/insights/reports/usage', area: 'usage', workload: 'apps', labelKey: 'reports.area.usage.label', titleKey: 'reports.area.usage.label', blurbKey: 'reports.area.usage.blurb' },
  { path: '/insights/reports/office-apps', area: 'office-apps', workload: 'apps', labelKey: 'reports.area.officeApps.label', titleKey: 'reports.area.officeApps.label', blurbKey: 'reports.area.officeApps.blurb' },
  { path: '/insights/reports/spo-audit', area: 'spo-audit', workload: 'sharepoint', labelKey: 'reports.page.files', titleKey: 'reports.page.files', blurbKey: 'reports.area.spoAudit.blurb' },
  { path: '/insights/reports/web-traffic', area: 'web-traffic', workload: 'sharepoint', labelKey: 'reports.area.webTraffic.label', titleKey: 'reports.area.webTraffic.label', blurbKey: 'reports.area.webTraffic.blurb' },
  { path: '/insights/reports/calls', area: 'calls', workload: 'teams', labelKey: 'reports.area.calls.label', titleKey: 'reports.area.calls.label', blurbKey: 'reports.area.calls.blurb' },
  { path: '/insights/reports/emails', area: 'emails', labelKey: 'reports.area.emails.label', titleKey: 'reports.area.emails.label', blurbKey: 'reports.area.emails.blurb' },
];

export function reportPageForPath(path: string): ReportPageDefinition | undefined {
  return REPORT_PAGES.find(page => page.path === path);
}

export function reportPageEnabled(page: ReportPageDefinition, areas: ReportAreas): boolean {
  if (page.prompts) return areas.copilot || areas.copilotInteractionHistory;
  switch (page.area) {
    case 'copilot':
    case 'copilot-agents': return areas.copilot;
    case 'office-apps': return areas.officeApps;
    case 'spo-audit': return areas.spoAudit;
    case 'web-traffic': return areas.webTraffic;
    default: return areas[page.area];
  }
}

export function reportImportLabelKey(page: ReportPageDefinition): TranslationKey {
  switch (page.area) {
    case 'copilot':
    case 'copilot-agents': return 'reports.import.copilotAudit';
    case 'usage':
    case 'office-apps': return 'reports.area.usage.label';
    case 'spo-audit': return 'reports.import.spoAudit';
    case 'web-traffic': return 'reports.area.webTraffic.label';
    case 'calls': return 'reports.area.calls.label';
    case 'emails': return 'reports.area.emails.label';
  }
}

export function reportMonths(search: string): number {
  const value = new URLSearchParams(search).get('months');
  return value === '1' ? 1 : value === '6' ? 6 : 3;
}

export function reportPath(path: string, months: number): string {
  return `${path}?months=${months}`;
}

export const PROMPT_CHART_KEYS = new Set(['copilot-key-phrases', 'copilot-sentiment', 'copilot-languages']);
