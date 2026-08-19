import { useMemo, useState } from 'react';
import { Badge, Input, makeStyles, Popover, PopoverSurface, PopoverTrigger, Text, tokens } from '@fluentui/react-components';
import { Search20Regular } from '@fluentui/react-icons';
import type { ClientSummary } from '../types';
import { formatDate, formatMB, formatNumber, formatRelative } from '../format';
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
    mono: {
        fontFamily: tokens.fontFamilyMonospace,
        fontSize: tokens.fontSizeBase200,
    },
    stale: { color: tokens.colorPaletteRedForeground1 },
    importList: {
        display: 'flex',
        flexDirection: 'column',
        gap: '4px',
        maxWidth: '280px',
    },
    trigger: { cursor: 'pointer' },
});

const STALE_AFTER_DAYS = 30;

function isStale(generated: string | null): boolean {
    if (!generated) return true;
    const d = new Date(generated);
    if (isNaN(d.getTime())) return true;
    return Date.now() - d.getTime() > STALE_AFTER_DAYS * 24 * 60 * 60 * 1000;
}

export default function ClientsTab({ clients }: { clients: ClientSummary[] }) {
    const styles = useStyles();
    const [filter, setFilter] = useState('');

    const filtered = useMemo(() => {
        const needle = filter.trim().toLowerCase();
        if (!needle) return clients;
        return clients.filter(c =>
            c.anonClientId.toLowerCase().includes(needle) ||
            (c.buildVersionLabel ?? '').toLowerCase().includes(needle));
    }, [clients, filter]);

    const columns: Column<ClientSummary>[] = [
        {
            key: 'id',
            header: 'Anon client ID',
            render: c => <span className={styles.mono}>{c.anonClientId.slice(0, 16)}…</span>,
            sortValue: c => c.anonClientId,
        },
        {
            key: 'generated',
            header: 'Last report',
            render: c => (
                <span className={isStale(c.generated) ? styles.stale : undefined} title={formatDate(c.generated)}>
                    {formatRelative(c.generated)}
                </span>
            ),
            sortValue: c => (c.generated ? new Date(c.generated).getTime() : 0),
        },
        {
            key: 'build',
            header: 'Build',
            render: c => c.buildVersionLabel ?? '—',
            sortValue: c => c.buildVersionLabel ?? '',
        },
        { key: 'rows', header: 'Rows', numeric: true, render: c => formatNumber(c.rows), sortValue: c => c.rows },
        { key: 'size', header: 'Size', numeric: true, render: c => formatMB(c.totalSpaceMB), sortValue: c => c.totalSpaceMB },
        { key: 'tables', header: 'Tables', numeric: true, render: c => formatNumber(c.tableCount), sortValue: c => c.tableCount },
        {
            key: 'ai',
            header: 'AI data points',
            numeric: true,
            render: c => formatNumber(c.dataPointsFromAITotal),
            sortValue: c => c.dataPointsFromAITotal ?? -1,
        },
        {
            key: 'imports',
            header: 'Imports on',
            numeric: true,
            render: c =>
                c.enabledImports.length === 0 ? (
                    '—'
                ) : (
                    <Popover withArrow>
                        <PopoverTrigger disableButtonEnhancement>
                            <Badge appearance="tint" color="brand" className={styles.trigger}>
                                {c.enabledImports.length}
                            </Badge>
                        </PopoverTrigger>
                        <PopoverSurface>
                            <div className={styles.importList}>
                                <Text weight="semibold">Enabled imports</Text>
                                {c.enabledImports.map(i => (
                                    <Text key={i} size={200}>{i}</Text>
                                ))}
                            </div>
                        </PopoverSurface>
                    </Popover>
                ),
            sortValue: c => c.enabledImports.length,
        },
    ];

    return (
        <Section
            title="Reporting clients"
            description="One row per installation. IDs are one-way hashes of the tenant ID — no tenant is identifiable."
        >
            <div className={styles.toolbar}>
                <Input
                    className={styles.search}
                    placeholder="Filter by client ID or build…"
                    value={filter}
                    onChange={(_e, d) => setFilter(d.value)}
                    contentBefore={<Search20Regular />}
                />
                <Text className={styles.count}>
                    {filtered.length === clients.length
                        ? `${formatNumber(clients.length)} clients`
                        : `${formatNumber(filtered.length)} of ${formatNumber(clients.length)} clients`}
                </Text>
            </div>

            <Surface>
                <SortableTable
                    items={filtered}
                    columns={columns}
                    rowKey={c => c.anonClientId}
                    initialSortKey="generated"
                    emptyMessage="No clients match that filter."
                />
            </Surface>
        </Section>
    );
}
