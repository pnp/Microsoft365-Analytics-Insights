import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import {
  makeStyles,
  tokens,
  Tab,
  TabList,
  Text,
  Button,
  type SelectTabEventHandler,
} from '@fluentui/react-components';
import { SignOut20Regular } from '@fluentui/react-icons';
import { AppToaster } from './components/toast';
import Spinner from './components/Spinner';

// Code-split the pages so each route is a separate chunk (smaller initial load).
const HomePage = lazy(() => import('./pages/HomePage'));
const ReportsPage = lazy(() => import('./pages/ReportsPage'));
const CopilotAdoptionPage = lazy(() => import('./pages/CopilotAdoptionPage'));
const TeamsPermissionsPage = lazy(() => import('./pages/TeamsPermissionsPage'));
const UserLookupPage = lazy(() => import('./pages/UserLookupPage'));
const ProfilingStatusPage = lazy(() => import('./pages/ProfilingStatusPage'));
const InstallLogPage = lazy(() => import('./pages/InstallLogPage'));
const HealthPage = lazy(() => import('./pages/HealthPage'));

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
  tabBar: {
    backgroundColor: tokens.colorNeutralBackground1,
    paddingInline: '12px',
    boxShadow: tokens.shadow4,
  },
  content: {
    padding: '24px',
    maxWidth: '1120px',
    marginInline: 'auto',
  },
});

/**
 * App shell: an Office 365-style brand header + a Fluent TabList for navigation. Uses HashRouter
 * so the whole SPA is served by a single MVC action (no IIS / MVC route changes to add pages).
 */
export default function App() {
  const styles = useStyles();
  const location = useLocation();
  const navigate = useNavigate();

  const selectedTab = location.pathname.startsWith('/reports')
    ? 'reports'
    : location.pathname.startsWith('/copilot-adoption')
      ? 'copilot-adoption'
      : location.pathname.startsWith('/teams')
        ? 'teams'
        : location.pathname.startsWith('/user-lookup')
          ? 'user-lookup'
          : location.pathname.startsWith('/profiling')
            ? 'profiling'
            : location.pathname.startsWith('/install-log')
              ? 'install-log'
              : location.pathname.startsWith('/health')
                ? 'health'
                : 'home';

  const onTabSelect: SelectTabEventHandler = (_event, data) => {
    navigate(`/${data.value}`);
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

      <div className={styles.tabBar}>
        <TabList selectedValue={selectedTab} onTabSelect={onTabSelect} size="large">
          <Tab value="home">Home</Tab>
          <Tab value="reports">Reports</Tab>
          <Tab value="copilot-adoption">Copilot Adoption</Tab>
          <Tab value="teams">Teams Permissions</Tab>
          <Tab value="user-lookup">User Data Lookup</Tab>
          <Tab value="profiling">Profiling</Tab>
          <Tab value="install-log">Install Log</Tab>
          <Tab value="health">Health</Tab>
        </TabList>
      </div>

      <main className={styles.content}>
        <Suspense
          fallback={
            <div style={{ textAlign: 'center', padding: '32px' }}>
              <Spinner size={80} label="Loading..." />
            </div>
          }
        >
          <Routes>
            <Route path="/" element={<Navigate to="/home" replace />} />
            <Route path="/home" element={<HomePage />} />
            <Route path="/reports" element={<ReportsPage />} />
            <Route path="/copilot-adoption" element={<CopilotAdoptionPage />} />
            <Route path="/teams" element={<TeamsPermissionsPage />} />
            <Route path="/user-lookup" element={<UserLookupPage />} />
            <Route path="/profiling" element={<ProfilingStatusPage />} />
            <Route path="/install-log" element={<InstallLogPage />} />
            <Route path="/health" element={<HealthPage />} />
            <Route path="*" element={<Navigate to="/home" replace />} />
          </Routes>
        </Suspense>
      </main>
    </>
  );
}
