import { lazy, Suspense, useEffect, useState } from 'react';
import {
    Button,
    makeStyles,
    MessageBar,
    MessageBarBody,
    MessageBarTitle,
    Spinner,
    Tab,
    TabList,
    Text,
    tokens,
    type SelectTabEventHandler,
} from '@fluentui/react-components';
import { ArrowClockwise20Regular, SignOut20Regular } from '@fluentui/react-icons';
import type { DashboardAuth } from './auth';
import type { ClientSummary, DashboardStats } from './types';

// Code-split the tabs so the initial load only pays for the Overview chunk.
const OverviewTab = lazy(() => import('./tabs/OverviewTab'));
const TablesTab = lazy(() => import('./tabs/TablesTab'));
const ClientsTab = lazy(() => import('./tabs/ClientsTab'));
const AdoptionTab = lazy(() => import('./tabs/AdoptionTab'));

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
    headerRight: {
        display: 'flex',
        alignItems: 'center',
        gap: '8px',
    },
    account: { color: tokens.colorNeutralForegroundOnBrand },
    headerButton: { color: tokens.colorNeutralForegroundOnBrand },
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
    intro: {
        color: tokens.colorNeutralForeground3,
        marginBottom: '20px',
        display: 'block',
    },
    centre: {
        display: 'flex',
        justifyContent: 'center',
        padding: '48px',
    },
});

type TabValue = 'overview' | 'tables' | 'clients' | 'adoption';

export default function App({ auth }: { auth: DashboardAuth }) {
    const styles = useStyles();
    const [stats, setStats] = useState<DashboardStats | null>(null);
    const [clients, setClients] = useState<ClientSummary[] | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [loading, setLoading] = useState(true);
    const [reloadKey, setReloadKey] = useState(0);
    const [selectedTab, setSelectedTab] = useState<TabValue>('overview');

    // The effect body deliberately performs no synchronous setState: it starts an async
    // function whose first statement awaits, so React never sees a cascading render. The
    // spinner is driven by `loading` starting true, and by the refresh handler below.
    useEffect(() => {
        const controller = new AbortController();

        void (async () => {
            try {
                const accessToken = await auth.getAccessToken();
                if (controller.signal.aborted) return;

                const request = {
                    headers: { Authorization: `Bearer ${accessToken}` },
                    signal: controller.signal,
                };
                const [statsRes, clientsRes] = await Promise.all([
                    fetch('/api/Telemetry/stats', request),
                    fetch('/api/Telemetry/clients', request),
                ]);
                if (!statsRes.ok) throw new Error(`stats: HTTP ${statsRes.status}`);
                if (!clientsRes.ok) throw new Error(`clients: HTTP ${clientsRes.status}`);

                const statsData: DashboardStats = await statsRes.json();
                const clientsData: ClientSummary[] = await clientsRes.json();
                if (controller.signal.aborted) return;

                setStats(statsData);
                setClients(clientsData);
                setError(null);
            } catch (e: unknown) {
                if (controller.signal.aborted) return;
                setError(e instanceof Error ? e.message : 'Failed to load dashboard data');
            } finally {
                if (!controller.signal.aborted) setLoading(false);
            }
        })();

        return () => controller.abort();
    }, [auth, reloadKey]);

    function refresh() {
        setLoading(true);
        setError(null);
        setReloadKey(k => k + 1);
    }

    const onTabSelect: SelectTabEventHandler = (_event, data) => {
        setSelectedTab(data.value as TabValue);
    };

    function signOut() {
        void auth.signOut().catch((e: unknown) => {
            setError(e instanceof Error ? e.message : 'Sign-out failed.');
        });
    }

    const hasData = !!stats && stats.clientCount > 0;

    return (
        <>
            <header className={styles.header}>
                <Text size={400} className={styles.brand}>
                    Microsoft 365 Analytics — Telemetry
                </Text>
                <div className={styles.headerRight}>
                    <Text className={styles.account}>{auth.accountName}</Text>
                    <Button
                        appearance="transparent"
                        className={styles.headerButton}
                        icon={<ArrowClockwise20Regular />}
                        disabled={loading}
                        onClick={() => refresh()}
                    >
                        Refresh
                    </Button>
                    <Button
                        appearance="transparent"
                        className={styles.headerButton}
                        icon={<SignOut20Regular />}
                        onClick={signOut}
                    >
                        Sign out
                    </Button>
                </div>
            </header>

            <div className={styles.tabBar}>
                <TabList selectedValue={selectedTab} onTabSelect={onTabSelect} size="large">
                    <Tab value="overview">Overview</Tab>
                    <Tab value="tables">Tables</Tab>
                    <Tab value="clients">Clients</Tab>
                    <Tab value="adoption">Adoption</Tab>
                </TabList>
            </div>

            <main className={styles.content}>
                <Text className={styles.intro}>
                    Anonymous usage statistics reported by Microsoft 365 Analytics Insights installations.
                </Text>

                {error && (
                    <MessageBar intent="error">
                        <MessageBarBody>
                            <MessageBarTitle>Could not load dashboard data</MessageBarTitle>
                            {error}
                        </MessageBarBody>
                    </MessageBar>
                )}

                {loading && (
                    <div className={styles.centre}>
                        <Spinner size="large" label="Loading telemetry…" />
                    </div>
                )}

                {!loading && !error && stats && !hasData && (
                    <MessageBar intent="info">
                        <MessageBarBody>
                            <MessageBarTitle>No telemetry received yet</MessageBarTitle>
                            Once an importer instance has StatsApiUrl and StatsApiSecret configured to point
                            at this service, it will start reporting in.
                        </MessageBarBody>
                    </MessageBar>
                )}

                {!loading && !error && stats && hasData && (
                    <Suspense
                        fallback={
                            <div className={styles.centre}>
                                <Spinner size="large" label="Loading…" />
                            </div>
                        }
                    >
                        {selectedTab === 'overview' && <OverviewTab stats={stats} />}
                        {selectedTab === 'tables' && <TablesTab stats={stats} />}
                        {selectedTab === 'clients' && <ClientsTab clients={clients ?? []} />}
                        {selectedTab === 'adoption' && <AdoptionTab stats={stats} />}
                    </Suspense>
                )}
            </main>
        </>
    );
}
