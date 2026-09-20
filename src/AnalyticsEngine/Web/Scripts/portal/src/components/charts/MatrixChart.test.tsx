import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import MatrixChart from './MatrixChart';
import type { ReportMatrix } from '../../types/reports';

const matrix = (overrides: Partial<ReportMatrix> = {}): ReportMatrix => ({
  rowLabel: 'App',
  columnLabel: 'Department',
  rows: ['Outlook', 'OneNote'],
  columns: ['Finance', 'Engineering'],
  cells: [
    { row: 'Outlook', column: 'Finance', value: 400 },
    { row: 'Outlook', column: 'Engineering', value: 300 },
    { row: 'OneNote', column: 'Finance', value: 2 },
    { row: 'OneNote', column: 'Engineering', value: 8 },
  ],
  shadeByRow: false,
  ...overrides,
});

/**
 * How strongly a cell is filled, 0 (empty) to 1 (full strength).
 *
 * Read as a number rather than by string-matching the colour because jsdom normalises a fully
 * opaque `rgba(r, g, b, 1.000)` back to `rgb(r, g, b)`. Asserting the literal string therefore
 * tests CSS serialisation rather than the shading rule, and fails for a reason that has nothing to
 * do with the component.
 */
function alphaOf(title: string): number {
  const style = screen.getByTitle(title).getAttribute('style') ?? '';
  const rgba = /background-color:\s*rgba\(\s*\d+,\s*\d+,\s*\d+,\s*([0-9.]+)\s*\)/.exec(style);
  if (rgba) return Number(rgba[1]);
  // Opaque brand blue is the full-strength end of the ramp.
  if (/background-color:\s*rgb\(\s*15,\s*108,\s*189\s*\)/.test(style)) return 1;
  return 0;
}

describe('MatrixChart', () => {
  it('says so when there is nothing to draw', () => {
    renderWithProvider(
      <MatrixChart matrix={matrix({ cells: [], rows: [], columns: [] })} valueLabel="People" />,
    );
    expect(screen.getByText('No data for this period.')).toBeInTheDocument();
  });

  it('treats an all-zero grid as no data rather than drawing an empty grid', () => {
    renderWithProvider(
      <MatrixChart
        matrix={matrix({ cells: [{ row: 'Outlook', column: 'Finance', value: 0 }] })}
        valueLabel="People"
      />,
    );
    expect(screen.getByText('No data for this period.')).toBeInTheDocument();
  });

  it('renders every declared row and column with a readable tooltip', () => {
    renderWithProvider(<MatrixChart matrix={matrix()} valueLabel="People" />);

    expect(screen.getByTitle('Outlook / Finance: 400 People')).toBeInTheDocument();
    expect(screen.getByTitle('OneNote / Engineering: 8 People')).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Finance' })).toBeInTheDocument();
    expect(screen.getByRole('rowheader', { name: 'OneNote' })).toBeInTheDocument();
  });

  /**
   * The server sends rows and columns explicitly so a genuinely empty one still renders. An app
   * that nobody in the business uses is usually the most useful row on the grid, and inferring the
   * axes from the cells would delete exactly that row.
   */
  it('keeps a row that has no cells at all', () => {
    renderWithProvider(
      <MatrixChart
        matrix={matrix({
          rows: ['Outlook', 'PowerPoint'],
          cells: [{ row: 'Outlook', column: 'Finance', value: 5 }],
        })}
        valueLabel="People"
      />,
    );

    expect(screen.getByRole('rowheader', { name: 'PowerPoint' })).toBeInTheDocument();
    expect(screen.getByTitle('PowerPoint / Finance: 0 People')).toBeInTheDocument();
  });

  it('adds together duplicate cells for the same intersection', () => {
    renderWithProvider(
      <MatrixChart
        matrix={matrix({
          cells: [
            { row: 'Outlook', column: 'Finance', value: 4 },
            { row: 'Outlook', column: 'Finance', value: 6 },
          ],
        })}
        valueLabel="People"
      />,
    );

    expect(screen.getByTitle('Outlook / Finance: 10 People')).toBeInTheDocument();
  });

  it('leaves a zero cell blank so "nobody" does not look like "almost nobody"', () => {
    renderWithProvider(
      <MatrixChart
        matrix={matrix({
          rows: ['Outlook'],
          columns: ['Finance', 'Engineering'],
          cells: [{ row: 'Outlook', column: 'Finance', value: 7 }],
        })}
        valueLabel="People"
      />,
    );

    const zeroCell = screen.getByTitle('Outlook / Engineering: 0 People');
    expect(zeroCell).toHaveTextContent('');
    expect(screen.getByTitle('Outlook / Finance: 7 People')).toHaveTextContent('7');
  });

  it('announces per-row shading so the reader knows what the colours are relative to', () => {
    const { rerender } = renderWithProvider(
      <MatrixChart matrix={matrix({ shadeByRow: true })} valueLabel="People" />,
    );
    expect(screen.getByText(/shaded within each row/)).toBeInTheDocument();

    rerender(<MatrixChart matrix={matrix({ shadeByRow: false })} valueLabel="People" />);
    expect(screen.queryByText(/shaded within each row/)).not.toBeInTheDocument();
  });

  /**
   * Row shading is the difference between the OneNote row being legible and being blank. With a
   * single grid-wide scale, OneNote's 2 and 8 against Outlook's 400 both round to the palest
   * possible fill and the row stops saying anything.
   */
  it('shades a small row against its own maximum when asked to', () => {
    renderWithProvider(<MatrixChart matrix={matrix({ shadeByRow: true })} valueLabel="People" />);

    // 8 is the maximum of its own row, so it reaches full strength despite being tiny next to the
    // 400 in the row above.
    expect(alphaOf('OneNote / Engineering: 8 People')).toBe(1);
    const weakest = alphaOf('OneNote / Finance: 2 People');
    expect(weakest).toBeLessThan(1);
    // ...and is still visibly filled rather than washed out to nothing.
    expect(weakest).toBeGreaterThan(0.15);

    expect(alphaOf('Outlook / Finance: 400 People')).toBe(1);
  });

  it('shades against the whole grid when per-row shading is off', () => {
    renderWithProvider(<MatrixChart matrix={matrix({ shadeByRow: false })} valueLabel="People" />);

    // 400 is the grid maximum, so only it is full strength - the whole OneNote row is now relative
    // to Outlook and becomes faint, which is exactly the effect shadeByRow exists to avoid.
    expect(alphaOf('Outlook / Finance: 400 People')).toBe(1);
    expect(alphaOf('OneNote / Engineering: 8 People')).toBeLessThan(0.3);
  });

  it('abbreviates large values so a wide grid stays readable', () => {
    renderWithProvider(
      <MatrixChart
        matrix={matrix({
          rows: ['Outlook'],
          columns: ['Finance'],
          cells: [{ row: 'Outlook', column: 'Finance', value: 12400 }],
        })}
        valueLabel="People"
      />,
    );

    // Abbreviated in the cell, exact in the tooltip.
    expect(screen.getByTitle('Outlook / Finance: 12,400 People')).toHaveTextContent('12.4k');
  });
});
