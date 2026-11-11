import { describe, expect, it } from 'vitest';
import { renderWithProvider } from '../../test/renderWithProvider';
import StackedAreaChart from './StackedAreaChart';
import type { ReportSeries } from '../../types/reports';

const series = (middle: number | null): ReportSeries[] => [
  {
    name: 'Power',
    points: [
      { weekStart: '2026-03-02T00:00:00Z', value: 10 },
      { weekStart: '2026-03-09T00:00:00Z', value: middle },
      { weekStart: '2026-03-16T00:00:00Z', value: 12 },
      { weekStart: '2026-03-23T00:00:00Z', value: 11 },
    ],
  },
  {
    name: 'Light',
    points: [
      { weekStart: '2026-03-02T00:00:00Z', value: 5 },
      { weekStart: '2026-03-09T00:00:00Z', value: 6 },
      { weekStart: '2026-03-16T00:00:00Z', value: 7 },
      { weekStart: '2026-03-23T00:00:00Z', value: 8 },
    ],
  },
];

describe('StackedAreaChart', () => {
  it('draws a week withheld from a reader as a gap that says why, never as a zero', () => {
    const { container } = renderWithProvider(
      <StackedAreaChart series={series(null)} valueLabel="Users" withheldLabel="Withheld: fewer than 5 people" />,
    );

    // Each band is drawn as two runs - the week before the gap, and the two after it - rather than dipping to zero.
    expect(container.querySelectorAll('polygon')).toHaveLength(4);
    const titles = Array.from(container.querySelectorAll('rect > title')).map((title) => title.textContent ?? '');
    expect(titles.filter((title) => title.includes('Withheld: fewer than 5 people'))).toHaveLength(1);
    expect(titles.some((title) => /Power: 0\b/.test(title))).toBe(false);
  });

  it('still counts a null as zero for a chart that does not say nulls are withheld', () => {
    const { container } = renderWithProvider(<StackedAreaChart series={series(null)} valueLabel="Users" />);

    expect(container.querySelectorAll('polygon')).toHaveLength(2);
    const titles = Array.from(container.querySelectorAll('rect > title')).map((title) => title.textContent ?? '');
    expect(titles.some((title) => /Power: 0\b/.test(title))).toBe(true);
  });
});
