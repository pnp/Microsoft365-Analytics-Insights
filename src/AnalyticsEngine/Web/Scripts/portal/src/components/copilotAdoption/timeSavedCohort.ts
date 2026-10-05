import { useCallback, useMemo, useSyncExternalStore } from 'react';

/**
 * Who each time-saved headline models: the people recommended, or everyone they were drawn from.
 *
 * Both estimates lead with a recommended cohort - the people recommended for a Microsoft 365 Copilot
 * licence, and the people ready for Cowork now - because that is the decision each tab supports. But on
 * a tenant where nobody uses Microsoft 365 heavily enough to be recommended, the recommended cohort is
 * empty and there is nothing to quote. So each estimate can also model everyone: every licence
 * candidate, and every Copilot seat holder.
 *
 * The reader's choice is kept in this browser tab's session storage, like their assumptions, and every
 * surface that quotes a model reads it through here - the headline, the calculator and the overview's
 * time-back tile - so no two of them can describe different people. It is never written to the
 * database: one admin's view cannot become another's report.
 */

/**
 * Who a headline models: the people recommended, everyone they were drawn from, or - for Cowork only -
 * the people without a Copilot licence.
 */
export type TimeSavedCohort = 'recommended' | 'all' | 'withoutLicence';

/** The two time-saved models, each with its own cohort. */
export type TimeSavedModel = 'licence' | 'cowork';

export type TimeSavedCohorts = Record<TimeSavedModel, TimeSavedCohort>;

const MODELS: readonly TimeSavedModel[] = ['licence', 'cowork'];

/**
 * The cohorts each model can show. The licence estimate is already about people without a licence, so
 * "without a licence" is a choice for Cowork only: what Cowork could add once they were licensed.
 */
const COHORTS_FOR: Record<TimeSavedModel, readonly TimeSavedCohort[]> = {
  licence: ['recommended', 'all'],
  cowork: ['recommended', 'all', 'withoutLicence'],
};

/** The product's default: both headlines lead with the people recommended. */
export const DEFAULT_TIME_SAVED_COHORTS: Readonly<TimeSavedCohorts> = Object.freeze({
  licence: 'recommended',
  cowork: 'recommended',
});

/** Session-storage key. Versioned so a future change of shape cannot misread an old value. */
export const TIME_SAVED_COHORT_STORAGE_KEY = 'm365ai.copilotAdoption.timeSavedCohort.v1';

/**
 * Parses a stored choice, keeping only values this build understands. Anything else - a hand-edited
 * value, a model from a future version - falls back to the default rather than being guessed at.
 */
export function parseTimeSavedCohorts(raw: string): TimeSavedCohorts {
  const cohorts: TimeSavedCohorts = { ...DEFAULT_TIME_SAVED_COHORTS };
  if (!raw) return cohorts;
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return cohorts;
  }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return cohorts;

  for (const model of MODELS) {
    const value = (parsed as Record<string, unknown>)[model];
    if (typeof value === 'string' && (COHORTS_FOR[model] as readonly string[]).includes(value)) {
      cohorts[model] = value as TimeSavedCohort;
    }
  }
  return cohorts;
}

/**
 * `sessionStorage`, guarded: reading the property itself throws `SecurityError` when site data is
 * blocked. A blocked store means the choice lasts only as long as the page, which is a degradation,
 * not a failure.
 */
function sessionStore(): Storage | undefined {
  try {
    return typeof window === 'undefined' ? undefined : window.sessionStorage;
  } catch {
    return undefined;
  }
}

/** The choice held in memory when session storage cannot be used, so switching still works. */
let memoryFallback = '';

/**
 * Set once a write to session storage has failed - a full quota, or a browser (older Safari in private
 * browsing, for one) that lets the page read storage but not write to it. From then on the choice lives
 * in memory for the rest of the page: reading the store would keep returning the value from before the
 * failed write, and the picker would appear not to respond.
 */
let storageFailed = false;

function readRaw(): string {
  const store = sessionStore();
  if (!store || storageFailed) return memoryFallback;
  try {
    return store.getItem(TIME_SAVED_COHORT_STORAGE_KEY) ?? '';
  } catch {
    return memoryFallback;
  }
}

const listeners = new Set<() => void>();

function writeRaw(value: string): void {
  memoryFallback = value;
  const store = sessionStore();
  if (store && !storageFailed) {
    try {
      if (value) store.setItem(TIME_SAVED_COHORT_STORAGE_KEY, value);
      else store.removeItem(TIME_SAVED_COHORT_STORAGE_KEY);
    } catch {
      storageFailed = true;
    }
  }
  for (const listener of [...listeners]) listener();
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/** Test-only: forget the stored choice, and any earlier failure to store one. */
export function resetTimeSavedCohortStore(): void {
  storageFailed = false;
  writeRaw('');
}

export interface TimeSavedCohortState {
  /** The reader's choice for each model: `recommended` unless they picked everyone. */
  cohorts: TimeSavedCohorts;
  setCohort: (model: TimeSavedModel, cohort: TimeSavedCohort) => void;
}

/** The cohorts chosen in this browser tab, shared by every component that quotes a time-saved model. */
export function useTimeSavedCohorts(): TimeSavedCohortState {
  const raw = useSyncExternalStore(subscribe, readRaw, () => '');
  const cohorts = useMemo(() => parseTimeSavedCohorts(raw), [raw]);

  const setCohort = useCallback((model: TimeSavedModel, cohort: TimeSavedCohort) => {
    const next = { ...parseTimeSavedCohorts(readRaw()), [model]: cohort };
    // Only a choice that differs from the default is kept, so an empty store means "the defaults".
    const kept: Partial<TimeSavedCohorts> = {};
    for (const key of MODELS) {
      if (next[key] !== DEFAULT_TIME_SAVED_COHORTS[key]) kept[key] = next[key];
    }
    writeRaw(Object.keys(kept).length > 0 ? JSON.stringify(kept) : '');
  }, []);

  return { cohorts, setCohort };
}

/** The cohort a headline actually shows, and why. */
export interface ResolvedTimeSavedCohort<P> {
  cohort: TimeSavedCohort;
  projection: P;
  /**
   * True when the reader's choice had nobody in it, so the other cohort stands in - above all when
   * nobody is recommended and everyone is modelled instead. The headline says so rather than quietly
   * describing different people from the ones the reader asked for.
   */
  fallback: boolean;
}

/**
 * Picks the projection a headline shows: the reader's choice when it has anybody in it, otherwise the
 * recommended cohort, otherwise everyone, otherwise nothing.
 *
 * Standing in for an empty cohort is the point of the "all" option. A tenant where nobody is recommended
 * used to get no licence headline at all, and every Copilot seat holder already stood in for an empty
 * Cowork cohort; both now say that is what happened. `withoutLicence` is Cowork's third choice - the
 * people without a Copilot seat - and is only ever shown when chosen: it is a different population, not
 * a stand-in for an empty one.
 */
export function resolveTimeSavedCohort<P>(
  chosen: TimeSavedCohort,
  recommended: P | null | undefined,
  all: P | null | undefined,
  withoutLicence?: P | null,
): ResolvedTimeSavedCohort<P> | null {
  if (chosen === 'withoutLicence' && withoutLicence) return { cohort: 'withoutLicence', projection: withoutLicence, fallback: false };
  if (chosen === 'all' && all) return { cohort: 'all', projection: all, fallback: false };
  if (recommended) return { cohort: 'recommended', projection: recommended, fallback: chosen !== 'recommended' };
  if (all) return { cohort: 'all', projection: all, fallback: true };
  return null;
}
