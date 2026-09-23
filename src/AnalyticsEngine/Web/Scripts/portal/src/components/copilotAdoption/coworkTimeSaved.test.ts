import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { CopilotAdoptionOptions, CoworkValueEstimate } from '../../types/copilotAdoption';
import {
  TIME_SAVED_STORAGE_KEY,
  apportion,
  defaultTimeSavedAssumptions,
  parseTimeSavedOverrides,
  projectTimeSaved,
  resetTimeSavedStore,
  timeSavedExportParams,
  useTimeSavedAssumptions,
} from './coworkTimeSaved';
import { TIME_SAVED_BENCHMARKS, benchmarkRange, senseCheck } from './coworkTimeSavedEvidence';

/** The product defaults as the server ships them (CopilotAdoptionOptions.cs). */
const OPTIONS = {
  workingDaysPerWeek: 5,
  habitBucketNormalisationDays: 28,
  coworkMinutesSavedPerMeeting: 5,
  coworkMinutesSavedPerMailThread: 0.5,
  coworkMinutesSavedPerDocument: 1,
  coworkEstimateLowerBoundRatio: 0.5,
} as CopilotAdoptionOptions;

function estimate(overrides: Partial<CoworkValueEstimate> = {}): CoworkValueEstimate {
  return {
    isModelled: true,
    cohortUsers: 10,
    addressableMeetings: 1234,
    addressableMailThreads: 5678,
    addressableDocuments: 910,
    hoursPerMonthLow: 0,
    hoursPerMonthHigh: 0,
    assumptions: [],
    ...overrides,
  };
}

describe('projectTimeSaved', () => {
  /**
   * The golden figure shared with CopilotAdoptionCoworkTests.Estimate_MatchesThePortalsGoldenFigure.
   * The portal recomputes the hours the server also publishes, so both implementations are pinned to
   * the same answer for the same input - change one and this, or its C# twin, fails.
   *
   * 1,234 x 5 + 5,678 x 0.5 + 910 x 1 = 9,919 minutes = 165.3 hours; x 50% = 82.7.
   */
  it('matches the server to the hour for the same volumes and assumptions', () => {
    const projection = projectTimeSaved(estimate(), defaultTimeSavedAssumptions(OPTIONS), OPTIONS)!;

    expect(projection.hoursHigh).toBe(165);
    expect(projection.hoursLow).toBe(83);
    expect(projection.activities.map((a) => a.displayHours)).toEqual([103, 47, 15]);
  });

  it('restates the hours per person per day and as full-time people', () => {
    const projection = projectTimeSaved(estimate(), defaultTimeSavedAssumptions(OPTIONS), OPTIONS)!;

    // 28 days x 5/7 = 20 working days; 20 x 8 hours = a 160-hour full-time month.
    expect(projection.workingDaysPerMonth).toBe(20);
    expect(projection.hoursPerFullTimeMonth).toBe(160);
    expect(projection.minutesPerPersonDayHigh).toBeCloseTo(9919 / 200, 9);
    expect(projection.fteHigh).toBeCloseTo(9919 / 60 / 160, 9);
    expect(projection.fteLow).toBeCloseTo(projection.fteHigh / 2, 9);
  });

  it('says nothing, rather than zero, when there is nobody to model', () => {
    // "0 hours" would read as a finding about the tenant, when it only means an empty cohort.
    expect(projectTimeSaved(estimate({ cohortUsers: 0 }), defaultTimeSavedAssumptions(OPTIONS), OPTIONS)).toBeNull();
    expect(projectTimeSaved(undefined, defaultTimeSavedAssumptions(OPTIONS), OPTIONS)).toBeNull();
  });

  it('keeps the range the right way round whatever the conservative share says', () => {
    const assumptions = { ...defaultTimeSavedAssumptions(OPTIONS), conservativeRatio: 7 };
    const projection = projectTimeSaved(estimate(), assumptions, OPTIONS)!;

    expect(projection.hoursLow).toBeLessThanOrEqual(projection.hoursHigh);
  });
});

