import { describe, expect, it } from 'vitest';
import { ALL_GRANTED_PORTAL_ACCESS, type PortalAccessValue } from './access';
import { ROUTES, WORKLOADS, groupedRoutesForArea, missingPermission, visibleAreas, visibleRoutesForArea } from './navigation';
import { REPORT_PAGES, reportPageEnabled, reportMonths } from './reportNavigation';
import type { ReportAreas } from './types/reports';

const restricted: PortalAccessValue = {
  ...ALL_GRANTED_PORTAL_ACCESS,
  administration: false,
  seePii: false,
};

describe('portal access navigation helpers', () => {
  it('hides the Administration area without the administration permission', () => {
    expect(visibleAreas(restricted).map((area) => area.id)).toEqual(['insights']);
    expect(visibleRoutesForArea('admin', restricted)).toEqual([]);
  });

  describe('workload navigation', () => {
    const disabled: ReportAreas = {
      copilot: false, copilotInteractionHistory: false, usage: false, officeApps: false,
      spoAudit: false, webTraffic: false, calls: false, emails: false,
    };

    it('registers every catalog report exactly once in the router and retains the catalog address', () => {
      expect(new Set(ROUTES.map(route => route.path)).size).toBe(ROUTES.length);
      for (const page of REPORT_PAGES) {
        expect(ROUTES.find(route => route.path === page.path)?.reportPage).toBe(page);
      }
      expect(ROUTES.find(route => route.path === '/insights/reports')?.reportPage).toBeUndefined();
    });

    it('defines only workload groups backed by existing routes', () => {
      for (const workload of WORKLOADS) {
        expect(ROUTES.filter(route => route.workload === workload.id).length).toBeGreaterThan(1);
      }
      expect(ROUTES.filter(route => route.area === 'admin').every(route => route.workload === undefined)).toBe(true);
    });

    it('makes history-only Prompt insights available without exposing audit usage or agents', () => {
      const areas = { ...disabled, copilotInteractionHistory: true };
      expect(REPORT_PAGES.filter(page => reportPageEnabled(page, areas)).map(page => page.path)).toEqual(['/insights/reports/copilot-prompts']);
    });

    it('maps every import flag to its own report without enabling other workloads', () => {
      expect(REPORT_PAGES.filter(page => reportPageEnabled(page, disabled))).toEqual([]);
      for (const flag of ['usage', 'officeApps', 'spoAudit', 'webTraffic', 'calls', 'emails'] as const) {
        expect(REPORT_PAGES.filter(page => reportPageEnabled(page, { ...disabled, [flag]: true }))).toHaveLength(1);
      }
    });

    it('accepts only the periods the report controls actually support', () => {
      expect(reportMonths('?months=1')).toBe(1);
      expect(reportMonths('?months=3')).toBe(3);
      expect(reportMonths('?months=6')).toBe(6);
      expect(reportMonths('?months=99')).toBe(3);
      expect(reportMonths('')).toBe(3);
    });
  });

  it('requires administration for admin routes and See PII as the additional user-lookup permission', () => {
    const health = ROUTES.find((route) => route.path === '/admin/health')!;
    const lookup = ROUTES.find((route) => route.path === '/admin/user-lookup')!;
    expect(missingPermission(health, restricted)).toBe('administration');
    expect(missingPermission(lookup, { ...restricted, administration: true })).toBe('seePii');
  });

  it('groups administration by task without losing destinations or mixing connections into configuration', () => {
    expect(groupedRoutesForArea('admin').map(group => ({
      key: group.groupKey,
      paths: group.routes.map(route => route.path),
    }))).toEqual([
      { key: 'app.navGroup.service', paths: [
        '/admin/health', '/admin/configuration', '/admin/install-log', '/admin/profiling',
      ] },
      { key: 'app.navGroup.connections', paths: [
        '/admin/teams-permissions', '/admin/agent-cost-connection', '/admin/copilot-cost-connection',
      ] },
      { key: 'app.navGroup.users', paths: [
        '/admin/user-import', '/admin/user-scope', '/admin/user-orgs', '/admin/user-lookup',
      ] },
      { key: 'app.navGroup.reporting', paths: [
        '/admin/global-filter', '/admin/copilot-adoption-settings', '/admin/leadership-cohort',
        '/admin/prompt-categories', '/admin/copilot-audit-backfill', '/admin/agent365-catalog',
      ] },
    ]);
  });

  it('requires Administration, but not See PII, for the dedicated credit-history connection', () => {
    const connection = ROUTES.find(route => route.path === '/admin/copilot-cost-connection')!;
    expect(connection).toBeDefined();
    expect(missingPermission(connection, restricted)).toBe('administration');
    expect(missingPermission(connection, { ...restricted, administration: true })).toBeNull();
    const groups = groupedRoutesForArea('admin', visibleRoutesForArea('admin', { ...restricted, administration: true }));
    expect(groups.find(group => group.groupKey === 'app.navGroup.connections')?.routes).toContain(connection);
    expect(groups.flatMap(group => group.routes).some(route => route.path === '/admin/user-lookup')).toBe(false);
  });

  it('opens Activity analysis to every reader: it trims small groups itself and offers people only with See PII', () => {
    const activity = ROUTES.find((route) => route.path === '/insights/activity-analysis')!;
    expect(activity.labelKey).toBe('app.route.activityAnalysis');
    expect(missingPermission(activity, restricted)).toBeNull();
    expect(visibleRoutesForArea('insights', restricted).map((route) => route.path)).toContain('/insights/activity-analysis');
  });
});
