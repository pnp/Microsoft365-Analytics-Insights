import { describe, it, expect } from 'vitest';
import {
  HEALTHY_BOUNCE_PCT,
  HIGH_BOUNCE_PCT,
  HIGH_SEARCH_RELIANCE_PCT,
  FAST_LOAD_SECONDS,
  GOOD_REACH_PCT,
  LOW_REACH_PCT,
  SLOW_LOAD_SECONDS,
  bounceTone,
  formatDuration,
  formatHour,
  formatPct,
  formatSeconds,
  judgementIntent,
  loadTone,
  queryFor,
  reachTone,
  reachToneOrNeutral,
  searchRelianceTone,
  shortenUrl,
  toStackedSeries,
  withRemainder,
} from './webActivityShared';
import type { WebActivityQueryInfo, WebActivityStackPoint } from '../../types/webActivity';

describe('webActivityShared thresholds', () => {
  // These MUST match WebActivityScoring on the server. The server decides the tone of its own
  // judgements and the client decides the tone of the KPI cards; if they drift, the page states a
  // figure is healthy next to a red card and nothing else on it can be trusted.
  //
  // This pins the CLIENT half only - nothing here can see the C# constants, so it catches an
  // accidental edit to these literals, not server drift. Changing a boundary means changing it in
  // WebActivityScoring.cs, here, and in this test, deliberately.
  it('pins the client band boundaries against accidental edits', () => {
    expect(HIGH_BOUNCE_PCT).toBe(60);
    expect(HEALTHY_BOUNCE_PCT).toBe(40);
    expect(LOW_REACH_PCT).toBe(25);
    expect(GOOD_REACH_PCT).toBe(60);
    expect(SLOW_LOAD_SECONDS).toBe(3.0);
    expect(FAST_LOAD_SECONDS).toBe(1.5);
    expect(HIGH_SEARCH_RELIANCE_PCT).toBe(35);
  });

  it('colours reach, bounce and load in the right direction', () => {
    // Reach: higher is better.
    expect(reachTone(10)).toBe('critical');
    expect(reachTone(45)).toBe('warning');
    expect(reachTone(80)).toBe('good');

    // An unmeasurable reach is neutral, not critical - no denominator is not a bad result.
    expect(reachToneOrNeutral(null)).toBe('neutral');
    expect(reachToneOrNeutral(10)).toBe('critical');

    // Bounce: lower is better, and the boundary value belongs to the healthier band.
    expect(bounceTone(70)).toBe('critical');
    expect(bounceTone(HIGH_BOUNCE_PCT)).toBe('critical');
    expect(bounceTone(50)).toBe('warning');
    expect(bounceTone(HEALTHY_BOUNCE_PCT)).toBe('good');

    // Load: lower is better, and unmeasured is NOT instant. A missing load time rendered as a good
    // green card would tell an admin their slowest pages are fine.
    expect(loadTone(null)).toBe('neutral');
    expect(loadTone(undefined)).toBe('neutral');
    expect(loadTone(0)).toBe('neutral');
    expect(loadTone(0.9)).toBe('good');
    expect(loadTone(2)).toBe('warning');
    expect(loadTone(5)).toBe('critical');
  });

  it('never marks search reliance as critical', () => {
    // High search reliance is a navigation finding, not a fault. Colouring it red beside genuinely
    // broken figures invites an intranet team to "fix" something that may be entirely healthy.
    expect(searchRelianceTone(10)).toBe('neutral');
    expect(searchRelianceTone(90)).toBe('warning');
  });

  it('maps server judgement tones onto message-bar intents', () => {
    expect(judgementIntent('good')).toBe('success');
    expect(judgementIntent('neutral')).toBe('info');
    expect(judgementIntent('warning')).toBe('warning');
    expect(judgementIntent('critical')).toBe('error');
  });
});

