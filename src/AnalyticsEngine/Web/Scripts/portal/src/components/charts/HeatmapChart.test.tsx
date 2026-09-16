import { describe, it, expect } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import HeatmapChart from './HeatmapChart';

describe('HeatmapChart', () => {
  it('says so when there is nothing to draw', () => {
    renderWithProvider(<HeatmapChart cells={[]} valueLabel="calls" />);
    expect(screen.getByText('No data for this period.')).toBeInTheDocument();
  });

  it('treats an all-zero grid as no data rather than drawing an empty grid', () => {
    renderWithProvider(
      <HeatmapChart cells={[{ dayOfWeek: 0, hour: 9, value: 0 }]} valueLabel="calls" />,
    );
    expect(screen.getByText('No data for this period.')).toBeInTheDocument();
  });

  it('renders a full week of hourly cells with readable tooltips', () => {
    renderWithProvider(
      <HeatmapChart
        cells={[
          { dayOfWeek: 0, hour: 9, value: 12 },
          { dayOfWeek: 4, hour: 16, value: 3 },
        ]}
        valueLabel="calls"
      />,
    );

    // 7 days x 24 hours.
    expect(screen.getByTitle('Mon 09:00 - 12 calls')).toBeInTheDocument();
    expect(screen.getByTitle('Fri 16:00 - 3 calls')).toBeInTheDocument();
    expect(screen.getByTitle('Sun 00:00 - 0 calls')).toBeInTheDocument();
  });

  it('adds together several cells for the same slot', () => {
    renderWithProvider(
      <HeatmapChart
        cells={[
          { dayOfWeek: 2, hour: 11, value: 4 },
          { dayOfWeek: 2, hour: 11, value: 6 },
        ]}
        valueLabel="calls"
      />,
    );

    expect(screen.getByTitle('Wed 11:00 - 10 calls')).toBeInTheDocument();
  });

  it('ignores cells outside the grid instead of throwing', () => {
    // The day index comes from SQL arithmetic; a bad value should not take the tab down.
    renderWithProvider(
      <HeatmapChart
        cells={[
          { dayOfWeek: 9, hour: 9, value: 5 },
          { dayOfWeek: 0, hour: 99, value: 5 },
          { dayOfWeek: 1, hour: 8, value: 7 },
        ]}
        valueLabel="calls"
      />,
    );

    expect(screen.getByTitle('Tue 08:00 - 7 calls')).toBeInTheDocument();
  });

  it('shows the footnote when one is supplied', () => {
    renderWithProvider(
      <HeatmapChart
        cells={[{ dayOfWeek: 0, hour: 9, value: 1 }]}
        valueLabel="calls"
        footnote="Assumed working day: 08:00-18:00 UTC."
      />,
    );

    expect(screen.getByText('Assumed working day: 08:00-18:00 UTC.')).toBeInTheDocument();
  });
});
