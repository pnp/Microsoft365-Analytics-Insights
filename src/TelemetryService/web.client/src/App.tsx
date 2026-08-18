import { useEffect, useState } from 'react';
import './App.css';
import type { DashboardAuth } from './auth';

interface TableTotal {
    tableName: string;
    rows: number;
    totalSpaceMB: number;
    clientCount: number;
}

interface DashboardStats {
    clientCount: number;
    totalRows: number;
    totalSpaceMB: number;
    lastUpdated: string | null;
    tableTotals: TableTotal[];
}

interface ClientSummary {
    anonClientId: string;
    generated: string | null;
    buildVersionLabel: string | null;
    configuredImportsEnabledDescription: string | null;
    configuredSolutionsEnabledDescription: string | null;
    dataPointsFromAITotal: number | null;
    rows: number;
    totalSpaceMB: number;
    tableCount: number;
}

function formatNumber(n: number): string {
    return n.toLocaleString();
}

function formatMB(mb: number): string {
    if (mb >= 1024) {
        return `${(mb / 1024).toFixed(2)} GB`;
    }
    return `${mb.toFixed(2)} MB`;
}

function formatDate(value: string | null): string {
    if (!value) return '—';
    const d = new Date(value);
    if (isNaN(d.getTime())) return value;
    return d.toLocaleString();
}

function App({ auth }: { auth: DashboardAuth }) {
    const [stats, setStats] = useState<DashboardStats | null>(null);
    const [clients, setClients] = useState<ClientSummary[] | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [loading, setLoading] = useState<boolean>(true);

    useEffect(() => {
        let cancelled = false;

        async function load() {
            setLoading(true);
            setError(null);
            try {
                const accessToken = await auth.getAccessToken();
                const request = {
                    headers: {
                        Authorization: `Bearer ${accessToken}`,
                    },
                };
                const [statsRes, clientsRes] = await Promise.all([
                    fetch('/api/Telemetry/stats', request),
                    fetch('/api/Telemetry/clients', request),
                ]);
                if (!statsRes.ok) throw new Error(`stats: HTTP ${statsRes.status}`);
                if (!clientsRes.ok) throw new Error(`clients: HTTP ${clientsRes.status}`);

                const statsData: DashboardStats = await statsRes.json();
                const clientsData: ClientSummary[] = await clientsRes.json();
                if (cancelled) return;
                setStats(statsData);
                setClients(clientsData);
            } catch (e: unknown) {
                if (cancelled) return;
                setError(e instanceof Error ? e.message : 'Failed to load dashboard data');
            } finally {
                if (!cancelled) setLoading(false);
            }
        }

        load();
        return () => { cancelled = true; };
    }, [auth]);

    function signOut() {
        void auth.signOut().catch((e: unknown) => {
            setError(e instanceof Error ? e.message : 'Sign-out failed.');
        });
    }

    return (
        <div className="dashboard">
            <header className="dashboard-header">
                <div>
                    <h1>Analytics Telemetry</h1>
                    <p className="subtitle">
                        Anonymous usage stats reported by Microsoft 365 Analytics Insights installations.
                    </p>
                </div>
                <div className="auth-controls">
                    <span>{auth.accountName}</span>
                    <button type="button" onClick={signOut}>Sign out</button>
                </div>
            </header>

            {loading && <p><em>Loading…</em></p>}

            {error && (
                <div className="error">
                    <strong>Could not load dashboard data:</strong> {error}
                </div>
            )}

            {!loading && !error && stats && (
                <>
                    {stats.clientCount === 0 ? (
                        <p className="empty">
                            No telemetry has been received yet. Once an importer instance has
                            <code> StatsApiUrl</code> + <code>StatsApiSecret</code> configured to
                            point at this service it will start reporting in.
                        </p>
                    ) : (
                        <section className="cards">
                            <Card label="Reporting clients" value={formatNumber(stats.clientCount)} />
                            <Card label="Total rows" value={formatNumber(stats.totalRows)} />
                            <Card label="Total size" value={formatMB(stats.totalSpaceMB)} />
                            <Card label="Last update" value={formatDate(stats.lastUpdated)} />
                        </section>
                    )}

                    {stats.tableTotals.length > 0 && (
                        <section>
                            <h2>Tables (aggregated across clients)</h2>
                            <table className="data-table">
                                <thead>
                                    <tr>
                                        <th>Table</th>
                                        <th className="num">Rows</th>
                                        <th className="num">Size</th>
                                        <th className="num">Clients</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {stats.tableTotals.map(t => (
                                        <tr key={t.tableName}>
                                            <td>{t.tableName}</td>
                                            <td className="num">{formatNumber(t.rows)}</td>
                                            <td className="num">{formatMB(t.totalSpaceMB)}</td>
                                            <td className="num">{t.clientCount}</td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </section>
                    )}
                </>
            )}

            {!loading && !error && clients && clients.length > 0 && (
                <section>
                    <h2>Clients</h2>
                    <table className="data-table">
                        <thead>
                            <tr>
                                <th>Anon client ID</th>
                                <th>Last report</th>
                                <th>Build</th>
                                <th>Imports</th>
                                <th>Solutions</th>
                                <th className="num">Rows</th>
                                <th className="num">Size</th>
                                <th className="num">AI calls</th>
                            </tr>
                        </thead>
                        <tbody>
                            {clients.map(c => (
                                <tr key={c.anonClientId}>
                                    <td className="mono">{c.anonClientId}</td>
                                    <td>{formatDate(c.generated)}</td>
                                    <td>{c.buildVersionLabel ?? '—'}</td>
                                    <td>{c.configuredImportsEnabledDescription ?? '—'}</td>
                                    <td>{c.configuredSolutionsEnabledDescription ?? '—'}</td>
                                    <td className="num">{formatNumber(c.rows)}</td>
                                    <td className="num">{formatMB(c.totalSpaceMB)}</td>
                                    <td className="num">{c.dataPointsFromAITotal != null ? formatNumber(c.dataPointsFromAITotal) : '—'}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </section>
            )}
        </div>
    );
}

function Card({ label, value }: { label: string; value: string }) {
    return (
        <div className="card">
            <div className="card-label">{label}</div>
            <div className="card-value">{value}</div>
        </div>
    );
}

export default App;