import { describe, expect, it } from 'vitest';
import { renderWithProvider } from '../../test/renderWithProvider';
import TimeSeriesChart from './TimeSeriesChart';
import { CHART_PALETTE, lineSeriesStyle } from './chartCommon';
import type { ReportSeries } from '../../types/reports';

describe('TimeSeriesChart many series', () => {
  const weeks = ['2026-09-07', '2026-09-14', '2026-09-21'];
  const series: ReportSeries[] = Array.from({ length: 20 }, (_, i) => ({
    name: `Series ${i + 1}`,
    points: weeks.map((weekStart, w) => ({ weekStart, value: i + w })),
  }));

  it('keeps the first eight colours, then tells later lines apart by colour and stroke pattern', () => {
    const { container } = renderWithProvider(<TimeSeriesChart series={series} valueLabel="Activity" />);
    const paths = [...container.querySelectorAll('svg[role="img"] path')];

    expect(paths).toHaveLength(20);
    expect(paths.slice(0, 8).map((p) => p.getAttribute('stroke'))).toEqual([...CHART_PALETTE]);
    expect(new Set(paths.slice(0, 16).map((p) => p.getAttribute('stroke'))).size).toBe(16);
    expect(paths[15].getAttribute('stroke-dasharray')).toBeNull();
    // The seventeenth line reuses the first colour, dashed.
    expect(paths[16].getAttribute('stroke')).toBe(paths[0].getAttribute('stroke'));
    expect(paths[16].getAttribute('stroke-dasharray')).toBeTruthy();
    // ...and its legend key shows the dash, which a colour square could not.
    expect(container.querySelectorAll('svg[aria-hidden="true"] line')).toHaveLength(4);
  });

  it('gives every one of 58 lines a different colour and pattern', () => {
    const styles = Array.from({ length: 58 }, (_, i) => {
      const { color, dash } = lineSeriesStyle(i);
      return `${color}|${dash ?? 'solid'}`;
    });
    expect(new Set(styles).size).toBe(58);
  });
});

describe('TimeSeriesChart gaps', () => {
  it('draws null points as gaps and explains them in the legend', () => {
    const series: ReportSeries[] = [
      {
        name: 'Active licensed users',
        points: [
          { weekStart: '2026-09-07T00:00:00Z', value: 5 },
          { weekStart: '2026-09-14T00:00:00Z', value: null },
          { weekStart: '2026-09-21T00:00:00Z', value: 7 },
        ],
      },
    ];

    const { container, getByText } = renderWithProvider(
      <TimeSeriesChart series={series} valueLabel="Users" gapNote="Gap means coverage could not be verified." />,
    );

    expect(getByText('Gap means coverage could not be verified.')).toBeInTheDocument();

    const path = container.querySelector('path');
    expect(path?.getAttribute('d')).toContain('M ');
    expect((path?.getAttribute('d')?.match(/M /g) ?? [])).toHaveLength(2);
  });
});
