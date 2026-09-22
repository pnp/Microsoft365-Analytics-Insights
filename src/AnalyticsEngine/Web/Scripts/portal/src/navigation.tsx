import type { ReactElement } from 'react';
import {
  ChartMultiple20Regular,
  DatabaseSearch20Regular,
  DataTrending20Regular,
  DataUsage20Regular,
  DocumentBulletList20Regular,
  Globe20Regular,
  Home20Regular,
  Money20Regular,
  Organization20Regular,
  PeopleCommunity20Regular,
  PeopleTeam20Regular,
  Pulse20Regular,
  Settings20Regular,
  ShieldProhibited20Regular,
  Sparkle20Regular,
} from '@fluentui/react-icons';

import { lazyWithReload } from './lazyWithReload';
import type { TranslationKey } from './i18n';

// Code-split the pages so each route is a separate chunk (smaller initial load). lazyWithReload
// recovers from a stale chunk after a rebuild/redeploy instead of leaving the route blank.
const InsightsOverviewPage = lazyWithReload(() => import('./pages/InsightsOverviewPage'));
const ReportsPage = lazyWithReload(() => import('./pages/ReportsPage'));
const CopilotAdoptionPage = lazyWithReload(() => import('./pages/CopilotAdoptionPage'));
const TeamsExplorerPage = lazyWithReload(() => import('./pages/TeamsExplorerPage'));
const WebActivityPage = lazyWithReload(() => import('./pages/WebActivityPage'));
const AgentCostsPage = lazyWithReload(() => import('./pages/AgentCostsPage'));
const LicenceActivityPage = lazyWithReload(() => import('./pages/LicenceActivityPage'));
const DlpPage = lazyWithReload(() => import('./pages/DlpPage'));
const TeamsPermissionsPage = lazyWithReload(() => import('./pages/TeamsPermissionsPage'));
const UserLookupPage = lazyWithReload(() => import('./pages/UserLookupPage'));
const UserOrgsPage = lazyWithReload(() => import('./pages/UserOrgsPage'));
const ProfilingStatusPage = lazyWithReload(() => import('./pages/ProfilingStatusPage'));
const InstallLogPage = lazyWithReload(() => import('./pages/InstallLogPage'));
const HealthPage = lazyWithReload(() => import('./pages/HealthPage'));
const ServiceConfigurationPage = lazyWithReload(() => import('./pages/ServiceConfigurationPage'));

/**
 * The portal is split into two areas so the two audiences it serves don't have to wade
 * through each other's tooling:
 *
 * - **Insights** - what the data says. Reports, Copilot adoption and licence activity.
 * - **Administration** - running the service. Health, logs, permissions, for an IT operator.
 */
export type AreaId = 'insights' | 'admin';

export interface AreaDefinition {
  id: AreaId;
  /** Catalog key for the area's name. Resolved at render time - see the note on ROUTES. */
  labelKey: TranslationKey;
  /** Path prefix owned by the area, e.g. '/insights'. */
  basePath: string;
  /** Where the area switcher lands when this area is selected. */
  homePath: string;
}

export const AREAS: AreaDefinition[] = [
  { id: 'insights', labelKey: 'app.area.insights', basePath: '/insights', homePath: '/insights/overview' },
  { id: 'admin', labelKey: 'app.area.admin', basePath: '/admin', homePath: '/admin/health' },
];

/** The area the portal opens on. */
export const DEFAULT_PATH = '/insights/overview';

