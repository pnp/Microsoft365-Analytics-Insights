import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type {
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  CoworkValueEstimate,
  LicenceValueEstimate,
} from '../../types/copilotAdoption';
import {
  COWORK_ASSUMPTION_KEYS,
  LICENCE_ASSUMPTION_KEYS,
  TIME_SAVED_STORAGE_KEY,
  apportion,
  compactHoursRange,
  customisesAny,
  defaultTimeSavedAssumptions,
  parseTimeSavedOverrides,
  projectCoworkTimeSaved,
  projectLicenceTimeSaved,
  resetTimeSavedStore,
  timeSavedExportParams,
  useTimeSavedAssumptions,
} from './coworkTimeSaved';
import { TIME_SAVED_BENCHMARKS, benchmarkRange, senseCheck } from './coworkTimeSavedEvidence';
import { loadCatalog, setActiveLanguage, translateActive, type TFunction } from '../../i18n';

/** The product defaults as the server ships them (CopilotAdoptionOptions.cs). */
const OPTIONS = {
  workingDaysPerWeek: 5,
  habitBucketNormalisationDays: 28,
  copilotMinutesSavedPerMeeting: 5,
  copilotMinutesSavedPerMailThread: 0.5,
  copilotMinutesSavedPerDocument: 1,
  coworkEstimateLowerBoundRatio: 0.5,
  coworkMinutesSavedPerTask: 6,
  coworkAssumedTasksPerPersonPerMonth: 20,
  maxOpportunityCandidates: 50000,
} as CopilotAdoptionOptions;

/** The licence golden estimate shared with CopilotAdoptionLicenceEstimateTests.Estimate_MatchesThePortalsGoldenFigure. */
function licenceEstimate(overrides: Partial<LicenceValueEstimate> = {}): LicenceValueEstimate {
  return {
    isModelled: true,
    cohortUsers: 10,
    addressableMeetings: 1234,
    addressableMailThreads: 5678,
    addressableDocuments: 910,
    hoursPerMonthLow: 83,
    hoursPerMonthHigh: 165,
    candidatesCapped: false,
    assumptions: [],
    ...overrides,
  };
}

/** The Cowork golden estimate shared with CopilotAdoptionCoworkTests.Estimate_MatchesThePortalsGoldenFigure. */
function coworkEstimate(overrides: Partial<CoworkValueEstimate> = {}): CoworkValueEstimate {
  return {
    isModelled: true,
    cohortUsers: 10,
    coworkTaskUsers: 3,
    observedCoworkTasks: 45,
    projectedCoworkUsers: 7,
    coworkTasksPerPersonPerMonth: 12.5,
    coworkTaskRateBasis: 'observed',
    coworkTaskRateUsers: 3,
    coworkTasks: 133,
    hoursPerMonthLow: 7,
    hoursPerMonthHigh: 13,
    assumptions: [],
    ...overrides,
  };
}

const SUMMARY = {
  options: OPTIONS,
  coworkValueEstimate: coworkEstimate(),
  coworkFullRolloutEstimate: coworkEstimate(),
} as Pick<CopilotAdoptionSummary, 'options' | 'coworkValueEstimate' | 'coworkFullRolloutEstimate'>;

const defaults = () => defaultTimeSavedAssumptions(OPTIONS, coworkEstimate());

