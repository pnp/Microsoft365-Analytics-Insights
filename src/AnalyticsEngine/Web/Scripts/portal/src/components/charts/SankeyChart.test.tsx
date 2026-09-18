import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import SankeyChart, { type SankeyFlow } from './SankeyChart';

const flows: SankeyFlow[] = [
  { sourceLabel: 'Home', targetLabel: 'Expenses', value: 60 },
  { sourceLabel: 'Home', targetLabel: 'Home (stayed)', value: 30, isSelfFlow: true },
  { sourceLabel: 'News', targetLabel: 'Expenses', value: 10 },
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
          { sourceLabel: 'Home', targetLabel: 'Expenses', value: 0 },
          { sourceLabel: 'News', targetLabel: 'Policies', value: -5 },
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

  it('gives the diagram an accessible name that says what it measures', () => {
    render(<SankeyChart flows={flows} valueLabel="visits" />);
    expect(screen.getByRole('img', { name: /started and ended.*visits/i })).toBeInTheDocument();
  });

  it('shows the caption it is given, so the diagram can admit what it leaves out', () => {
    render(<SankeyChart flows={flows} valueLabel="visits" caption="Covers 84.2% of visits." />);
    expect(screen.getByText('Covers 84.2% of visits.')).toBeInTheDocument();
  });
});
