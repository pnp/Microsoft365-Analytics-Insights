import { describe, it, expect } from 'vitest';
import {
  DASH,
  UNKNOWN_TEXT,
  daysAgo,
  formatAge,
  formatCount,
  formatDate,
  formatMaybeCount,
  formatPct,
  ratioPct,
} from './format';

describe('format helpers', () => {
  it('formats whole counts with thousands separators', () => {
    expect(formatCount(12345)).toBe('12,345');
    expect(formatCount(0)).toBe('0');
  });

  describe('unknown is not zero', () => {
    it('renders null as the unknown marker, not 0', () => {
      expect(formatMaybeCount(null)).toBe(UNKNOWN_TEXT);
      expect(formatMaybeCount(undefined)).toBe(UNKNOWN_TEXT);
    });

    it('renders a measured zero as 0, not unknown', () => {
      expect(formatMaybeCount(0)).toBe('0');
    });

    it('renders a real count normally', () => {
      expect(formatMaybeCount(42)).toBe('42');
    });

    it('ratioPct returns null (not 0) when an operand is unknown or the denominator is 0', () => {
      expect(ratioPct(null, 100)).toBeNull();
      expect(ratioPct(10, null)).toBeNull();
      expect(ratioPct(10, 0)).toBeNull();
      expect(ratioPct(0, 100)).toBe(0); // a measured 0% is a real value
      expect(ratioPct(50, 200)).toBe(25);
    });
  });

  it('formats percentages, dropping a trailing .0', () => {
    expect(formatPct(12)).toBe('12%');
    expect(formatPct(12.34)).toBe('12.3%');
  });

  it('formats or dashes dates', () => {
    expect(formatDate(null)).toBe(DASH);
    expect(formatDate('not-a-date')).toBe(DASH);
    expect(formatDate('2026-05-20T00:00:00Z')).toMatch(/2026/);
  });

  it('computes whole days ago and a friendly age, treating unknown as unknown', () => {
    const now = new Date('2026-05-20T12:00:00Z');
    expect(daysAgo(null, now)).toBeNull();
    expect(formatAge(null, now)).toBe(UNKNOWN_TEXT);
    expect(formatAge('2026-05-20T00:00:00Z', now)).toBe('today');
    expect(formatAge('2026-05-19T00:00:00Z', now)).toBe('yesterday');
    expect(formatAge('2026-05-10T00:00:00Z', now)).toBe('10 days ago');
  });
});
