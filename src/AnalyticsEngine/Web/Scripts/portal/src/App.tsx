import { Suspense, useCallback, useState } from 'react';
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import {
  makeStyles,
  tokens,
  Tab,
  TabList,
  Text,
  Button,
  Hamburger,
  NavDrawer,
  NavDrawerBody,
  NavItem,
  NavSectionHeader,
  Tooltip,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { SignOut20Regular } from '@fluentui/react-icons';
import { AppToaster } from './components/toast';
import Spinner from './components/Spinner';
import { AREAS, DEFAULT_PATH, ROUTES, areaForPath, groupedRoutesForArea } from './navigation';
import { PRODUCT_NAME, REPOSITORY_URL, buildLabel } from './product';
import { LanguageSwitcher, useT } from './i18n';

const useStyles = makeStyles({
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundOnBrand,
    paddingInline: '20px',
    height: '48px',
  },
  brand: {
    color: tokens.colorNeutralForegroundOnBrand,
    fontWeight: tokens.fontWeightSemibold,
  },
  headerActions: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
  },
  signOut: {
    color: tokens.colorNeutralForegroundOnBrand,
  },
  areaBar: {
    display: 'flex',
    alignItems: 'center',
    gap: '4px',
    backgroundColor: tokens.colorNeutralBackground1,
    paddingInline: '12px',
    boxShadow: tokens.shadow4,
  },
  layout: {
    display: 'flex',
    alignItems: 'stretch',
    // Fill the viewport below the 48px header and the ~44px area bar so the nav rail runs the
    // full height of the page rather than only as far as the content happens to reach.
    minHeight: 'calc(100vh - 92px)',
  },
  nav: {
    height: 'auto',
  },
  content: {
    // min-width: 0 stops a wide page (tables, charts) forcing the flex row wider than the
    // viewport and pushing the nav rail off-screen.
    flexGrow: 1,
    minWidth: 0,
    padding: '24px',
  },
  contentInner: {
    maxWidth: '1120px',
    marginInline: 'auto',
  },
  // Paper only - see the `data-print` contract in index.css, which repeats this on every printed
  // page. A printed report gets forwarded and re-read months later, so it has to name the product
  // that produced it, where that product lives, and the build the figures came out of.
  printFooter: {
    display: 'none',
  },
});

/**
 * The route the browser's history is on *right now*.
 *
 * `useLocation` reports the last committed render, which lags a navigation that has been pushed
 * but not yet rendered - pages load lazily, so that gap is real. It therefore cannot answer "are
 * we already going there?" for a second click, and using it leads to both duplicate history
 * entries and silently dropped clicks. pushState/replaceState update the URL synchronously, so
 * under HashRouter the hash always can.
 *
 * Parsed the way HashRouter parses it, so a hand-typed `#insights/overview` reads the same as the
 * canonical `#/insights/overview`. Falls back to the committed path only when there is no fragment
 * at all - a host that is not hash-routed, where the committed path is the best available answer.
 */
