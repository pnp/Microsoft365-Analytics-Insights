import { useMemo, useState, type ReactNode } from 'react';
import {
    makeStyles,
    tokens,
    Table,
    TableBody,
    TableCell,
    TableHeader,
    TableHeaderCell,
    TableRow,
} from '@fluentui/react-components';
import { EmptyState } from './layout';

export interface Column<T> {
    key: string;
    header: string;
    /** Right-aligns and tabular-numbers the column. */
    numeric?: boolean;
    render: (item: T) => ReactNode;
    /** Omit to make the column unsortable. */
    sortValue?: (item: T) => number | string;
}

const useStyles = makeStyles({
    numeric: {
        textAlign: 'right',
        fontVariantNumeric: 'tabular-nums',
    },
    headerNumeric: {
        textAlign: 'right',
        // The header cell renders a flex button internally; push it to the right edge so it
        // lines up with the right-aligned numbers beneath it.
        '& > *': { justifyContent: 'flex-end' },
    },
    zebra: {
        ':nth-child(even)': {
            backgroundColor: tokens.colorNeutralBackground2,
        },
    },
});

/**
 * Small sortable table over the Fluent primitives. Deliberately not DataGrid: these lists are
 * short enough that virtualisation is unnecessary, and this keeps full control of cell rendering.
 */
export function SortableTable<T>({
    items,
    columns,
    rowKey,
    initialSortKey,
    initialDescending = true,
    emptyMessage = 'Nothing to show.',
}: {
    items: T[];
    columns: Column<T>[];
    rowKey: (item: T) => string;
    initialSortKey?: string;
    initialDescending?: boolean;
    emptyMessage?: string;
}) {
    const styles = useStyles();
    const [sortKey, setSortKey] = useState<string | undefined>(initialSortKey);
    const [descending, setDescending] = useState<boolean>(initialDescending);

    const sorted = useMemo(() => {
        const column = columns.find(c => c.key === sortKey);
        if (!column?.sortValue) return items;

        const copy = [...items];
        copy.sort((a, b) => {
            const av = column.sortValue!(a);
            const bv = column.sortValue!(b);
            let result: number;
            if (typeof av === 'number' && typeof bv === 'number') {
                result = av - bv;
            } else {
                result = String(av).localeCompare(String(bv), undefined, { sensitivity: 'base' });
            }
            return descending ? -result : result;
        });
        return copy;
    }, [items, columns, sortKey, descending]);

    function toggleSort(column: Column<T>) {
        if (!column.sortValue) return;
        if (column.key === sortKey) {
            setDescending(d => !d);
        } else {
            setSortKey(column.key);
            setDescending(true);
        }
    }

    if (items.length === 0) {
        return <EmptyState message={emptyMessage} />;
    }

    return (
        <Table size="small" aria-label="Telemetry data">
            <TableHeader>
                <TableRow>
                    {columns.map(c => (
                        <TableHeaderCell
                            key={c.key}
                            className={c.numeric ? styles.headerNumeric : undefined}
                            sortable={!!c.sortValue}
                            sortDirection={
                                c.sortValue && c.key === sortKey
                                    ? (descending ? 'descending' : 'ascending')
                                    : undefined
                            }
                            onClick={() => toggleSort(c)}
                        >
                            {c.header}
                        </TableHeaderCell>
                    ))}
                </TableRow>
            </TableHeader>
            <TableBody>
                {sorted.map(item => (
                    <TableRow key={rowKey(item)} className={styles.zebra}>
                        {columns.map(c => (
                            <TableCell key={c.key} className={c.numeric ? styles.numeric : undefined}>
                                {c.render(item)}
                            </TableCell>
                        ))}
                    </TableRow>
                ))}
            </TableBody>
        </Table>
    );
}