describe('projectLicenceTimeSaved', () => {
  /**
   * The golden figure shared with CopilotAdoptionLicenceEstimateTests.Estimate_MatchesThePortalsGoldenFigure.
   * The portal recomputes the hours the server also publishes, so both implementations are pinned to
   * the same answer for the same input - change one and this, or its C# twin, fails.
   *
   * 1,234 x 5 + 5,678 x 0.5 + 910 x 1 = 9,919 minutes = 165.3 hours; x 50% = 82.7.
   */
  it('matches the server to the hour for the same inputs and assumptions', () => {
    const projection = projectLicenceTimeSaved(licenceEstimate(), defaults(), OPTIONS)!;

    expect(projection.hoursHigh).toBe(165);
    expect(projection.hoursLow).toBe(83);
    expect(projection.activities.map((a) => a.displayHours)).toEqual([103, 47, 15]);
  });

  it('restates the hours per person per day and as full-time people', () => {
    const projection = projectLicenceTimeSaved(licenceEstimate(), defaults(), OPTIONS)!;

    // 28 days x 5/7 = 20 working days; 20 x 8 hours = a 160-hour full-time month.
    expect(projection.workingDaysPerMonth).toBe(20);
    expect(projection.hoursPerFullTimeMonth).toBe(160);
    expect(projection.minutesPerPersonDayHigh).toBeCloseTo(9919 / 200, 9);
    expect(projection.minutesPerPersonDayLow).toBeCloseTo(9919 / 400, 9);
    expect(projection.fteHigh).toBeCloseTo(9919 / 60 / 160, 9);
    expect(projection.fteLow).toBeCloseTo(projection.fteHigh / 2, 9);
  });

  it('shares the bar between the three kinds of work, and never moves with Cowork\u2019s figures', () => {
    const projection = projectLicenceTimeSaved(licenceEstimate(), defaults(), OPTIONS)!;
    expect(projection.activities.reduce((sum, a) => sum + a.sharePct, 0)).toBeCloseTo(100, 9);

    const heavierCowork = projectLicenceTimeSaved(licenceEstimate(), { ...defaults(), taskMinutes: 240, tasksPerPerson: 200 }, OPTIONS)!;
    expect(heavierCowork.hoursHigh).toBe(projection.hoursHigh);
  });

  it('carries the candidate cap, so the headline can say it is a floor', () => {
    expect(projectLicenceTimeSaved(licenceEstimate({ candidatesCapped: true }), defaults(), OPTIONS)!.candidatesCapped).toBe(true);
    expect(projectLicenceTimeSaved(licenceEstimate(), defaults(), OPTIONS)!.candidatesCapped).toBe(false);
  });

  it('says nothing, rather than zero, when there is nobody to model', () => {
    // "0 hours" would read as a finding about the tenant, when it only means an empty cohort.
    expect(projectLicenceTimeSaved(licenceEstimate({ cohortUsers: 0 }), defaults(), OPTIONS)).toBeNull();
    expect(projectLicenceTimeSaved(undefined, defaults(), OPTIONS)).toBeNull();
  });

  it('keeps the range the right way round whatever the conservative share says', () => {
    const projection = projectLicenceTimeSaved(licenceEstimate(), { ...defaults(), conservativeRatio: 7 }, OPTIONS)!;
    expect(projection.hoursLow).toBeLessThanOrEqual(projection.hoursHigh);
  });
});

describe('projectCoworkTimeSaved', () => {
  /**
   * The golden figure shared with CopilotAdoptionCoworkTests.Estimate_MatchesThePortalsGoldenFigure.
   *
   * 45 observed + 7 projected x 12.5 (= 87.5, a deliberate midpoint: both languages must round it up
   * to 88) = 133 tasks x 6 minutes = 798 minutes = 13.3 hours; x 50% = 6.65.
   */
  it('matches the server to the hour for the same inputs and assumptions', () => {
    const projection = projectCoworkTimeSaved(coworkEstimate(), defaults(), OPTIONS)!;

    expect(projection.projectedTasks).toBe(88);
    expect(projection.tasks).toBe(133);
    expect(projection.hoursHigh).toBe(13);
    expect(projection.hoursLow).toBe(7);
  });

  it('is Cowork\u2019s increment alone: the Copilot minutes never move it', () => {
    // The defect this guards: the Cowork headline used to add a Copilot layer for people who already
    // hold a licence - most of the figure used to justify Copilot Credits was time the licence gives.
    const projection = projectCoworkTimeSaved(coworkEstimate(), defaults(), OPTIONS)!;
    const heavierCopilot = projectCoworkTimeSaved(
      coworkEstimate(),
      { ...defaults(), meetingMinutes: 120, emailMinutes: 60, documentMinutes: 120 },
      OPTIONS,
    )!;

    expect(heavierCopilot.hoursHigh).toBe(projection.hoursHigh);
    expect(projection.minutesPerPersonDayHigh).toBeCloseTo(798 / 200, 9);
  });

  it('takes the task-rate default from the tenant\u2019s own Cowork users, not from configuration', () => {
    expect(defaultTimeSavedAssumptions(OPTIONS, coworkEstimate()).tasksPerPerson).toBe(12.5);
    // Nothing published: the server's labelled placeholder.
    const unpublished = { ...coworkEstimate(), coworkTasksPerPersonPerMonth: undefined } as unknown as CoworkValueEstimate;
    expect(defaultTimeSavedAssumptions(OPTIONS, unpublished).tasksPerPerson).toBe(20);
    expect(projectCoworkTimeSaved(coworkEstimate(), defaults(), OPTIONS)!.rateBasis).toBe('observed');
  });

  it('labels a changed task rate as the reader\u2019s own and projects only the people not yet observed', () => {
    const projection = projectCoworkTimeSaved(coworkEstimate(), { ...defaults(), tasksPerPerson: 30 }, OPTIONS)!;

    expect(projection.rateBasis).toBe('custom');
    expect(projection.rateUsers).toBe(0);
    expect(projection.projectedTasks).toBe(7 * 30);
    // The 45 observed tasks stay exactly as Microsoft reported them.
    expect(projection.tasks).toBe(45 + 210);
  });

  it('says nothing, rather than zero, when there is nobody to model', () => {
    expect(projectCoworkTimeSaved(coworkEstimate({ cohortUsers: 0 }), defaults(), OPTIONS)).toBeNull();
    expect(projectCoworkTimeSaved(undefined, defaults(), OPTIONS)).toBeNull();
  });
});

