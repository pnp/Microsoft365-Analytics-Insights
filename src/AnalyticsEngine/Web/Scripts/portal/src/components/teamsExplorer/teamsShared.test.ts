import { describe, it, expect } from 'vitest';
import {
  NEEDS_ATTENTION_UPPER_PCT,
  PROGRESSING_UPPER_PCT,
  bucketsToCategories,
  formatCount,
  formatDate,
  formatDecimal,
  formatHours,
  formatPct,
  formatSentiment,
  queryFor,
  reachTone,
  sentimentLabel,
  toCategories,
} from './teamsShared';

describe('adoption bands', () => {
  it('uses the same boundaries as the server and the gauge', () => {
    // These three must agree or the page states a figure is "healthy" beside a red card.
    // The server side is pinned in TeamsExplorerScoringTests.BandsMatchTheGaugeScaleUsedByTheUi,
    // and GaugeRing's ADOPTION_BANDS carry the same numbers.
    expect(NEEDS_ATTENTION_UPPER_PCT).toBe(40);
    expect(PROGRESSING_UPPER_PCT).toBe(70);
  });

  it('maps a percentage to the matching tone at every boundary', () => {
    expect(reachTone(0)).toBe('critical');
    expect(reachTone(39.9)).toBe('critical');
    expect(reachTone(40)).toBe('warning');
    expect(reachTone(69.9)).toBe('warning');
    expect(reachTone(70)).toBe('good');
    expect(reachTone(100)).toBe('good');
  });
});

describe('sentiment', () => {
  it('treats 0.5 as neutral rather than as "50% positive"', () => {
    // The stored score is a weighted mean of a ternary score, so half way along the scale is
    // neutral. Rendering it as a percentage is the single easiest way to misread this page.
    expect(sentimentLabel(0.5)).toBe('neutral');
    expect(formatSentiment(0.5)).toBe('0.50 (neutral)');
    expect(formatSentiment(0.5)).not.toContain('%');
  });

  it('labels each end of the scale', () => {
    expect(sentimentLabel(0)).toBe('negative');
    expect(sentimentLabel(0.4)).toBe('leaning negative');
    expect(sentimentLabel(0.6)).toBe('leaning positive');
    expect(sentimentLabel(1)).toBe('positive');
  });

  it('renders an unscored value as a dash rather than as zero', () => {
    // Zero is the most NEGATIVE possible score, so showing it for "not scored" would invert the
    // meaning of an empty cell.
    expect(formatSentiment(null)).toBe('\u2014');
    expect(formatSentiment(undefined)).toBe('\u2014');
  });
});

describe('formatting', () => {
  it('rounds counts and keeps one decimal on rates', () => {
    expect(formatCount(1234.6)).toBe((1235).toLocaleString());
    expect(formatDecimal(3)).toBe('3');
    expect(formatDecimal(3.14)).toBe('3.1');
    expect(formatPct(12.34)).toBe('12.3%');
    expect(formatPct(50)).toBe('50%');
  });

  it('drops the decimal on large hour figures', () => {
    expect(formatHours(4.25)).toBe('4.3 h');
    expect(formatHours(1234.5)).toBe(`${(1235).toLocaleString()} h`);
  });

  it('formats dates in UTC so a Monday does not render as the previous Sunday', () => {
    const rendered = formatDate('2026-03-02T00:00:00Z');
    expect(rendered).toContain('2026');
    expect(rendered).toContain('2');
    expect(formatDate(null)).toBe('\u2014');
  });
});

describe('chart adapters', () => {
  it('maps named counts to categories', () => {
    expect(toCategories([{ name: 'General', count: 12, sharePct: 40 }])).toEqual([
      { label: 'General', value: 12 },
    ]);
  });

  it('preserves bucket order so a distribution is not re-sorted by size', () => {
    const buckets = [
      { key: '1', label: '1', count: 0, sharePct: 0 },
      { key: '2', label: '2', count: 9, sharePct: 90 },
      { key: '3-5', label: '3-5', count: 1, sharePct: 10 },
    ];

    expect(bucketsToCategories(buckets).map((c) => c.label)).toEqual(['1', '2', '3-5']);
  });
});

describe('query lookup', () => {
  const queries = [
    { key: 'overview-usage', sql: 'SELECT 1', error: null, elapsedMs: 5 },
    { key: 'overview-calls', sql: 'SELECT 2', error: 'Timeout expired.', elapsedMs: 25000 },
  ];

  it('finds a section by key and returns undefined for an unknown one', () => {
    expect(queryFor(queries, 'overview-calls')?.error).toBe('Timeout expired.');
    expect(queryFor(queries, 'not-a-section')).toBeUndefined();
  });
});
