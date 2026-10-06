import { beforeEach, describe, expect, it } from 'vitest';
import { translateActive, type TFunction } from '../../i18n';
import { setActiveLanguage } from '../../i18n/runtime';
import type { ActivityAnalysisMetric } from '../../types/activityAnalysis';
import { EMPTY_USER_FILTER } from '../../types/userFilter';
import {
  NO_FILTERS,
  activeFilterCount,
  applyDraft,
  describeAppliedFilters,
  draftFrom,
  parseRange,
} from './filters';
import {
  addWeeks,
  allIsLimited,
  mondayOnOrBefore,
  presetPeriod,
  validateCustomPeriod,
  weekCount,
} from './period';

const t: TFunction = (key, values) => translateActive(key, values);

const calls: ActivityAnalysisMetric = { key: 'teams.calls', category: 'teams', unit: 'count', core: true, available: true, label: 'Teams Calls' };
const audio: ActivityAnalysisMetric = {
  key: 'teams.audioDuration',
  category: 'teams',
  unit: 'seconds',
  core: false,
  available: true,
  label: 'Teams Audio Duration Seconds',
};
const sent: ActivityAnalysisMetric = { key: 'outlook.emailsSent', category: 'outlook', unit: 'count', core: true, available: true, label: 'Emails Sent' };
const METRICS = new Map([calls, audio, sent].map((m) => [m.key, m]));
const ORDER = ['teams.calls', 'teams.audioDuration', 'outlook.emailsSent'];

beforeEach(() => setActiveLanguage('en'));

describe('weeks and periods', () => {
  const bounds = { earliestWeek: '2025-01-06', latestWeek: '2026-09-28', maximumWeeks: 52 };

  it('moves any date to the Monday of its week, Sundays included', () => {
    expect(mondayOnOrBefore('2026-09-28')).toBe('2026-09-28');
    expect(mondayOnOrBefore('2026-10-04')).toBe('2026-09-28');
    expect(mondayOnOrBefore('2026-10-01')).toBe('2026-09-28');
    expect(mondayOnOrBefore('2026-02-31')).toBeNull();
  });

  it('counts weeks inclusively', () => {
    expect(weekCount({ from: '2026-09-28', to: '2026-09-28' })).toBe(1);
    expect(weekCount({ from: addWeeks('2026-09-28', -51), to: '2026-09-28' })).toBe(52);
    expect(weekCount({ from: '2026-09-28', to: '2026-09-21' })).toBe(0);
  });

  it('ends every preset on the latest week and reaches back its number of weeks', () => {
    expect(presetPeriod('months3', bounds)).toEqual({ from: addWeeks('2026-09-28', -12), to: '2026-09-28' });
    expect(presetPeriod('months12', bounds)).toEqual({ from: addWeeks('2026-09-28', -51), to: '2026-09-28' });
  });

  it('never starts before the earliest week, and limits "all" to the longest period allowed', () => {
    const young = { earliestWeek: '2026-08-03', latestWeek: '2026-09-28', maximumWeeks: 105 };
    expect(presetPeriod('months6', young)).toEqual({ from: '2026-08-03', to: '2026-09-28' });
    expect(presetPeriod('all', young)).toEqual({ from: '2026-08-03', to: '2026-09-28' });
    expect(allIsLimited(young)).toBe(false);

    expect(presetPeriod('all', bounds)).toEqual({ from: addWeeks('2026-09-28', -51), to: '2026-09-28' });
    expect(allIsLimited(bounds)).toBe(true);
  });

  it('snaps a custom period to Mondays within the weeks that have data, and refuses a bad one', () => {
    expect(validateCustomPeriod({ from: '2024-01-01', to: '2026-10-03' }, { ...bounds, maximumWeeks: 200 }, t)).toEqual({
      ok: true,
      period: { from: '2025-01-06', to: '2026-09-28' },
    });
    expect(validateCustomPeriod({ from: '', to: '2026-09-28' }, bounds, t)).toEqual({
      ok: false,
      error: 'Enter both the first and the last week.',
    });
    expect(validateCustomPeriod({ from: '2026-09-28', to: '2026-01-05' }, bounds, t)).toEqual({
      ok: false,
      error: 'The first week must be on or before the last week.',
    });
    expect(validateCustomPeriod({ from: '2025-01-06', to: '2026-09-28' }, bounds, t)).toEqual({
      ok: false,
      error: 'Choose a period of 52 weeks or fewer.',
    });
  });
});