describe('apportion', () => {
  it('rounds parts so they add up to exactly the rounded total', () => {
    expect(apportion(165, [102.83, 47.32, 15.17])).toEqual([103, 47, 15]);
    expect(apportion(10, [3.34, 3.33, 3.33])).toEqual([4, 3, 3]);
    expect(apportion(0, [0, 0, 0])).toEqual([0, 0, 0]);
  });
});

/**
 * The overview's tiles are a couple of hundred pixels wide, and at the 200,000-user design point a
 * range reaches seven digits. Compact notation keeps it on one line - but only where it is shorter:
 * Spanish writes thousands as "48 mil", longer than "48.000".
 */
describe('compactHoursRange', () => {
  const t = translateActive as unknown as TFunction;

  afterEach(() => setActiveLanguage('en'));

  it('uses compact notation in English once the range reaches five digits, on both ends alike', () => {
    setActiveLanguage('en');
    expect(compactHoursRange(t, 1275, 2550)).toBe('1,275\u20132,550');
    expect(compactHoursRange(t, 48000, 97000)).toBe('48k\u201397k');
    expect(compactHoursRange(t, 9500, 19000)).toBe('9.5k\u201319k');
    expect(compactHoursRange(t, 1234567, 2469134)).toBe('1.23m\u20132.47m');
  });

  it('keeps Spanish digits until compact notation is genuinely shorter', async () => {
    await loadCatalog('es');
    setActiveLanguage('es');
    // "48.000" is shorter than "48 mil", so thousands keep their digits...
    expect(compactHoursRange(t, 48000, 97000)).toBe('48.000\u201397.000');
    // ...and millions switch, because "1,23 M" is far shorter than "1.234.567".
    expect(compactHoursRange(t, 1234567, 2469134)).toBe('1,23\u00a0M\u20132,47\u00a0M');
  });

  it('shows one figure, not a range, when both ends round to the same value', () => {
    setActiveLanguage('en');
    expect(compactHoursRange(t, 12340, 12345)).toBe('12.3k');
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
    // Both tabs, the overview tiles and the Excel link each read the store; a change in one must be
    // the figure in all of them.
    const first = renderHook(() => useTimeSavedAssumptions(SUMMARY));
    const second = renderHook(() => useTimeSavedAssumptions(SUMMARY));

    act(() => first.result.current.setAssumption('documentMinutes', 2));
    expect(second.result.current.assumptions.documentMinutes).toBe(2);
  });

  it('knows which estimate a changed figure belongs to', () => {
    // A reader who retuned the Copilot minutes has changed the licence estimate, not the Cowork one.
    expect(customisesAny(['meetingMinutes'], LICENCE_ASSUMPTION_KEYS)).toBe(true);
    expect(customisesAny(['meetingMinutes'], COWORK_ASSUMPTION_KEYS)).toBe(false);
    expect(customisesAny(['tasksPerPerson'], COWORK_ASSUMPTION_KEYS)).toBe(true);
    expect(customisesAny(['conservativeRatio'], LICENCE_ASSUMPTION_KEYS)).toBe(true);
    expect(customisesAny(['conservativeRatio'], COWORK_ASSUMPTION_KEYS)).toBe(true);
    expect(customisesAny(['hoursPerDay'], [...LICENCE_ASSUMPTION_KEYS, ...COWORK_ASSUMPTION_KEYS])).toBe(false);
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
      copilotMinutesSavedPerMeeting: '8',
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
