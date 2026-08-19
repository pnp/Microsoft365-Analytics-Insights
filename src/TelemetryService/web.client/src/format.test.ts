import { describe, expect, it, vi, afterEach, beforeEach } from 'vitest';
import { formatMB, formatNumber, formatPercent, formatRelative } from './format';

describe('formatNumber', () => {
    it('renders an em dash for missing values rather than "null"', () => {
        expect(formatNumber(null)).toBe('—');
        expect(formatNumber(undefined)).toBe('—');
    });

    it('renders zero as a number, not as missing', () => {
        expect(formatNumber(0)).toBe('0');
    });

    it('groups thousands', () => {
        // Locale-dependent separator, so assert on the digits rather than the exact string.
        expect(formatNumber(1234567).replace(/\D/g, '')).toBe('1234567');
        expect(formatNumber(1234567)).not.toBe('1234567');
    });
});

describe('formatMB', () => {
    it('renders missing values as an em dash', () => {
        expect(formatMB(null)).toBe('—');
        expect(formatMB(undefined)).toBe('—');
    });

    it('keeps small values in MB', () => {
        expect(formatMB(0)).toBe('0.00 MB');
        expect(formatMB(512)).toBe('512.00 MB');
        expect(formatMB(1023.99)).toBe('1023.99 MB');
    });

    it('scales to GB at 1024 MB', () => {
        expect(formatMB(1024)).toBe('1.00 GB');
        expect(formatMB(391000)).toBe('381.84 GB');
    });

    it('scales to TB so a large install base stays readable', () => {
        expect(formatMB(1024 * 1024)).toBe('1.00 TB');
        expect(formatMB(5 * 1024 * 1024)).toBe('5.00 TB');
    });
});

describe('formatPercent', () => {
    it('avoids dividing by zero', () => {
        expect(formatPercent(5, 0)).toBe('0%');
    });

    it('rounds to whole percentages', () => {
        expect(formatPercent(1, 3)).toBe('33%');
        expect(formatPercent(2, 3)).toBe('67%');
        expect(formatPercent(3, 3)).toBe('100%');
    });
});

describe('formatRelative', () => {
    const now = new Date('2026-08-19T12:00:00Z');

    beforeEach(() => {
        vi.useFakeTimers();
        vi.setSystemTime(now);
    });

    afterEach(() => {
        vi.useRealTimers();
    });

    it('describes a missing timestamp as never', () => {
        expect(formatRelative(null)).toBe('never');
        expect(formatRelative(undefined)).toBe('never');
    });

    it('returns the raw value when it is not a date', () => {
        expect(formatRelative('not-a-date')).toBe('not-a-date');
    });

    it('collapses very recent times', () => {
        expect(formatRelative('2026-08-19T11:59:30Z')).toBe('just now');
    });

    it('singularises correctly', () => {
        expect(formatRelative('2026-08-19T11:00:00Z')).toBe('1 hour ago');
        expect(formatRelative('2026-08-19T10:00:00Z')).toBe('2 hours ago');
    });

    it('steps up through minutes, hours and days', () => {
        expect(formatRelative('2026-08-19T11:55:00Z')).toBe('5 minutes ago');
        expect(formatRelative('2026-08-17T12:00:00Z')).toBe('2 days ago');
    });

    it('describes long-stale clients in months', () => {
        expect(formatRelative('2026-06-19T12:00:00Z')).toBe('2 months ago');
    });
});
