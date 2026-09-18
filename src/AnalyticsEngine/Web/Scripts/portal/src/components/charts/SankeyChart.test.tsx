import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import SankeyChart, { type SankeyFlow } from './SankeyChart';

const flows: SankeyFlow[] = [
  { sourceKey: '/home', sourceLabel: 'Home', targetKey: '/expenses', targetLabel: 'Expenses', value: 60 },
  { sourceKey: '/home', sourceLabel: 'Home', targetKey: '/home', targetLabel: 'Home', value: 30, isSelfFlow: true },
  { sourceKey: '/news', sourceLabel: 'News', targetKey: '/expenses', targetLabel: 'Expenses', value: 10 },
];

describe('SankeyChart', () => {
  it('says so rather than drawing an empty frame when there are no flows', () => {
    render(<SankeyChart flows={[]} valueLabel="visits" />);
    expect(screen.getByText(/No visit flows/i)).toBeInTheDocument();
  });

  it('ignores zero and negative flows instead of drawing a zero-width band', () => {
    render(
      <SankeyChart
        flows={[
          { sourceKey: '/home', sourceLabel: 'Home', targetKey: '/expenses', targetLabel: 'Expenses', value: 0 },
          { sourceKey: '/news', sourceLabel: 'News', targetKey: '/policies', targetLabel: 'Policies', value: -5 },
        ]}
        valueLabel="visits"
      />,
    );

    expect(screen.getByText(/No visit flows/i)).toBeInTheDocument();
  });

  it('exposes every flow as text, because the diagram itself is unreadable to a screen reader', () => {
    render(<SankeyChart flows={flows} valueLabel="visits" />);

    const table = screen.getByRole('table', { name: /where visits started and ended/i });
    expect(table).toBeInTheDocument();

    // One row per flow, and a self-flow reads as staying put rather than repeating the page name,
    // which would otherwise be read out as "Home, Home".
    expect(screen.getByRole('row', { name: /Home the same page 30/i })).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /Home Expenses 60/i })).toBeInTheDocument();
  });

  it('keeps self-flows in the diagram rather than hiding them', () => {
    const { container } = render(<SankeyChart flows={flows} valueLabel="visits" />);

    // Three flows in, three ribbons out. Dropping the self-flow would imply a journey happened
    // where none did, and on most intranets it is the largest single band.
    expect(container.querySelectorAll('svg path').length).toBe(3);
  });

  it('keeps two pages that share a title apart, because SharePoint titles are not unique', () => {
    // The SQL keys flows on url ids, so two different pages both called "Home" arrive as two
    // distinct flows. Keying the chart on the title instead merged them into one node whose height
    // was a number no real page had, and produced duplicate React keys on the ribbons.
    const shared: SankeyFlow[] = [
      { sourceKey: '/sites/a/home', sourceLabel: 'Home', targetKey: '/expenses', targetLabel: 'Expenses', value: 40 },
      { sourceKey: '/sites/b/home', sourceLabel: 'Home', targetKey: '/expenses', targetLabel: 'Expenses', value: 30 },
    ];

    const { container } = render(<SankeyChart flows={shared} valueLabel="visits" />);

    // The discriminating assertion. Both shapes draw two ribbons and two screen-reader rows, so
    // only the NODE count tells them apart: keyed on the title the left column collapses to a
    // single "Home" node worth 70 visits (2 rects); keyed on the URL it is two nodes (3 rects).
    expect(container.querySelectorAll('svg rect').length).toBe(3);
    expect(container.querySelectorAll('svg path').length).toBe(2);

    const rows = screen.getAllByRole('row', { name: /Home Expenses/i });
    expect(rows.length).toBe(2);
    expect(screen.getByRole('row', { name: /Home Expenses 40/i })).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /Home Expenses 30/i })).toBeInTheDocument();
  });

  it('gives the diagram an accessible name that says what it measures', () => {
    render(<SankeyChart flows={flows} valueLabel="visits" />);
    expect(screen.getByRole('img', { name: /started and ended.*visits/i })).toBeInTheDocument();
  });

  it('shows the caption it is given, so the diagram can admit what it leaves out', () => {
    render(<SankeyChart flows={flows} valueLabel="visits" caption="Covers 84.2% of visits." />);
    expect(screen.getByText('Covers 84.2% of visits.')).toBeInTheDocument();
  });
});
