import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type {
  CopilotAdoptionOptions,
  CopilotAdoptionSummary,
  CoworkValueEstimate,
  LicenceValueEstimate,
} from '../../types/copilotAdoption';
import {
  COWORK_ACTIVITIES,
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
  coworkOrganiseMeetingsShare: 0.25,
  coworkOrganiseMeetingsMinutes: 6,
  coworkPrepareMeetingsShare: 0.1,
  coworkPrepareMeetingsMinutes: 6,
  coworkSendEmailShare: 0.05,
  coworkSendEmailMinutes: 6,
  coworkPostInTeamsShare: 0.01,
  coworkPostInTeamsMinutes: 6,
  coworkCreateDocumentsShare: 0.02,
  coworkCreateDocumentsMinutes: 6,
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
    activities: [
      { activity: 'organiseMeetings', volumePerMonth: 142 },
      { activity: 'prepareMeetings', volumePerMonth: 560 },
      { activity: 'sendEmail', volumePerMonth: 1300 },
      { activity: 'postInTeams', volumePerMonth: 4200 },
      { activity: 'createDocuments', volumePerMonth: 1100 },
    ],
    projectedCoworkTasks: 221,
    coworkTasks: 266,
    observedTasksPerPersonPerMonth: 15,
    observedTaskRateUsers: 3,
    hoursPerMonthLow: 13,
    hoursPerMonthHigh: 27,
    assumptions: [],
    ...overrides,
  };
}

const SUMMARY = {
  options: OPTIONS,
  coworkValueEstimate: coworkEstimate(),
  coworkFullRolloutEstimate: coworkEstimate(),
} as Pick<CopilotAdoptionSummary, 'options' | 'coworkValueEstimate' | 'coworkFullRolloutEstimate'>;

