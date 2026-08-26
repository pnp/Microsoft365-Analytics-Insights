import { Suspense, useState } from 'react';
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
});

/**
 * App shell: an Office 365-style brand header, an area switcher (Insights / Administration) and a
 * per-area left nav. Uses HashRouter so the whole SPA is served by a single MVC action (no IIS /
 * MVC route changes to add pages).
 *
 * Routing and navigation are both driven from the route table in ./navigation, so the two cannot
 * drift and adding a page means adding one entry there.
 */
export default function App() {
  const styles = useStyles();
  const location = useLocation();
  const navigate = useNavigate();
  const [navOpen, setNavOpen] = useState(true);

  const currentArea = areaForPath(location.pathname);
  const navGroups = groupedRoutesForArea(currentArea);

  const onAreaSelect: SelectTabEventHandler = (_event, data) => {
    const area = AREAS.find((a) => a.id === data.value);
    if (area) navigate(area.homePath);
  };

  return (
    <>
      <AppToaster />
      <header className={styles.header}>
        <Text size={400} className={styles.brand}>
          Microsoft 365 Advanced Analytics
        </Text>
        <Button
          appearance="transparent"
          className={styles.signOut}
          icon={<SignOut20Regular />}
          onClick={() => {
            // Server-side OIDC sign-out (full page navigation, not a SPA route).
            window.location.href = '/Account/SignOut';
          }}
        >
          Sign out
        </Button>
      </header>

      <div className={styles.areaBar}>
        <Tooltip content={navOpen ? 'Collapse navigation' : 'Expand navigation'} relationship="label">
          <Hamburger onClick={() => setNavOpen(!navOpen)} />
        </Tooltip>
        <TabList selectedValue={currentArea} onTabSelect={onAreaSelect} size="large">
          {AREAS.map((area) => (
            <Tab key={area.id} value={area.id}>
              {area.label}
            </Tab>
          ))}
        </TabList>
      </div>

      <div className={styles.layout}>
        <NavDrawer
          open={navOpen}
          type="inline"
          className={styles.nav}
          selectedValue={location.pathname}
          onNavItemSelect={(_event, data) => navigate(String(data.value))}
          aria-label={`${AREAS.find((a) => a.id === currentArea)?.label} navigation`}
        >
          <NavDrawerBody>
            {navGroups.map((bucket, i) => (
              <div key={bucket.group ?? `ungrouped-${i}`}>
                {bucket.group && <NavSectionHeader>{bucket.group}</NavSectionHeader>}
                {bucket.routes.map((route) => (
                  <NavItem key={route.path} value={route.path} icon={route.icon}>
                    {route.label}
                  </NavItem>
                ))}
              </div>
            ))}
          </NavDrawerBody>
        </NavDrawer>

        <main className={styles.content}>
          <div className={styles.contentInner}>
            <Suspense
              fallback={
                <div style={{ textAlign: 'center', padding: '32px' }}>
                  <Spinner size={80} label="Loading..." />
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
    </>
  );
}
