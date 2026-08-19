import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { DashboardStats } from '../types';
import { formatDate, formatMB, formatNumber, formatRelative } from '../format';
import { Section, StatCard, StatCardGrid, Surface } from '../components/layout';
import { BarRow } from '../components/BarRow';
import { SortableTable, type Column } from '../components/SortableTable';
import type { SchemaTotal, TableTotal } from '../types';

const useStyles = makeStyles({
    bars: { paddingBlock: '6px' },
    note: {
        color: tokens.colorNeutralForeground3,
        paddingInline: '16px',
        paddingBottom: '10px',
    },
});

const TOP_TABLES = 10;

export default function OverviewTab({ stats }: { stats: DashboardStats }) {
    const styles = useStyles();
    const f = stats.freshness;
    const sd = stats.sizeDistribution;

    const topTables = stats.tableTotals.slice(0, TOP_TABLES);

    const topTableColumns: Column<TableTotal>[] = [
        { key: 'name', header: 'Table', render: t => t.displayName, sortValue: t => t.displayName },
        { key: 'rows', header: 'Rows', numeric: true, render: t => formatNumber(t.rows), sortValue: t => t.rows },
        { key: 'size', header: 'Size', numeric: true, render: t => formatMB(t.totalSpaceMB), sortValue: t => t.totalSpaceMB },
    ];

    const schemaColumns: Column<SchemaTotal>[] = [
        { key: 'schema', header: 'Schema', render: s => s.schemaName, sortValue: s => s.schemaName },
        { key: 'tables', header: 'Tables', numeric: true, render: s => formatNumber(s.tableCount), sortValue: s => s.tableCount },
        { key: 'rows', header: 'Rows', numeric: true, render: s => formatNumber(s.rows), sortValue: s => s.rows },
        { key: 'size', header: 'Size', numeric: true, render: s => formatMB(s.totalSpaceMB), sortValue: s => s.totalSpaceMB },
    ];

    return (
        <>
            <Section title="At a glance">
                <StatCardGrid>
                    <StatCard label="Reporting clients" value={formatNumber(stats.clientCount)} />
                    <StatCard label="Total rows" value={formatNumber(stats.totalRows)} />
                    <StatCard label="Total size" value={formatMB(stats.totalSpaceMB)} />
                    <StatCard label="Distinct tables" value={formatNumber(stats.distinctTableCount)} />
                    <StatCard
                        label="Last report"
                        value={formatRelative(stats.lastUpdated)}
                        hint={formatDate(stats.lastUpdated)}
                        small
                    />
                </StatCardGrid>
            </Section>

            <Section
                title="Reporting freshness"
                description="When each client last checked in. Anything in the stale bucket has stopped reporting."
            >
                <Surface>
                    <div className={styles.bars}>
                        <BarRow label="Last 24 hours" count={f.last24Hours} total={stats.clientCount} />
                        <BarRow label="1–7 days" count={f.last7Days} total={stats.clientCount} />
                        <BarRow label="7–30 days" count={f.last30Days} total={stats.clientCount} />
                        <BarRow
                            label="Stale (30+ days)"
                            count={f.stale}
                            total={stats.clientCount}
                            color={f.stale > 0 ? tokens.colorPaletteRedBackground3 : undefined}
                        />
                    </div>
                </Surface>
            </Section>

            <Section
                title="Deployment size"
                description="Averages hide the shape of the install base, so the median and largest are shown too."
            >
                <StatCardGrid>
                    <StatCard label="Median rows / client" value={formatNumber(sd.medianRowsPerClient)} />
                    <StatCard label="Average rows / client" value={formatNumber(sd.avgRowsPerClient)} />
                    <StatCard label="Largest client" value={formatNumber(sd.maxRowsPerClient)} hint="rows" />
                    <StatCard label="Median size / client" value={formatMB(sd.medianSpaceMBPerClient)} small />
                    <StatCard label="Average size / client" value={formatMB(sd.avgSpaceMBPerClient)} small />
                    <StatCard label="Largest client size" value={formatMB(sd.maxSpaceMBPerClient)} small />
                    <StatCard label="Average tables / client" value={formatNumber(sd.avgTablesPerClient)} />
                </StatCardGrid>
            </Section>

            <Section
                title="Azure AI usage"
                description="Records enriched by Azure AI calls, across the clients that report it."
            >
                <StatCardGrid>
                    <StatCard label="AI data points" value={formatNumber(stats.aiDataPointsTotal)} />
                    <StatCard
                        label="Clients using AI"
                        value={`${stats.clientsReportingAi} / ${stats.clientCount}`}
                        small
                    />
                </StatCardGrid>
            </Section>

            {stats.schemaTotals.length > 0 && (
                <Section title="Storage by schema" description="Application tables versus profiling and other schemas.">
                    <Surface>
                        <SortableTable
                            items={stats.schemaTotals}
                            columns={schemaColumns}
                            rowKey={s => s.schemaName}
                            initialSortKey="rows"
                        />
                        <Text as="p" className={styles.note} size={200}>
                            Clients running builds older than the schema-reporting change appear under “(unknown)”.
                        </Text>
                    </Surface>
                </Section>
            )}

            {topTables.length > 0 && (
                <Section
                    title={`Top ${Math.min(TOP_TABLES, topTables.length)} tables by rows`}
                    description="See the Tables tab for the full list."
                >
                    <Surface>
                        <SortableTable
                            items={topTables}
                            columns={topTableColumns}
                            rowKey={t => t.displayName}
                            initialSortKey="rows"
                        />
                    </Surface>
                </Section>
            )}
        </>
    );
}
