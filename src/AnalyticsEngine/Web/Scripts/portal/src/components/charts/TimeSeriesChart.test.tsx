import { describe, expect, it } from 'vitest';
import { renderWithProvider } from '../../test/renderWithProvider';
import TimeSeriesChart from './TimeSeriesChart';
import type { ReportSeries } from '../../types/reports';

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