describe('apportion', () => {
  it('rounds parts so they add up to exactly the rounded total', () => {
    const parts = [102.83, 47.32, 15.17];
    expect(apportion(165, parts)).toEqual([103, 47, 15]);
    expect(apportion(10, [3.34, 3.33, 3.33])).toEqual([4, 3, 3]);
    expect(apportion(0, [0, 0, 0])).toEqual([0, 0, 0]);
  });
});

describe('the reader\u2019s own figures', () => {
  beforeEach(() => resetTimeSavedStore());

  it('drops anything stored that the model would not accept', () => {
    expect(parseTimeSavedOverrides('not json')).toEqual({});
    expect(
      parseTimeSavedOverrides(JSON.stringify({ meetingMinutes: 12, emailMinutes: -1, documentMinutes: 'x', futureKey: 3 })),
    ).toEqual({ meetingMinutes: 12 });
  });

  it('stores only a changed figure, and forgets one set back to the default', () => {
    const { result } = renderHook(() => useTimeSavedAssumptions(OPTIONS));

    act(() => result.current.setAssumption('emailMinutes', 0.25));
    expect(result.current.assumptions.emailMinutes).toBe(0.25);
    expect(result.current.customised).toEqual(['emailMinutes']);
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)!)).toEqual({ emailMinutes: 0.25 });

    // Setting the default back removes the override, so the figure follows any later change of
    // default instead of pinning this tab to an old value.
    act(() => result.current.setAssumption('emailMinutes', 0.5));
    expect(result.current.isCustomised).toBe(false);
    expect(sessionStorage.getItem(TIME_SAVED_STORAGE_KEY)).toBeNull();
  });

  it('ignores a figure outside the bounds', () => {
    const { result } = renderHook(() => useTimeSavedAssumptions(OPTIONS));

    act(() => result.current.setAssumption('meetingMinutes', 100000));
    expect(result.current.assumptions.meetingMinutes).toBe(5);
    expect(result.current.isCustomised).toBe(false);
  });

  it('shares one set of figures between every part of the page', () => {
    // The headline, the overview KPI and the Excel link each read the store; a change in one must
    // be the figure in all of them.
    const first = renderHook(() => useTimeSavedAssumptions(OPTIONS));
    const second = renderHook(() => useTimeSavedAssumptions(OPTIONS));

    act(() => first.result.current.setAssumption('documentMinutes', 2));
    expect(second.result.current.assumptions.documentMinutes).toBe(2);
  });

  it('sends only the changed, server-modelled figures with an export, under the option names', () => {
    const { result } = renderHook(() => useTimeSavedAssumptions(OPTIONS));
    expect(timeSavedExportParams(result.current)).toEqual({});

    act(() => {
      result.current.setAssumption('meetingMinutes', 8);
      result.current.setAssumption('conservativeRatio', 0.3);
      // On-screen only: the workbook quotes hours, not full-time people.
      result.current.setAssumption('hoursPerDay', 7.5);
    });

    expect(timeSavedExportParams(result.current)).toEqual({
      coworkMinutesSavedPerMeeting: '8',
      coworkEstimateLowerBoundRatio: '0.3',
    });
  });
});

describe('the sense check', () => {
  it('compares against whole-job figures only', () => {
    // The randomised study's email-only figure is a floor, not a whole-job saving, so it must not
    // widen the range a whole-job model is judged against.
    const partial = TIME_SAVED_BENCHMARKS.filter((b) => b.partial).map((b) => b.minutesPerDay);
    expect(partial).toEqual([17]);
    expect(benchmarkRange()).toEqual({ min: 14, max: 27 });
  });

  it('judges the conservative end, which is the counterpart of today\u2019s studies', () => {
    expect(senseCheck(20)).toBe('within');
    expect(senseCheck(40)).toBe('above');
    expect(senseCheck(5)).toBe('below');
  });

  it('cites every benchmark to a primary source', () => {
    for (const b of TIME_SAVED_BENCHMARKS) {
      expect(b.url).toMatch(/^https:\/\//);
    }
  });
});