describe('filter drafts', () => {
  it('reads durations in hours and sends them in seconds', () => {
    expect(parseRange({ min: '1.5', max: '' }, audio)).toEqual({ min: 5400, max: null, error: null });
    expect(parseRange({ min: '', max: '2,25' }, audio)).toEqual({ min: null, max: 8100, error: null });
  });

  it('refuses a fraction of a count, a negative number, text and a reversed range', () => {
    expect(parseRange({ min: '2.5', max: '' }, calls).error).toBe('whole');
    expect(parseRange({ min: '-1', max: '' }, calls).error).toBe('number');
    expect(parseRange({ min: 'lots', max: '' }, calls).error).toBe('number');
    expect(parseRange({ min: '10', max: '5' }, calls).error).toBe('order');
    expect(parseRange({ min: ' ', max: '' }, calls)).toEqual({ min: null, max: null, error: null });
  });

  it('applies ranges in catalogue order and licences ascending, leaving out empty ranges', () => {
    const { applied, errors } = applyDraft(
      {
        userFilter: EMPTY_USER_FILTER,
        licences: [12, 7, 12],
        ranges: {
          'outlook.emailsSent': { min: '', max: '100' },
          'teams.audioDuration': { min: '', max: '' },
          'teams.calls': { min: '5', max: '' },
        },
      },
      ORDER,
      METRICS,
    );

    expect(errors).toEqual({});
    expect(applied.licences).toEqual([7, 12]);
    expect(applied.ranges).toEqual([
      { metric: 'teams.calls', min: 5, max: null },
      { metric: 'outlook.emailsSent', min: null, max: 100 },
    ]);
    expect(activeFilterCount(applied)).toBe(3);
  });

  it('reports each range that cannot be applied', () => {
    const { applied, errors } = applyDraft(
      { userFilter: EMPTY_USER_FILTER, licences: [], ranges: { 'teams.calls': { min: '9', max: '3' } } },
      ORDER,
      METRICS,
    );
    expect(errors).toEqual({ 'teams.calls': 'order' });
    expect(applied.ranges).toEqual([]);
  });

  it('opens the panel on the filters in force, durations back in hours', () => {
    const draft = draftFrom(
      { ...NO_FILTERS, licences: [7], ranges: [{ metric: 'teams.audioDuration', min: 5400, max: null }] },
      METRICS,
    );
    expect(draft.licences).toEqual([7]);
    expect(draft.ranges).toEqual({ 'teams.audioDuration': { min: '1.5', max: '' } });
  });

  it('describes the applied filters in words, naming licences as the tenant does', () => {
    const items = describeAppliedFilters(
      t,
      {
        userFilter: { clauses: [{ join: 'and', dimension: 'department', operator: 'is', values: ['Sales'], includeNotSet: false }] },
        licences: [7, 99],
        ranges: [
          { metric: 'teams.calls', min: 5, max: 100 },
          { metric: 'teams.audioDuration', min: 3600, max: null },
          { metric: 'outlook.emailsSent', min: null, max: 1000 },
        ],
      },
      METRICS,
      [{ id: 7, name: 'Microsoft 365 E3', skuId: 'SPE_E3', people: 80 }],
    );

    expect(items).toEqual([
      'Department is Sales',
      'Licences: Microsoft 365 E3 and Licence 99',
      'Teams calls: 5 to 100',
      'Teams audio time: at least 1 h',
      'Emails sent: at most 1,000',
    ]);
  });
});
