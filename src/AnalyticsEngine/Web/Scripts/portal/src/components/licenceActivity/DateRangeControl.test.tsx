import { describe, it, expect, vi } from 'vitest';
import { screen, fireEvent } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import DateRangeControl from './DateRangeControl';
import { presetRange } from './dateRange';

const NOW = new Date(Date.UTC(2026, 4, 20, 12)); // 2026-05-20; latest allowed end = 2026-05-19

describe('DateRangeControl', () => {
  it('emits the matching range when a preset is clicked', () => {
    const onChange = vi.fn();
    renderWithProvider(
      <DateRangeControl value={{ from: '2026-01-01', to: '2026-03-01' }} onChange={onChange} now={NOW} />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Last settled week' }));
    expect(onChange).toHaveBeenCalledWith(presetRange(7, NOW));
  });

  it('marks the active preset as pressed', () => {
    renderWithProvider(<DateRangeControl value={presetRange(28, NOW)} onChange={vi.fn()} now={NOW} />);
    expect(screen.getByRole('button', { name: 'Last 4 fully settled weeks' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Last settled week' })).toHaveAttribute('aria-pressed', 'false');
  });

  it('validates a custom range: rejects inverted / today / too-short, accepts a valid one', () => {
    const onChange = vi.fn();
    renderWithProvider(
      <DateRangeControl value={presetRange(28, NOW)} onChange={onChange} now={NOW} minDays={7} maxDays={180} />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Custom range' }));
    const from = screen.getByLabelText('Start date');
    const to = screen.getByLabelText('End date');

    // Inverted.
    fireEvent.change(from, { target: { value: '2026-05-10' } });
    fireEvent.change(to, { target: { value: '2026-05-01' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect(screen.getByRole('alert')).toHaveTextContent(/on or before/i);
    expect(onChange).not.toHaveBeenCalled();

    // Ends today (not allowed - reporting covers whole past days).
    fireEvent.change(to, { target: { value: '2026-05-20' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect(screen.getByRole('alert')).toHaveTextContent(/before today/i);
    expect(onChange).not.toHaveBeenCalled();

    // Too short (3 days < 7).
    fireEvent.change(to, { target: { value: '2026-05-12' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect(screen.getByRole('alert')).toHaveTextContent(/at least 7 days/i);
    expect(onChange).not.toHaveBeenCalled();

    // Valid (7 days, ends before today).
    fireEvent.change(to, { target: { value: '2026-05-16' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect(onChange).toHaveBeenCalledWith({ from: '2026-05-10', to: '2026-05-16' });
  });

  it('opens the custom editor reflecting the current range when it is not a preset', () => {
    renderWithProvider(
      <DateRangeControl value={{ from: '2026-02-01', to: '2026-02-10' }} onChange={vi.fn()} now={NOW} />,
    );
    expect(screen.getByLabelText('Start date')).toHaveValue('2026-02-01');
    expect(screen.getByLabelText('End date')).toHaveValue('2026-02-10');
  });
});
