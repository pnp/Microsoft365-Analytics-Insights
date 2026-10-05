import { useState } from 'react';
import { describe, it, expect, vi } from 'vitest';
import { screen, fireEvent, within } from '@testing-library/react';
import { renderWithProvider } from '../../test/renderWithProvider';
import AdoptionPeriodControl, {
  lastCalendarMonthRange,
  lastCalendarQuarterRange,
  periodLabel,
  type FixedPeriod,
} from './AdoptionPeriodControl';
import { translateActive } from '../../i18n/runtime';

const NOW = new Date(Date.UTC(2026, 4, 20, 12)); // 2026-05-20; latest allowed end = 2026-05-19

/** The control as the page holds it: a rolling window, or a fixed period that replaces it. */
function Harness({
  onRolling = vi.fn(),
  onFixed = vi.fn(),
  initialFixed = null,
}: {
  onRolling?: (days: number) => void;
  onFixed?: (period: FixedPeriod) => void;
  initialFixed?: FixedPeriod | null;
}) {
  const [windowDays, setWindowDays] = useState(28);
  const [fixed, setFixed] = useState<FixedPeriod | null>(initialFixed);
  return (
    <AdoptionPeriodControl
      windowDays={windowDays}
      fixed={fixed}
      now={NOW}
      onRollingChange={(days) => {
        onRolling(days);
        setWindowDays(days);
        setFixed(null);
      }}
      onFixedChange={(period) => {
        onFixed(period);
        setFixed(period);
      }}
    />
  );
}

const period = () => screen.getByLabelText('Reporting period');