function currentRoutePath(committedPath: string): string {
  const hash = typeof window === 'undefined' ? '' : window.location.hash;
  if (!hash.startsWith('#')) {
    if (import.meta.env.DEV && typeof window !== 'undefined') {
      // Not hash-routed: the committed path is all there is, which is the lagging value this
      // function exists to avoid. Nav would go back to duplicating history entries and dropping
      // clicks during an in-flight navigation, silently - so say so rather than degrade quietly.
      console.warn('[portal] No hash route found - App expects HashRouter. Nav de-duplication is degraded.');
    }
    return committedPath;
  }

  const path = hash.slice(1).split(/[?#]/)[0];
  if (!path) return '/';
  return path.startsWith('/') ? path : `/${path}`;
}

/**
 * App shell: an Office 365-style brand header, an area switcher (Insights / Administration) and a
 * per-area left nav. Uses HashRouter so the whole SPA is served by a single MVC action (no IIS /
 * MVC route changes to add pages).
 *
 * Routing and navigation are both driven from the route table in ./navigation, so the two cannot
 * drift and adding a page means adding one entry there.
 *
 * The shell is marked up for print with `data-print` attributes (see the `@media print` block in
 * index.css): the brand bar, area switcher and nav rail are dropped, and the layout wrappers are
 * flattened so the page itself gets the whole sheet instead of a 1120px column beside a menu.
 */
export default function App() {
  const styles = useStyles();
  const location = useLocation();
  const navigate = useNavigate();
  const t = useT();
  const [navOpen, setNavOpen] = useState(true);
  const build = buildLabel();

  const currentArea = areaForPath(location.pathname);
  const navGroups = groupedRoutesForArea(currentArea);

  const goTo = useCallback(
    (target: string) => {
      // Navigating to the entry history is already on would push an identical entry, so the
      // browser's Back button would look like it is doing nothing. Anything else navigates: a
      // click must never be silently dropped, however fast it follows the previous one.
      if (target === currentRoutePath(location.pathname)) return;
      navigate(target);
    },
    [navigate, location.pathname],
  );

  const onAreaSelect: SelectTabEventHandler = (_event: unknown, data: { value: unknown }) => {
    const area = AREAS.find((a) => a.id === data.value);
    // TabList fires for the already-selected tab too, and re-navigating would bounce the user off
    // the page they are reading back to the area's home page. Compare against where history
    // actually is, so a tab click during an in-flight navigation is not discarded.
    const liveArea = areaForPath(currentRoutePath(location.pathname));
    if (area && area.id !== liveArea) goTo(area.homePath);
  };

  return (
    <>
      <AppToaster />
      <header className={styles.header} data-print="hide">
        <Text size={400} className={styles.brand}>
          {/* The product's own name. Microsoft does not translate it, and neither do we: a
              customer searching for it, or matching it against their licence, needs the same
              string in every language. */}
          Microsoft 365 Advanced Analytics
        </Text>
        <div className={styles.headerActions}>
          <LanguageSwitcher />
          <Button
            appearance="transparent"
            className={styles.signOut}
            icon={<SignOut20Regular />}
            onClick={() => {
              // Server-side OIDC sign-out (full page navigation, not a SPA route).
              window.location.href = '/Account/SignOut';
            }}
          >
            {t('app.signOut')}
          </Button>
        </div>
      </header>

      <div className={styles.areaBar} data-print="hide">
        <Tooltip
          content={navOpen ? t('app.nav.collapse') : t('app.nav.expand')}
          relationship="label"
        >
          <Hamburger onClick={() => setNavOpen(!navOpen)} />
        </Tooltip>
        <TabList selectedValue={currentArea} onTabSelect={onAreaSelect} size="large">
          {AREAS.map((area) => (
            <Tab key={area.id} value={area.id}>
              {t(area.labelKey)}
            </Tab>
          ))}
        </TabList>
      </div>

      <div className={styles.layout} data-print="content">
        <NavDrawer
          open={navOpen}
          type="inline"
          className={styles.nav}
          data-print="hide"
          selectedValue={location.pathname}
          onNavItemSelect={(_event: unknown, data: { value: unknown }) => goTo(String(data.value))}
          aria-label={t('app.nav.ariaLabel', {
            area: t(AREAS.find((a) => a.id === currentArea)?.labelKey ?? AREAS[0].labelKey),
          })}
        >
          <NavDrawerBody>
            {navGroups.map((bucket, i) => (
              <div key={bucket.groupKey ?? `ungrouped-${i}`}>
                {bucket.groupKey && <NavSectionHeader>{t(bucket.groupKey)}</NavSectionHeader>}
                {bucket.routes.map((route) => (
                  <NavItem key={route.path} value={route.path} icon={route.icon}>
                    {t(route.labelKey)}
                  </NavItem>
                ))}
              </div>
            ))}
          </NavDrawerBody>
        </NavDrawer>

        <main className={styles.content} data-print="content">
          <div className={styles.contentInner} data-print="content">
            <Suspense
              fallback={
                <div style={{ textAlign: 'center', padding: '32px' }}>
                  <Spinner size={80} label={t('common.state.loading')} />
                </div>
              }
            >
              <Routes>
                <Route path="/" element={<Navigate to={DEFAULT_PATH} replace />} />
                {ROUTES.map((route) => (
                  <Route key={route.path} path={route.path} element={route.element} />
                ))}
                <Route path="*" element={<Navigate to={DEFAULT_PATH} replace />} />
              </Routes>
            </Suspense>
          </div>
        </main>
      </div>

      {/* Repeated at the foot of every printed page - see the `data-print` contract in index.css.
          The repository URL is a real link so it stays clickable in a PDF, and is shown in full
          rather than as link text, because on paper the href is not recoverable. */}
      <div className={styles.printFooter} data-print="footer">
        {PRODUCT_NAME}
        {build && ` \u00b7 ${build}`}
        {' \u00b7 '}
        <a href={REPOSITORY_URL}>{REPOSITORY_URL}</a>
      </div>
    </>
  );
}
