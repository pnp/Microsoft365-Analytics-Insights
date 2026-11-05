import { describe, expect, it } from 'vitest';
import { inTimeZone } from '../test/timeZone';
import { parseApiDate } from './apiDate';
import { formatDay, formatDateTime } from '../components/agentCosts/agentCostShared';
import { formatWeek, formatWeekLong } from '../components/charts/chartCommon';
import { formatUtc } from '../components/health/healthShared';
import { formatDate as formatLicenceDate } from '../components/licenceActivity/format';
import { formatDate as formatKpiDate } from '../components/shared/KpiGrid';
import { formatDate as formatTeamsDate } from '../components/teamsExplorer/teamsShared';
import { formatDetailTimestamp } from '../components/userlookup/CategoryRow';

/**
 * The API sends a `DateTime` read from SQL with no offset. East of UTC, reading one as local time -
 * what `new Date(string)` does - lands on the previous UTC day; west of UTC, on the right day but the
 * wrong instant.
 */
const EAST = 'Europe/Madrid';
const WEST = 'America/New_York';

describe('parseApiDate', () => {
  it('reads a date-time with no offset as UTC, east and west of UTC', () => {
    for (const zone of [EAST, WEST]) {
      inTimeZone(zone, () => {
        expect(parseApiDate('2026-10-19T00:00:00').toISOString()).toBe('2026-10-19T00:00:00.000Z');
        // .NET writes seven fractional digits.
        expect(parseApiDate('2026-10-19T23:30:00.1234567').toISOString()).toBe('2026-10-19T23:30:00.123Z');
        expect(parseApiDate('2026-10-19T08:15').toISOString()).toBe('2026-10-19T08:15:00.000Z');
      });
    }
  });

  it('is needed: the browser reads the same string as local midnight, the day before in UTC', () => {
    inTimeZone(EAST, () => {
      expect(new Date('2026-10-19T00:00:00').toISOString()).toBe('2026-10-18T22:00:00.000Z');
    });
  });

  it('reads a value that carries Z or an offset, or a date with no time, exactly as new Date does', () => {
    inTimeZone(EAST, () => {
      for (const value of [
        '2026-10-19T00:00:00Z',
        '2026-10-19T00:00:00.0000000Z',
        '2026-10-19T08:00:00+09:00',
        '2026-10-19T00:00:00-05:00',
        '2026-10-19',
      ]) {
        expect(parseApiDate(value).toISOString()).toBe(new Date(value).toISOString());
      }
      expect(parseApiDate('2026-10-19T08:00:00+09:00').toISOString()).toBe('2026-10-18T23:00:00.000Z');
    });
  });

  it('leaves an empty or malformed value invalid, as it was', () => {
    for (const value of ['', 'not a date', '2026-13-45T99:99:99']) {
      expect(Number.isNaN(parseApiDate(value).getTime())).toBe(true);
      expect(Number.isNaN(new Date(value).getTime())).toBe(true);
    }
  });
});

describe('helpers that print a date from the API', () => {
  // Monday 19 October 2026: a week start, a usage day or a snapshot date, as SQL hands it over.
  const cases: [string, (iso: string) => string, string, string][] = [
    ['chart week label', formatWeek, '2026-10-19T00:00:00', '19 Oct'],
    ['chart week tooltip', formatWeekLong, '2026-10-19T00:00:00', 'Mon, 19 Oct 2026'],
    ['Teams Explorer date', formatTeamsDate, '2026-10-19T00:00:00', '19 Oct 2026'],
    ['agent cost day', formatDay, '2026-10-19T00:00:00', '19 Oct 2026'],
    ['agent cost time', formatDateTime, '2026-10-19T00:30:00', '19 Oct 2026, 00:30'],
    ['Copilot Adoption snapshot date', formatKpiDate, '2026-10-19T00:00:00', '19 Oct 2026'],
    ['Health time', formatUtc, '2026-10-19T00:30:00', '19/10/2026, 00:30:00 UTC'],
    ['Licence activity date', formatLicenceDate, '2026-10-19T00:00:00', '19 Oct 2026'],
    ['User lookup row time', formatDetailTimestamp, '2026-10-19T00:30:00', '19/10/2026, 00:30:00 UTC'],
  ];

  it.each(cases)('%s keeps the UTC day east of UTC when the API sent no offset', (_name, format, zoneless, expected) => {
    inTimeZone(EAST, () => {
      expect(format(zoneless)).toBe(expected);
    });
  });

  it.each(cases)('%s is unchanged for the same date sent with Z', (_name, format, zoneless, expected) => {
    inTimeZone(EAST, () => {
      expect(format(`${zoneless}Z`)).toBe(expected);
    });
  });
});
