import { describe, expect, it } from 'vitest';
import { ALL_GRANTED_PORTAL_ACCESS, type PortalAccessValue } from './access';
import { ROUTES, missingPermission, visibleAreas, visibleRoutesForArea } from './navigation';

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

  it('requires administration for admin routes and See PII as the additional user-lookup permission', () => {
    const health = ROUTES.find((route) => route.path === '/admin/health')!;
    const lookup = ROUTES.find((route) => route.path === '/admin/user-lookup')!;
    expect(missingPermission(health, restricted)).toBe('administration');
    expect(missingPermission(lookup, { ...restricted, administration: true })).toBe('seePii');
  });

  it('opens Activity analysis to every reader: it trims small groups itself and offers people only with See PII', () => {
    const activity = ROUTES.find((route) => route.path === '/insights/activity-analysis')!;
    expect(activity.labelKey).toBe('app.route.activityAnalysis');
    expect(missingPermission(activity, restricted)).toBeNull();
    expect(visibleRoutesForArea('insights', restricted).map((route) => route.path)).toContain('/insights/activity-analysis');
  });
});
