import { describe, it, expect } from 'vitest';
import {
  addDays,
  diffDaysInclusive,
  isValidDateString,
  latestEndString,
  matchPreset,
  parseDateString,
  presetRange,
  settledEndString,
  todayString,
  validateRange,
} from './dateRange';

// A fixed "now" at midday UTC so the UTC-based date maths is deterministic regardless of the
// machine's timezone. Assertions about the settled-Sunday end are property-based (it's a Sunday,
// at least 3 days old) rather than hard-coded, so they hold whatever weekday `now` falls on.
const NOW = new Date(Date.UTC(2026, 4, 20, 12)); // 2026-05-20T12:00Z

function utcDay(dateString: string): number {
  return parseDateString(dateString)!.getUTCDay(); // 0 = Sunday, 1 = Monday
}

describe('dateRange helpers', () => {
  it('reports today, yesterday (custom ceiling) and rejects impossible dates', () => {
    expect(todayString(NOW)).toBe('2026-05-20');
    expect(latestEndString(NOW)).toBe('2026-05-19'); // custom `to` ceiling stays yesterday
    expect(isValidDateString('2026-02-31')).toBe(false);
    expect(isValidDateString('2026-05-20')).toBe(true);
  });

  it('addDays returns invalid input unchanged instead of falling back to today', () => {
    expect(addDays('2026-05-19', -6)).toBe('2026-05-13');
    expect(addDays('not-a-date', 5)).toBe('not-a-date');
    expect(addDays('', -1)).toBe('');
  });

  it('settles to the latest Sunday that is at least 3 days old', () => {
    const end = settledEndString(NOW);
    expect(utcDay(end)).toBe(0); // a Sunday
    expect(end <= addDays(todayString(NOW), -3)).toBe(true); // on/before today-3
  });

  it('builds presets that all end on the settled Sunday with exact spans (90/180 not rounded)', () => {
    const end = settledEndString(NOW);

    for (const days of [7, 28, 90, 180] as const) {
      const range = presetRange(days, NOW);
      expect(range.to).toBe(end); // every preset ends on the same settled Sunday
      expect(diffDaysInclusive(range.from, range.to)).toBe(days); // exact span, never rounded to weeks
    }

    // The 7- and 28-day presets are whole settled weeks (start on a Monday); 90/180 need not be.
    expect(utcDay(presetRange(7, NOW).from)).toBe(1);
    expect(utcDay(presetRange(28, NOW).from)).toBe(1);
  });

  it('recognises a preset and returns null for a genuine custom range', () => {
    expect(matchPreset(presetRange(90, NOW), NOW)).toBe(90);
    expect(matchPreset({ from: '2026-01-01', to: '2026-03-15' }, NOW)).toBeNull();
  });
});

describe('validateRange (custom stays exact: 7..180, ending before today)', () => {
  it('accepts a valid window', () => {
    expect(validateRange({ from: '2026-05-01', to: '2026-05-19' }, { now: NOW })).toEqual({ ok: true });
  });

  it('rejects an inverted range', () => {
    expect(validateRange({ from: '2026-05-19', to: '2026-05-01' }, { now: NOW })).toMatchObject({
      ok: false,
      error: expect.stringMatching(/on or before/i),
    });
  });

  it('rejects an end date that is today or later', () => {
    expect(validateRange({ from: '2026-05-13', to: '2026-05-20' }, { now: NOW })).toMatchObject({
      ok: false,
      error: expect.stringMatching(/before today/i),
    });
  });

  it('rejects a range shorter than the minimum', () => {
    expect(validateRange({ from: '2026-05-17', to: '2026-05-19' }, { now: NOW, minDays: 7 })).toMatchObject({
      ok: false,
      error: expect.stringMatching(/at least 7 days/i),
    });
  });

  it('rejects a start date before the backend minimum (1753-01-01)', () => {
    expect(validateRange({ from: '1752-12-31', to: '1753-02-01' }, { now: NOW })).toMatchObject({
      ok: false,
      error: expect.stringMatching(/1753-01-01/),
    });
  });

  it('rejects a range longer than the maximum', () => {
    expect(validateRange({ from: '2025-05-19', to: '2026-05-19' }, { now: NOW, maxDays: 180 })).toMatchObject({
      ok: false,
      error: expect.stringMatching(/longer than 180 days/i),
    });
  });
});
