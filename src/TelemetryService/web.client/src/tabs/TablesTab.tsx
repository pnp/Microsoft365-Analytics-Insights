import { useMemo, useState } from 'react';
import { Input, makeStyles, tokens, Text } from '@fluentui/react-components';
import { Search20Regular } from '@fluentui/react-icons';
import type { DashboardStats, TableTotal } from '../types';
import { formatMB, formatNumber } from '../format';
import { Section, Surface } from '../components/layout';
import { SortableTable, type Column } from '../components/SortableTable';

const useStyles = makeStyles({
    toolbar: {
        display: 'flex',
        alignItems: 'center',
        gap: '12px',
        flexWrap: 'wrap',
    },
    search: { minWidth: '260px' },
    count: { color: tokens.colorNeutralForeground3 },
    schema: { color: tokens.colorNeutralForeground3 },
});

export default function TablesTab({ stats }: { stats: DashboardStats }) {
    const styles = useStyles();
    const [filter, setFilter] = useState('');

    const filtered = useMemo(() => {
        const needle = filter.trim().toLowerCase();
        if (!needle) return stats.tableTotals;
        return stats.tableTotals.filter(t => t.displayName.toLowerCase().includes(needle));
    }, [stats.tableTotals, filter]);

    const columns: Column<TableTotal>[] = [
        {
            key: 'name',
            header: 'Table',
            render: t => (
                <>
                    {t.schemaName && <Text className={styles.schema}>{t.schemaName}.</Text>}
                    {t.tableName}
                </>
            ),
            sortValue: t => t.displayName,
        },
        { key: 'rows', header: 'Rows', numeric: true, render: t => formatNumber(t.rows), sortValue: t => t.rows },
        { key: 'size', header: 'Size', numeric: true, render: t => formatMB(t.totalSpaceMB), sortValue: t => t.totalSpaceMB },
        {
            key: 'clients',
            header: 'Clients',
            numeric: true,
            render: t => formatNumber(t.clientCount),
            sortValue: t => t.clientCount,
        },
        {
            key: 'avg',
            header: 'Avg rows / client',
            numeric: true,
            render: t => formatNumber(t.clientCount > 0 ? Math.round(t.rows / t.clientCount) : 0),
            sortValue: t => (t.clientCount > 0 ? t.rows / t.clientCount : 0),
        },
    ];

    return (
        <Section
            title="Tables"
            description="Row counts and storage aggregated across every reporting client. Click a column to sort."
        >
            <div className={styles.toolbar}>
                <Input
                    className={styles.search}
                    placeholder="Filter tables…"
                    value={filter}
                    onChange={(_e, d) => setFilter(d.value)}
                    contentBefore={<Search20Regular />}
                />
                <Text className={styles.count}>
                    {filtered.length === stats.tableTotals.length
                        ? `${formatNumber(stats.tableTotals.length)} tables`
                        : `${formatNumber(filtered.length)} of ${formatNumber(stats.tableTotals.length)} tables`}
                </Text>
            </div>

            <Surface>
                <SortableTable
                    items={filtered}
                    columns={columns}
                    rowKey={t => t.displayName}
                    initialSortKey="rows"
                    emptyMessage="No tables match that filter."
                />
            </Surface>
        </Section>
    );
}