export interface PortalRoute {
  area: AreaId;
  /** Absolute route path, also used as the nav item's value. */
  path: string;
  /** Catalog key for the left-nav label. */
  labelKey: TranslationKey;
  /** Catalog key for an optional section heading grouping items within an area's nav. */
  groupKey?: TranslationKey;
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
    labelKey: 'app.route.overview',
    icon: <Home20Regular />,
    element: <InsightsOverviewPage />,
  },
  {
    area: 'insights',
    path: '/insights/reports',
    labelKey: 'app.route.reports',
    icon: <ChartMultiple20Regular />,
    element: <ReportsPage />,
  },
  {
    area: 'insights',
    path: '/insights/teams',
    labelKey: 'app.route.teamsExplorer',
    // Deliberately NOT PeopleTeam20Regular: that icon already marks the admin "Teams permissions"
    // page, and two different pages sharing an icon in the same nav is a reliable way to send
    // someone to the wrong one.
    icon: <PeopleCommunity20Regular />,
    element: <TeamsExplorerPage />,
  },
  {
    area: 'insights',
    path: '/insights/web-activity',
    labelKey: 'app.route.webActivity',
    // A globe rather than a chart: this page is about the intranet as a web site - who browses it
    // and from where - and ChartMultiple already marks the generic "Reports" page next to it.
    icon: <Globe20Regular />,
    element: <WebActivityPage />,
  },
  {
    area: 'insights',
    path: '/insights/copilot-adoption',
    labelKey: 'app.route.copilotAdoption',
    icon: <Sparkle20Regular />,
    element: <CopilotAdoptionPage />,
  },
  {
    area: 'insights',
    path: '/insights/licence-activity',
    labelKey: 'app.route.licenceActivity',
    icon: <DataUsage20Regular />,
    element: <LicenceActivityPage />,
  },
  {
    area: 'insights',
    path: '/insights/agent-costs',
    labelKey: 'app.route.agentCosts',
    icon: <Money20Regular />,
    element: <AgentCostsPage />,
  },
  {
    area: 'insights',
    path: '/insights/dlp',
    labelKey: 'app.route.dlp',
    icon: <ShieldProhibited20Regular />,
    element: <DlpPage />,
  },

  {
    area: 'admin',
    path: '/admin/health',
    labelKey: 'app.route.health',
    groupKey: 'app.navGroup.monitoring',
    icon: <Pulse20Regular />,
    element: <HealthPage />,
  },
  {
    area: 'admin',
    path: '/admin/install-log',
    labelKey: 'app.route.installLog',
    groupKey: 'app.navGroup.monitoring',
    icon: <DocumentBulletList20Regular />,
    element: <InstallLogPage />,
  },
  {
    area: 'admin',
    path: '/admin/profiling',
    labelKey: 'app.route.profiling',
    groupKey: 'app.navGroup.monitoring',
    icon: <DataTrending20Regular />,
    element: <ProfilingStatusPage />,
  },
  {
    area: 'admin',
    path: '/admin/teams-permissions',
    labelKey: 'app.route.teamsPermissions',
    groupKey: 'app.navGroup.manage',
    icon: <PeopleTeam20Regular />,
    element: <TeamsPermissionsPage />,
  },
  {
    area: 'admin',
    path: '/admin/user-lookup',
    labelKey: 'app.route.userLookup',
    groupKey: 'app.navGroup.manage',
    icon: <DatabaseSearch20Regular />,
    element: <UserLookupPage />,
  },
  {
    area: 'admin',
    path: '/admin/user-orgs',
    labelKey: 'app.route.userOrgs',
    groupKey: 'app.navGroup.manage',
    icon: <Organization20Regular />,
    element: <UserOrgsPage />,
  },
  {
    area: 'admin',
    path: '/admin/configuration',
    labelKey: 'app.route.configuration',
    groupKey: 'app.navGroup.manage',
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
 * An area's routes bucketed by their `groupKey` heading, preserving declaration order and keeping
 * ungrouped items (groupKey === undefined) in a single leading bucket.
 */
export function groupedRoutesForArea(area: AreaId): { groupKey?: TranslationKey; routes: PortalRoute[] }[] {
  const buckets: { groupKey?: TranslationKey; routes: PortalRoute[] }[] = [];
  for (const route of routesForArea(area)) {
    const last = buckets[buckets.length - 1];
    if (last && last.groupKey === route.groupKey) last.routes.push(route);
    else buckets.push({ groupKey: route.groupKey, routes: [route] });
  }
  return buckets;
}
