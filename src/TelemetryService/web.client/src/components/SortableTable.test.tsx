import { describe, expect, it } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import type { ReactElement } from 'react';
import { SortableTable, type Column } from './SortableTable';

interface Row {
    name: string;
    rows: number;
}

const data: Row[] = [
    { name: 'beta', rows: 20 },
    { name: 'alpha', rows: 300 },
    { name: 'gamma', rows: 1 },
];

const columns: Column<Row>[] = [
    { key: 'name', header: 'Table', render: r => r.name, sortValue: r => r.name },
    { key: 'rows', header: 'Rows', numeric: true, render: r => String(r.rows), sortValue: r => r.rows },
    { key: 'static', header: 'Static', render: () => 'x' },
];

function renderWithProvider(ui: ReactElement) {
    return render(<FluentProvider theme={webLightTheme}>{ui}</FluentProvider>);
}

function rowOrder(): string[] {
    // First row group is the header, so read the body rows only.
    const rows = screen.getAllByRole('row').slice(1);
    return rows.map(r => within(r).getAllByRole('cell')[0].textContent ?? '');
}

describe('SortableTable', () => {
    it('shows the empty message instead of an empty table', () => {
        renderWithProvider(
            <SortableTable items={[]} columns={columns} rowKey={r => r.name} emptyMessage="Nothing here." />);

        expect(screen.getByText('Nothing here.')).toBeInTheDocument();
        expect(screen.queryByRole('table')).not.toBeInTheDocument();
    });

    it('applies the initial sort descending', () => {
        renderWithProvider(
            <SortableTable items={data} columns={columns} rowKey={r => r.name} initialSortKey="rows" />);

        expect(rowOrder()).toEqual(['alpha', 'beta', 'gamma']);
    });

    it('honours an ascending initial sort', () => {
        renderWithProvider(
            <SortableTable
                items={data}
                columns={columns}
                rowKey={r => r.name}
                initialSortKey="rows"
                initialDescending={false}
            />);

        expect(rowOrder()).toEqual(['gamma', 'beta', 'alpha']);
    });

    it('toggles direction when the same column header is clicked again', async () => {
        const user = userEvent.setup();
        renderWithProvider(
            <SortableTable items={data} columns={columns} rowKey={r => r.name} initialSortKey="rows" />);

        await user.click(screen.getByText('Rows'));

        expect(rowOrder()).toEqual(['gamma', 'beta', 'alpha']);
    });

    it('sorts strings case-insensitively when a different column is picked', async () => {
        const user = userEvent.setup();
        renderWithProvider(
            <SortableTable items={data} columns={columns} rowKey={r => r.name} initialSortKey="rows" />);

        await user.click(screen.getByText('Table'));

        // Switching column starts descending.
        expect(rowOrder()).toEqual(['gamma', 'beta', 'alpha']);
    });

    it('ignores clicks on columns that have no sort value', async () => {
        const user = userEvent.setup();
        renderWithProvider(
            <SortableTable items={data} columns={columns} rowKey={r => r.name} initialSortKey="rows" />);

        const before = rowOrder();
        await user.click(screen.getByText('Static'));

        expect(rowOrder()).toEqual(before);
    });

    it('renders every row when unsorted', () => {
        renderWithProvider(<SortableTable items={data} columns={columns} rowKey={r => r.name} />);

        expect(screen.getAllByRole('row')).toHaveLength(data.length + 1);
    });
});