describe('webActivityShared formatting', () => {
  it('formats durations, percentages, seconds and hours, and shows a dash for absent values', () => {
    expect(formatDuration(42.4)).toBe('42.4s');
    expect(formatDuration(125)).toBe('2m 05s');
    expect(formatDuration(null)).toBe('\u2014');

    expect(formatPct(33.33)).toBe('33.3%');
    expect(formatPct(50)).toBe('50%');
    expect(formatPct(undefined)).toBe('\u2014');

    // Page loads are small numbers, so two decimals are the readable choice - one decimal turns
    // 0.94s and 0.86s into the same figure.
    expect(formatSeconds(0.941)).toBe('0.94s');
    expect(formatSeconds(null)).toBe('\u2014');

    expect(formatHour(9)).toBe('09:00');
    expect(formatHour(0)).toBe('00:00');
    expect(formatHour(null)).toBe('\u2014');
  });

  it('shortens a URL to its path and leaves an unparseable one alone', () => {
    expect(shortenUrl('https://contoso.sharepoint.com/sites/intranet/SitePages/Home.aspx'))
      .toBe('/sites/intranet/SitePages/Home.aspx');
    expect(shortenUrl('not a url')).toBe('not a url');
    expect(shortenUrl(null)).toBe('\u2014');
  });

  it('preserves a non-Latin path rather than mangling it', () => {
    // SharePoint page names are genuinely non-Latin in plenty of tenants. `URL.pathname` returns the
    // percent-ENCODED path, so without decoding this renders as "%CE%9A%CE%B1%CE%BB..." - which in a
    // table of page URLs is indistinguishable from a database encoding bug, and is exactly what an
    // admin would raise as one.
    const greek = 'https://contoso.sharepoint.com/sites/intranet/SitePages/Καλημέρα κόσμε/Home.aspx';
    expect(shortenUrl(greek)).toContain('Καλημέρα');

    // A malformed escape sequence must fall back to the raw path, not throw and blank the cell.
    expect(shortenUrl('https://contoso.sharepoint.com/sites/%E0%A4%A')).toContain('%E0%A4%A');
  });
});

describe('toStackedSeries', () => {
  it('pads every series to the full set of weeks', () => {
    // A stacked chart whose bands sit on different x positions does not stack, it interleaves - and
    // the resulting picture is wrong in a way that looks entirely plausible.
    const points: WebActivityStackPoint[] = [
      { weekStart: '2026-03-02T00:00:00Z', name: 'HR', count: 10 },
      { weekStart: '2026-03-09T00:00:00Z', name: 'HR', count: 12 },
      { weekStart: '2026-03-09T00:00:00Z', name: 'IT', count: 4 },
    ];

    const series = toStackedSeries(points);

    expect(series).toHaveLength(2);
    for (const line of series) {
      expect(line.points.map((p) => p.weekStart)).toEqual([
        '2026-03-02T00:00:00Z',
        '2026-03-09T00:00:00Z',
      ]);
    }

    const it = series.find((s) => s.name === 'IT');
    expect(it?.points[0].value).toBe(0);
    expect(it?.points[1].value).toBe(4);
  });

  it('returns nothing for no points', () => {
    expect(toStackedSeries([])).toEqual([]);
  });
});

describe('queryFor', () => {
  it('finds a section by key and returns undefined for an unknown one', () => {
    const queries: WebActivityQueryInfo[] = [
      { key: 'overview-kpis', sql: 'SELECT 1', error: null, elapsedMs: 5 },
    ];

    expect(queryFor(queries, 'overview-kpis')?.sql).toBe('SELECT 1');
    expect(queryFor(queries, 'nope')).toBeUndefined();
  });
});

describe('withRemainder', () => {
  it('adds the traffic a truncated list does not account for', () => {
    // Every ranked list is capped at the top N. A donut built from only those rows normalises to
    // them and always totals 100%, so a tenant with 40 countries would see 15 of them silently
    // inflated to cover everything.
    const listed = [
      { label: 'United Kingdom', value: 600 },
      { label: 'Ireland', value: 200 },
    ];

    const withOther = withRemainder(listed, 1000, 'Other countries');
    expect(withOther).toHaveLength(3);
    expect(withOther[2]).toEqual({ label: 'Other countries', value: 200 });
  });

  it('adds nothing when the list already accounts for the whole total', () => {
    const listed = [{ label: 'United Kingdom', value: 1000 }];
    expect(withRemainder(listed, 1000, 'Other')).toHaveLength(1);
  });

  it('adds nothing when the total does not belong to the list', () => {
    // A negative remainder means the caller passed a denominator from a different population.
    // Drawing it would produce a nonsensical slice, so the list has to be returned untouched.
    const listed = [{ label: 'United Kingdom', value: 1000 }];
    expect(withRemainder(listed, 400, 'Other')).toHaveLength(1);
  });
});