const defaults = () => defaultTimeSavedAssumptions(OPTIONS);

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

    const heavierCowork = projectLicenceTimeSaved(
      licenceEstimate(),
      { ...defaults(), taskMinutes: 240, sendEmailShare: 1, sendEmailMinutes: 240, organiseMeetingsShare: 1 },
      OPTIONS,
    )!;
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
   * At the product defaults: 142 meetings organised x 25% = 35.5, 560 attended x 10% = 56, 1,300
   * emails x 5% = 65, 4,200 Teams messages x 1% = 42 and 1,100 files x 2% = 22 - 220.5 pieces of work,
   * published as 221 - plus 45 observed tasks. (45 + 220.5) x 6 minutes = 1,593 minutes = 26.55
   * hours; x 50% = 13.3.
   */
  it('matches the server to the hour for the same inputs and assumptions', () => {
    const projection = projectCoworkTimeSaved(coworkEstimate(), defaults(), OPTIONS)!;

    expect(projection.projectedTasks).toBe(221);
    expect(projection.tasks).toBe(266);
    expect(projection.hoursHigh).toBe(27);
    expect(projection.hoursLow).toBe(13);
    // Where the time comes from, split exactly as CoworkHoursByActivity splits it: the five kinds of
    // work in order, then the observed tasks, adding up to the headline.
    expect(projection.activities.map((a) => a.displayHours)).toEqual([4, 6, 7, 4, 2]);
    expect(projection.observedDisplayHours).toBe(4);
    expect(projection.activities.map((a) => a.displayPieces)).toEqual([36, 56, 65, 42, 22]);
  });

  it('shows each kind of work in the server\u2019s order, with its own volume, share and minutes', () => {
    const projection = projectCoworkTimeSaved(coworkEstimate(), defaults(), OPTIONS)!;

    expect(projection.activities.map((a) => a.activity)).toEqual([...COWORK_ACTIVITIES]);
    expect(projection.activities.map((a) => a.volume)).toEqual([142, 560, 1300, 4200, 1100]);
    expect(projection.activities.map((a) => a.share)).toEqual([0.25, 0.1, 0.05, 0.01, 0.02]);
    const total = projection.activities.reduce((sum, a) => sum + a.sharePct, 0) + projection.observedSharePct;
    expect(total).toBeCloseTo(100, 9);
  });

  it('moves only the kind of work whose figures the reader changed', () => {
    const baseline = projectCoworkTimeSaved(coworkEstimate(), defaults(), OPTIONS)!;
    const changed = projectCoworkTimeSaved(
      coworkEstimate(),
      { ...defaults(), organiseMeetingsShare: 0.5, organiseMeetingsMinutes: 12 },
      OPTIONS,
    )!;

    // 142 x 50% = 71 pieces x 12 minutes = 852 minutes, up from 213: (1,593 + 639) / 60 = 37.2.
    expect(changed.hoursHigh).toBe(37);
    expect(changed.projectedTasks).toBe(256);
    expect(changed.activities.slice(1).map((a) => a.pieces)).toEqual(baseline.activities.slice(1).map((a) => a.pieces));
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
    expect(projection.minutesPerPersonDayHigh).toBeCloseTo(1593 / 200, 6);
  });

  it('takes every Cowork default from configuration: nobody is projected at a flat rate any more', () => {
    const d = defaultTimeSavedAssumptions(OPTIONS);
    expect(d.organiseMeetingsShare).toBe(0.25);
    expect(d.sendEmailMinutes).toBe(6);
    expect(d).not.toHaveProperty('tasksPerPerson');
    // A share above 100% is clamped as the server clamps it: Cowork is never handed more than people do.
    expect(defaultTimeSavedAssumptions({ ...OPTIONS, coworkSendEmailShare: 3 }).sendEmailShare).toBe(1);
  });

  it('compares the model with the tenant\u2019s own Cowork users, and says nothing when there are none', () => {
    const projection = projectCoworkTimeSaved(coworkEstimate(), defaults(), OPTIONS)!;
    expect(projection.observedRate).toBe(15);
    expect(projection.observedRateUsers).toBe(3);
    // 220.5 pieces over the 7 people modelled.
    expect(projection.piecesPerProjectedPerson).toBeCloseTo(220.5 / 7, 9);

    const nobody = projectCoworkTimeSaved(
      coworkEstimate({ observedTasksPerPersonPerMonth: 0, observedTaskRateUsers: 0 }),
      defaults(),
      OPTIONS,
    )!;
    expect(nobody.observedRateUsers).toBe(0);
  });

  it('models only the people not yet running Cowork tasks', () => {
    // Everyone is observed: there is nobody whose work to model, whatever the published volumes say.
    const allObserved = projectCoworkTimeSaved(
      coworkEstimate({ coworkTaskUsers: 10, projectedCoworkUsers: 0 }),
      defaults(),
      OPTIONS,
    )!;
    expect(allObserved.projectedTasks).toBe(0);
    expect(allObserved.hoursHigh).toBe(Math.round((45 * 6) / 60));
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
    // A share is at most all of the work.
    act(() => result.current.setAssumption('sendEmailShare', 1.5));
    expect(result.current.assumptions.sendEmailShare).toBe(0.05);
    act(() => result.current.setAssumption('postInTeamsMinutes', 1000));
    expect(result.current.assumptions.postInTeamsMinutes).toBe(6);
    expect(result.current.isCustomised).toBe(false);
  });

  it('forgets a figure from the flat tasks-a-person model rather than misreading it', () => {
    // A session that began before the activity model stored its task rate under this key.
    sessionStorage.setItem(TIME_SAVED_STORAGE_KEY, JSON.stringify({ tasksPerPerson: 25, taskMinutes: 9 }));
    const { result } = renderHook(() => useTimeSavedAssumptions(SUMMARY));

    expect(result.current.customised).toEqual(['taskMinutes']);
    expect(result.current.assumptions).not.toHaveProperty('tasksPerPerson');
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
    expect(customisesAny(['sendEmailShare'], COWORK_ASSUMPTION_KEYS)).toBe(true);
    expect(customisesAny(['createDocumentsMinutes'], COWORK_ASSUMPTION_KEYS)).toBe(true);
    expect(customisesAny(['sendEmailShare'], LICENCE_ASSUMPTION_KEYS)).toBe(false);
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
      result.current.setAssumption('organiseMeetingsShare', 0.4);
      result.current.setAssumption('createDocumentsMinutes', 20);
      // On-screen only: the workbook quotes hours, not full-time people.
      result.current.setAssumption('hoursPerDay', 7.5);
    });

    expect(timeSavedExportParams(result.current)).toEqual({
      copilotMinutesSavedPerMeeting: '8',
      coworkEstimateLowerBoundRatio: '0.3',
      coworkMinutesSavedPerTask: '15',
      coworkOrganiseMeetingsShare: '0.4',
      coworkCreateDocumentsMinutes: '20',
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
