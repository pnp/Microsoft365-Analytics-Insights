import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { CopilotAdoptionOptions, CopilotAdoptionSummary, CoworkValueEstimate } from '../../types/copilotAdoption';
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
  coworkMinutesSavedPerTask: 6,
  coworkAssumedTasksPerPersonPerMonth: 20,
} as CopilotAdoptionOptions;

/** The golden estimate shared with CopilotAdoptionCoworkTests.Estimate_MatchesThePortalsGoldenFigure. */
function estimate(overrides: Partial<CoworkValueEstimate> = {}): CoworkValueEstimate {
  return {
    isModelled: true,
    cohortUsers: 10,
    addressableMeetings: 1234,
    addressableMailThreads: 5678,
    addressableDocuments: 910,
    coworkTaskUsers: 3,
    observedCoworkTasks: 45,
    projectedCoworkUsers: 7,
    coworkTasksPerPersonPerMonth: 12.5,
    coworkTaskRateBasis: 'observed',
    coworkTaskRateUsers: 3,
    hoursPerMonthLow: 0,
    hoursPerMonthHigh: 0,
    assumptions: [],
    ...overrides,
  };
}

const SUMMARY = {
  options: OPTIONS,
  coworkValueEstimate: estimate(),
  coworkFullRolloutEstimate: estimate(),
} as Pick<CopilotAdoptionSummary, 'options' | 'coworkValueEstimate' | 'coworkFullRolloutEstimate'>;

const defaults = () => defaultTimeSavedAssumptions(OPTIONS, estimate());

describe('projectTimeSaved', () => {
  /**
   * The golden figure shared with CopilotAdoptionCoworkTests.Estimate_MatchesThePortalsGoldenFigure.
   * The portal recomputes the hours the server also publishes, so both implementations are pinned to
   * the same answer for the same input - change one and this, or its C# twin, fails.
   *
   * Copilot: 1,234 x 5 + 5,678 x 0.5 + 910 x 1 = 9,919 minutes = 165.3 hours; x 50% = 82.7.
   * Cowork: 45 observed + 7 projected x 12.5 (= 87.5, a deliberate midpoint: both languages must
   * round it up to 88) = 133 tasks x 6 minutes = 798 minutes = 13.3 hours; x 50% = 6.65.
   */
  it('matches the server to the hour for the same inputs and assumptions', () => {
    const projection = projectTimeSaved(estimate(), defaults(), OPTIONS)!;

    expect(projection.copilotHoursHigh).toBe(165);
    expect(projection.copilotHoursLow).toBe(83);
    expect(projection.activities.map((a) => a.displayHours)).toEqual([103, 47, 15]);
    expect(projection.cowork.projectedTasks).toBe(88);
    expect(projection.cowork.tasks).toBe(133);
    expect(projection.cowork.hoursHigh).toBe(13);
    expect(projection.cowork.hoursLow).toBe(7);
    expect(projection.hoursHigh).toBe(178);
    expect(projection.hoursLow).toBe(90);
  });

  it('restates the hours per person per day and as full-time people, with Copilot\u2019s share apart', () => {
    const projection = projectTimeSaved(estimate(), defaults(), OPTIONS)!;

    // 28 days x 5/7 = 20 working days; 20 x 8 hours = a 160-hour full-time month.
    expect(projection.workingDaysPerMonth).toBe(20);
    expect(projection.hoursPerFullTimeMonth).toBe(160);
    expect(projection.minutesPerPersonDayHigh).toBeCloseTo((9919 + 798) / 200, 9);
    expect(projection.copilotMinutesPerPersonDayHigh).toBeCloseTo(9919 / 200, 9);
    expect(projection.copilotMinutesPerPersonDayLow).toBeCloseTo(9919 / 400, 9);
    expect(projection.fteHigh).toBeCloseTo((9919 + 798) / 60 / 160, 9);
    expect(projection.fteLow).toBeCloseTo(projection.fteHigh / 2, 9);
  });

  it('says nothing, rather than zero, when there is nobody to model', () => {
    // "0 hours" would read as a finding about the tenant, when it only means an empty cohort.
    expect(projectTimeSaved(estimate({ cohortUsers: 0 }), defaults(), OPTIONS)).toBeNull();
    expect(projectTimeSaved(undefined, defaults(), OPTIONS)).toBeNull();
  });

  it('keeps the range the right way round whatever the conservative share says', () => {
    const assumptions = { ...defaults(), conservativeRatio: 7 };
    const projection = projectTimeSaved(estimate(), assumptions, OPTIONS)!;

    expect(projection.hoursLow).toBeLessThanOrEqual(projection.hoursHigh);
  });
});