describe('AdoptionPeriodControl', () => {
  it('is one drop-down, grouped into periods up to today and past periods, and nothing else', () => {
    renderWithProvider(<Harness />);

    expect(period()).toHaveValue('28');
    const groups = within(period()).getAllByRole('group');
    expect(groups.map((g) => g.getAttribute('label'))).toEqual(['Up to today', 'Past periods']);
    expect(within(groups[0]).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Last 7 days',
      'Last 28 days',
      'Last 90 days',
      'Last 180 days',
    ]);
    expect(within(groups[1]).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'Last calendar month',
      'Last calendar quarter',
      'Custom range',
    ]);
    expect(screen.queryAllByRole('button')).toHaveLength(0);
    expect(screen.queryByLabelText('From')).toBeNull();
  });

  it('reports a rolling window as days', () => {
    const onRolling = vi.fn();
    renderWithProvider(<Harness onRolling={onRolling} />);

    fireEvent.change(period(), { target: { value: '90' } });

    expect(onRolling).toHaveBeenCalledWith(90);
    expect(period()).toHaveValue('90');
  });

  it('pins a calendar period to its dates and states them beside the drop-down', () => {
    const onFixed = vi.fn();
    renderWithProvider(<Harness onFixed={onFixed} />);

    fireEvent.change(period(), { target: { value: 'lastMonth' } });
    expect(onFixed).toHaveBeenLastCalledWith({ kind: 'lastMonth', range: { from: '2026-04-01', to: '2026-04-30' } });
    expect(screen.getByText(/^1 Apr 2026 \u2013 30 Apr 2026$/)).toBeVisible();

    fireEvent.change(period(), { target: { value: 'lastQuarter' } });
    expect(onFixed).toHaveBeenLastCalledWith({ kind: 'lastQuarter', range: { from: '2026-01-01', to: '2026-03-31' } });
    expect(screen.getByText(/^1 Jan 2026 \u2013 31 Mar 2026$/)).toBeVisible();
    expect(screen.queryByLabelText('From')).toBeNull();
  });

  it('opens the date fields on the period in effect, and changes nothing until Apply', () => {
    const onFixed = vi.fn();
    const onRolling = vi.fn();
    renderWithProvider(<Harness onFixed={onFixed} onRolling={onRolling} />);

    fireEvent.change(period(), { target: { value: 'custom' } });

    expect(period()).toHaveValue('custom');
    // The 28-day window, pinned to dates: it ends yesterday.
    expect(screen.getByLabelText('From')).toHaveValue('2026-04-22');
    expect(screen.getByLabelText('To')).toHaveValue('2026-05-19');
    expect(onFixed).not.toHaveBeenCalled();
    expect(onRolling).not.toHaveBeenCalled();

    fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-03-02' } });
    fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-03-29' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));

    expect(onFixed).toHaveBeenCalledWith({ kind: 'custom', range: { from: '2026-03-02', to: '2026-03-29' } });
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();

    // Editing a date again offers Apply again.
    fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-03-30' } });
    expect(screen.getByRole('button', { name: 'Apply' })).toBeEnabled();
  });

  it('starts a custom range from the calendar period in effect', () => {
    renderWithProvider(<Harness />);

    fireEvent.change(period(), { target: { value: 'lastQuarter' } });
    fireEvent.change(period(), { target: { value: 'custom' } });

    expect(screen.getByLabelText('From')).toHaveValue('2026-01-01');
    expect(screen.getByLabelText('To')).toHaveValue('2026-03-31');
  });

  it('refuses a range the server would refuse, saying why', () => {
    const onFixed = vi.fn();
    renderWithProvider(<Harness onFixed={onFixed} />);
    fireEvent.change(period(), { target: { value: 'custom' } });

    fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-05-20' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect(screen.getByRole('alert')).toHaveTextContent(/before today/i);

    fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-05-17' } });
    fireEvent.change(screen.getByLabelText('To'), { target: { value: '2026-05-19' } });
    fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
    expect(screen.getByRole('alert')).toHaveTextContent(/at least 7 days/i);

    expect(onFixed).not.toHaveBeenCalled();
  });

  it('closes the date fields when another period is chosen instead', () => {
    const onRolling = vi.fn();
    renderWithProvider(<Harness onRolling={onRolling} />);

    fireEvent.change(period(), { target: { value: 'custom' } });
    fireEvent.change(period(), { target: { value: '28' } });

    expect(period()).toHaveValue('28');
    expect(screen.queryByLabelText('From')).toBeNull();
    expect(onRolling).toHaveBeenCalledWith(28);
  });

  it('shows an applied custom range in its fields', () => {
    renderWithProvider(<Harness initialFixed={{ kind: 'custom', range: { from: '2026-02-01', to: '2026-02-10' } }} />);

    expect(period()).toHaveValue('custom');
    expect(screen.getByLabelText('From')).toHaveValue('2026-02-01');
    expect(screen.getByLabelText('To')).toHaveValue('2026-02-10');
    expect(screen.getByRole('button', { name: 'Apply' })).toBeDisabled();
  });

  it('names each period as the printout states it', () => {
    const range = { from: '2026-04-01', to: '2026-04-30' };
    expect(periodLabel(translateActive, 28, null)).toBe('Last 28 days');
    expect(periodLabel(translateActive, 45, null)).toBe('Last 45 days');
    expect(periodLabel(translateActive, 28, { kind: 'lastMonth', range })).toBe('Last calendar month');
    expect(periodLabel(translateActive, 28, { kind: 'lastQuarter', range })).toBe('Last calendar quarter');
    expect(periodLabel(translateActive, 28, { kind: 'custom', range })).toBe('Custom range');
  });

  it('calculates the calendar periods in UTC, across year ends', () => {
    expect(lastCalendarMonthRange(new Date(Date.UTC(2026, 0, 1, 0, 30)))).toEqual({ from: '2025-12-01', to: '2025-12-31' });
    expect(lastCalendarQuarterRange(new Date(Date.UTC(2026, 3, 1)))).toEqual({ from: '2026-01-01', to: '2026-03-31' });
    expect(lastCalendarQuarterRange(new Date(Date.UTC(2026, 11, 31)))).toEqual({ from: '2026-07-01', to: '2026-09-30' });
  });
});
