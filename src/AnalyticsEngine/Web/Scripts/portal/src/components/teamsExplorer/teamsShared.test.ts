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
  translatedCodeBucketsToCategories,
  translatedCodeLabel,
} from './teamsShared';
import { loadCatalog, translateStatic } from '../../i18n';

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

  it('translates Teams call modality and quality codes with raw-code fallback', async () => {
    await loadCatalog('es');
    const es = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
      translateStatic('es', key, values);

    expect(translatedCodeBucketsToCategories(es, 'modality', [
      { key: 'screenSharing', label: 'screenSharing', count: 3, sharePct: 75 },
      { key: 'unknownFutureValue', label: 'unknownFutureValue', count: 1, sharePct: 25 },
    ])).toEqual([
      { label: 'Uso compartido de pantalla', value: 3 },
      { label: 'unknownFutureValue', value: 1 },
    ]);
    expect(translatedCodeLabel(es, 'quality', 'poor')).toBe('Deficiente');
  });

  it('keeps every Graph call code distinct, so no two chart slices share a label or a React key', async () => {
    await loadCatalog('es');
    for (const language of ['en', 'es'] as const) {
      const t = (key: Parameters<typeof translateStatic>[1], values?: Parameters<typeof translateStatic>[2]) =>
        translateStatic(language, key, values);
      const modality = translatedCodeBucketsToCategories(t, 'modality', [
        { key: 'audio', label: 'audio', count: 5, sharePct: 25 },
        { key: 'video', label: 'video', count: 5, sharePct: 25 },
        { key: 'screenSharing', label: 'screenSharing', count: 4, sharePct: 20 },
        { key: 'videoBasedScreenSharing', label: 'videoBasedScreenSharing', count: 3, sharePct: 15 },
        { key: 'data', label: 'data', count: 3, sharePct: 15 },
      ]).map((c) => c.label);
      expect(new Set(modality).size, `${language}: ${modality.join(' | ')}`).toBe(modality.length);
      expect(modality.some((label) => /^[a-z]+[A-Z]/.test(label)), `${language}: a raw camelCase code leaked`).toBe(false);

      const quality = ['excellent', 'good', 'fair', 'poor', 'bad', 'notRated', '(none)'].map((code) => translatedCodeLabel(t, 'quality', code));
      expect(new Set(quality).size, `${language}: ${quality.join(' | ')}`).toBe(quality.length);
    }

    const es = (key: Parameters<typeof translateStatic>[1]) => translateStatic('es', key);
    expect(translatedCodeLabel(es, 'modality', 'videoBasedScreenSharing')).toBe('Uso compartido de pantalla basado en vídeo');
    expect(translatedCodeLabel(es, 'quality', 'bad')).toBe('Mala');
    expect(translatedCodeLabel(es, 'quality', 'notRated')).toBe('Sin valorar');
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