describe('the Copilot and Cowork layers', () => {
  /**
   * The defect this guards: the model used to be one blended layer, "Copilot and Cowork together",
   * resting entirely on Microsoft 365 Copilot evidence - so the figure used to justify Copilot Credits
   * was mostly Copilot's. Each layer is now separate, and the total is exactly their sum.
   */
  it('adds the two layers up to the total, and keeps Cowork\u2019s minutes out of Copilot\u2019s', () => {
    const projection = projectTimeSaved(estimate(), defaults(), OPTIONS)!;
    expect(projection.hoursHigh).toBe(projection.copilotHoursHigh + projection.cowork.hoursHigh);
    expect(projection.hoursLow).toBe(projection.copilotHoursLow + projection.cowork.hoursLow);

    const noCowork = projectTimeSaved(estimate(), { ...defaults(), taskMinutes: 0 }, OPTIONS)!;
    expect(noCowork.hoursHigh).toBe(projection.copilotHoursHigh);
    expect(noCowork.copilotMinutesPerPersonDayHigh).toBe(projection.copilotMinutesPerPersonDayHigh);
  });

  it('shares the whole bar between the three kinds of Copilot work and Cowork\u2019s tasks', () => {
    const projection = projectTimeSaved(estimate(), defaults(), OPTIONS)!;
    const total = projection.activities.reduce((sum, a) => sum + a.sharePct, 0) + projection.cowork.sharePct;

    expect(total).toBeCloseTo(100, 9);
    expect(projection.cowork.sharePct).toBeCloseTo((798 / (9919 + 798)) * 100, 9);
  });

  it('takes the task-rate default from the tenant\u2019s own Cowork users, not from configuration', () => {
    expect(defaultTimeSavedAssumptions(OPTIONS, estimate()).tasksPerPerson).toBe(12.5);
    // Nothing observed and nothing published: the server's labelled placeholder.
    expect(defaultTimeSavedAssumptions(OPTIONS, estimate({ coworkTasksPerPersonPerMonth: undefined })).tasksPerPerson).toBe(20);
    expect(projectTimeSaved(estimate(), defaults(), OPTIONS)!.cowork.rateBasis).toBe('observed');
  });

  it('labels a changed task rate as the reader\u2019s own and projects only the people not yet observed', () => {
    const projection = projectTimeSaved(estimate(), { ...defaults(), tasksPerPerson: 30 }, OPTIONS)!;

    expect(projection.cowork.rateBasis).toBe('custom');
    expect(projection.cowork.rateUsers).toBe(0);
    expect(projection.cowork.projectedTasks).toBe(7 * 30);
    // The 45 observed tasks stay exactly as Microsoft reported them.
    expect(projection.cowork.tasks).toBe(45 + 210);
  });

  it('models no Cowork it was not told about', () => {
    // An estimate published before the layers were split carries no Cowork fields, and options with
    // no minutes per task: the Cowork layer is then zero rather than an invented figure.
    const legacy = estimate({
      coworkTaskUsers: undefined,
      observedCoworkTasks: undefined,
      projectedCoworkUsers: undefined,
      coworkTasksPerPersonPerMonth: undefined,
      coworkTaskRateBasis: undefined,
      coworkTaskRateUsers: undefined,
    });
    const legacyOptions = { ...OPTIONS, coworkMinutesSavedPerTask: undefined, coworkAssumedTasksPerPersonPerMonth: undefined } as unknown as CopilotAdoptionOptions;
    const projection = projectTimeSaved(legacy, defaultTimeSavedAssumptions(legacyOptions, legacy), legacyOptions)!;

    expect(projection.cowork.hoursHigh).toBe(0);
    expect(projection.hoursHigh).toBe(165);
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
    const { result } = renderHook(() => useTimeSavedAssumptions(SUMMARY));

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
    const { result } = renderHook(() => useTimeSavedAssumptions(SUMMARY));

    act(() => result.current.setAssumption('meetingMinutes', 100000));
    expect(result.current.assumptions.meetingMinutes).toBe(5);
    act(() => result.current.setAssumption('tasksPerPerson', 1000));
    expect(result.current.assumptions.tasksPerPerson).toBe(12.5);
    expect(result.current.isCustomised).toBe(false);
  });

  it('shares one set of figures between every part of the page', () => {
    // The headline, the overview KPI and the Excel link each read the store; a change in one must
    // be the figure in all of them.
    const first = renderHook(() => useTimeSavedAssumptions(SUMMARY));
    const second = renderHook(() => useTimeSavedAssumptions(SUMMARY));

    act(() => first.result.current.setAssumption('documentMinutes', 2));
    expect(second.result.current.assumptions.documentMinutes).toBe(2);
  });

  it('sends only the changed, server-modelled figures with an export, under the option names', () => {
    const { result } = renderHook(() => useTimeSavedAssumptions(SUMMARY));
    expect(timeSavedExportParams(result.current)).toEqual({});

    act(() => {
      result.current.setAssumption('meetingMinutes', 8);
      result.current.setAssumption('conservativeRatio', 0.3);
      result.current.setAssumption('taskMinutes', 15);
      result.current.setAssumption('tasksPerPerson', 25);
      // On-screen only: the workbook quotes hours, not full-time people.
      result.current.setAssumption('hoursPerDay', 7.5);
    });

    expect(timeSavedExportParams(result.current)).toEqual({
      coworkMinutesSavedPerMeeting: '8',
      coworkEstimateLowerBoundRatio: '0.3',
      coworkMinutesSavedPerTask: '15',
      coworkTasksPerPersonPerMonth: '25',
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
