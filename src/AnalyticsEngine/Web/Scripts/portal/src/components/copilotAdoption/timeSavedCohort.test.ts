import { describe, it, expect, beforeEach, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import {
  DEFAULT_TIME_SAVED_COHORTS,
  TIME_SAVED_COHORT_STORAGE_KEY,
  parseTimeSavedCohorts,
  resetTimeSavedCohortStore,
  resolveTimeSavedCohort,
  useTimeSavedCohorts,
} from './timeSavedCohort';

beforeEach(() => resetTimeSavedCohortStore());

describe('resolveTimeSavedCohort', () => {
  const recommended = { cohortUsers: 10 };
  const all = { cohortUsers: 40 };

  it('leads with the people recommended, as the product default', () => {
    expect(resolveTimeSavedCohort('recommended', recommended, all)).toEqual({
      cohort: 'recommended',
      projection: recommended,
      fallback: false,
    });
  });

  it('models everyone when the reader chose to', () => {
    expect(resolveTimeSavedCohort('all', recommended, all)).toEqual({ cohort: 'all', projection: all, fallback: false });
  });

  /** The customer this exists for: nobody uses Microsoft 365 heavily enough to be recommended. */
  it('models everyone instead when nobody is recommended, and says it stood in', () => {
    expect(resolveTimeSavedCohort('recommended', null, all)).toEqual({ cohort: 'all', projection: all, fallback: true });
  });

  it('keeps to the people recommended when there is no figure for everyone', () => {
    expect(resolveTimeSavedCohort('all', recommended, undefined)).toEqual({
      cohort: 'recommended',
      projection: recommended,
      fallback: true,
    });
  });

  it('has nothing to show when neither cohort has anybody in it', () => {
    expect(resolveTimeSavedCohort('recommended', null, null)).toBeNull();
    expect(resolveTimeSavedCohort('all', null, undefined)).toBeNull();
  });
});

describe('parseTimeSavedCohorts', () => {
  it('reads an empty store as the defaults', () => {
    expect(parseTimeSavedCohorts('')).toEqual(DEFAULT_TIME_SAVED_COHORTS);
  });

  it('keeps the choices it understands and ignores everything else', () => {
    expect(parseTimeSavedCohorts(JSON.stringify({ licence: 'all', cowork: 'everyone', future: 'all' }))).toEqual({
      licence: 'all',
      cowork: 'recommended',
    });
  });

  it('falls back to the defaults for a corrupt value rather than guessing', () => {
    expect(parseTimeSavedCohorts('{not json')).toEqual(DEFAULT_TIME_SAVED_COHORTS);
    expect(parseTimeSavedCohorts('["all"]')).toEqual(DEFAULT_TIME_SAVED_COHORTS);
  });
});

describe('useTimeSavedCohorts', () => {
  it('starts on the people recommended for both models', () => {
    const { result } = renderHook(() => useTimeSavedCohorts());
    expect(result.current.cohorts).toEqual({ licence: 'recommended', cowork: 'recommended' });
  });

  it('keeps a choice for the session, and forgets it when it is the default again', () => {
    const { result } = renderHook(() => useTimeSavedCohorts());

    act(() => result.current.setCohort('licence', 'all'));
    expect(result.current.cohorts).toEqual({ licence: 'all', cowork: 'recommended' });
    expect(JSON.parse(sessionStorage.getItem(TIME_SAVED_COHORT_STORAGE_KEY)!)).toEqual({ licence: 'all' });

    act(() => result.current.setCohort('licence', 'recommended'));
    expect(result.current.cohorts).toEqual(DEFAULT_TIME_SAVED_COHORTS);
    expect(sessionStorage.getItem(TIME_SAVED_COHORT_STORAGE_KEY)).toBeNull();
  });

  /**
   * The headline, the calculator and the overview tile each read the choice through their own hook.
   * They must agree, or the overview could quote the time for different people from the tab.
   */
  it('shares one choice between every surface that reads it', () => {
    const first = renderHook(() => useTimeSavedCohorts());
    const second = renderHook(() => useTimeSavedCohorts());

    act(() => first.result.current.setCohort('cowork', 'all'));

    expect(second.result.current.cohorts.cowork).toBe('all');
    expect(second.result.current.cohorts.licence).toBe('recommended');
  });

  /**
   * A browser that lets the page read session storage but not write to it - a full quota, or older
   * Safari in private browsing. Reading the store would keep returning the old choice and the picker
   * would appear not to respond, so the choice moves to memory for the rest of the page.
   */
  it('still switches when the browser refuses to store the choice', () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('The quota has been exceeded.', 'QuotaExceededError');
    });
    try {
      const { result } = renderHook(() => useTimeSavedCohorts());

      act(() => result.current.setCohort('licence', 'all'));
      expect(result.current.cohorts.licence).toBe('all');

      act(() => result.current.setCohort('cowork', 'all'));
      expect(result.current.cohorts).toEqual({ licence: 'all', cowork: 'all' });

      act(() => result.current.setCohort('licence', 'recommended'));
      expect(result.current.cohorts).toEqual({ licence: 'recommended', cowork: 'all' });
    } finally {
      setItem.mockRestore();
    }
  });
});
