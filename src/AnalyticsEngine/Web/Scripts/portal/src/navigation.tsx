import type { ReactElement } from 'react';
import {
  ChartMultiple20Regular,
  DatabaseSearch20Regular,
  DataTrending20Regular,
  DocumentBulletList20Regular,
  Home20Regular,
  PeopleTeam20Regular,
  Pulse20Regular,
  Settings20Regular,
  Sparkle20Regular,
} from '@fluentui/react-icons';

import { lazyWithReload } from './lazyWithReload';

// Code-split the pages so each route is a separate chunk (smaller initial load). lazyWithReload
// recovers from a stale chunk after a rebuild/redeploy instead of leaving the route blank.
const InsightsOverviewPage = lazyWithReload(() => import('./pages/InsightsOverviewPage'));
const ReportsPage = lazyWithReload(() => import('./pages/ReportsPage'));
const CopilotAdoptionPage = lazyWithReload(() => import('./pages/CopilotAdoptionPage'));
const TeamsPermissionsPage = lazyWithReload(() => import('./pages/TeamsPermissionsPage'));
const UserLookupPage = lazyWithReload(() => import('./pages/UserLookupPage'));
const ProfilingStatusPage = lazyWithReload(() => import('./pages/ProfilingStatusPage'));
const InstallLogPage = lazyWithReload(() => import('./pages/InstallLogPage'));
const HealthPage = lazyWithReload(() => import('./pages/HealthPage'));
const ServiceConfigurationPage = lazyWithReload(() => import('./pages/ServiceConfigurationPage'));

/**
 * The portal is split into two areas so the two audiences it serves don't have to wade
 * through each other's tooling:
 *
 * - **Insights** - what the data says. Reports and Copilot adoption, for a business reader.
 * - **Administration** - running the service. Health, logs, permissions, for an IT operator.
 */
export type AreaId = 'insights' | 'admin';

export interface AreaDefinition {
  id: AreaId;
  label: string;
  /** Path prefix owned by the area, e.g. '/insights'. */
  basePath: string;
  /** Where the area switcher lands when this area is selected. */
  homePath: string;
}

export const AREAS: AreaDefinition[] = [
  { id: 'insights', label: 'Insights', basePath: '/insights', homePath: '/insights/overview' },
  { id: 'admin', label: 'Administration', basePath: '/admin', homePath: '/admin/health' },
];

/** The area the portal opens on. */
export const DEFAULT_PATH = '/insights/overview';

export interface PortalRoute {
  area: AreaId;
  /** Absolute route path, also used as the nav item's value. */
  path: string;
  /** Left-nav label. */
  label: string;
  /** Optional section heading grouping items within an area's nav. */
  group?: string;
  icon: ReactElement;
  element: ReactElement;
}

/**
 * Single source of truth for routing *and* navigation - the router and the left nav are both
 * rendered from this list, so adding a page is a one-line change and the two can't drift.
 */
export const ROUTES: PortalRoute[] = [
  {
    area: 'insights',
    path: '/insights/overview',
    label: 'Overview',
    icon: <Home20Regular />,
    element: <InsightsOverviewPage />,
  },
  {
    area: 'insights',
    path: '/insights/reports',
    label: 'Reports',
    icon: <ChartMultiple20Regular />,
    element: <ReportsPage />,
  },
  {
    area: 'insights',
    path: '/insights/copilot-adoption',
    label: 'Copilot Adoption',
    icon: <Sparkle20Regular />,
    element: <CopilotAdoptionPage />,
  },

  {
    area: 'admin',
    path: '/admin/health',
    label: 'Service health',
    group: 'Monitoring',
    icon: <Pulse20Regular />,
    element: <HealthPage />,
  },
  {
    area: 'admin',
    path: '/admin/install-log',
    label: 'Install log',
    group: 'Monitoring',
    icon: <DocumentBulletList20Regular />,
    element: <InstallLogPage />,
  },
  {
    area: 'admin',
    path: '/admin/profiling',
    label: 'Profiling',
    group: 'Monitoring',
    icon: <DataTrending20Regular />,
    element: <ProfilingStatusPage />,
  },
  {
    area: 'admin',
    path: '/admin/teams-permissions',
    label: 'Teams permissions',
    group: 'Manage',
    icon: <PeopleTeam20Regular />,
    element: <TeamsPermissionsPage />,
  },
  {
    area: 'admin',
    path: '/admin/user-lookup',
    label: 'User data lookup',
    group: 'Manage',
    icon: <DatabaseSearch20Regular />,
    element: <UserLookupPage />,
  },
  {
    area: 'admin',
    path: '/admin/configuration',
    label: 'Service configuration',
    group: 'Manage',
    icon: <Settings20Regular />,
    element: <ServiceConfigurationPage />,
  },
];

/** The area that owns a pathname, defaulting to the landing area for anything unrecognised. */
export function areaForPath(pathname: string): AreaId {
  return AREAS.find((a) => pathname.startsWith(a.basePath))?.id ?? AREAS[0].id;
}

/** Routes belonging to an area, in nav order. */
export function routesForArea(area: AreaId): PortalRoute[] {
  return ROUTES.filter((r) => r.area === area);
}

/**
 * An area's routes bucketed by their `group` heading, preserving declaration order and keeping
 * ungrouped items (group === undefined) in a single leading bucket.
 */
export function groupedRoutesForArea(area: AreaId): { group?: string; routes: PortalRoute[] }[] {
  const buckets: { group?: string; routes: PortalRoute[] }[] = [];
  for (const route of routesForArea(area)) {
    const last = buckets[buckets.length - 1];
    if (last && last.group === route.group) last.routes.push(route);
    else buckets.push({ group: route.group, routes: [route] });
  }
  return buckets;
}
