import { useCallback, useEffect, useState } from 'react';
import { fetchUserFilterDimensions } from '../../api/userFilterApi';
import type { UserFilterDimensionList } from '../../types/userFilter';

/**
 * The attributes a filter can use, loaded once and shared by every filter bar on the page.
 *
 * Module-level rather than per component: the list is the same for every report and changes only when
 * an administrator adds or edits a custom organisation type, so refetching it on every mount - every
 * tab switch - would be waste. Held for a few minutes, then refreshed on the next mount - and dropped
 * at once when this session changes the organisation types (`invalidateUserFilterDimensions`), so a
 * type just created or renamed is on offer the moment the admin goes back to a report.
 */
const FRESH_FOR_MS = 5 * 60 * 1000;

let cached: { list: UserFilterDimensionList; loadedAt: number } | null = null;
let inFlight: Promise<UserFilterDimensionList> | null = null;
let generation = 0;

function load(force: boolean): Promise<UserFilterDimensionList> {
  if (!force && cached && Date.now() - cached.loadedAt < FRESH_FOR_MS) return Promise.resolve(cached.list);
  if (inFlight) return inFlight;

  const started = generation;
  const request: Promise<UserFilterDimensionList> = fetchUserFilterDimensions()
    .then((list) => {
      // A list read before an invalidation describes the types as they were: still this caller's
      // answer, but not one to keep.
      if (started === generation) cached = { list, loadedAt: Date.now() };
      return list;
    })
    .finally(() => {
      if (inFlight === request) inFlight = null;
    });
  inFlight = request;
  return request;
}

/** Drops the shared list, so the next filter bar to mount reads it again. Call after changing the organisation types. */
export function invalidateUserFilterDimensions(): void {
  generation++;
  cached = null;
  inFlight = null;
}

/** Test-only: forget the shared list. */
export function resetUserFilterDimensionsCache(): void {
  invalidateUserFilterDimensions();
}

export interface UserFilterDimensionsState {
  list: UserFilterDimensionList | null;
  loading: boolean;
  error: string | null;
  reload: () => void;
}

export function useUserFilterDimensions(): UserFilterDimensionsState {
  const [list, setList] = useState<UserFilterDimensionList | null>(cached?.list ?? null);
  const [loading, setLoading] = useState(!cached);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    load(attempt > 0)
      .then((result) => {
        if (!cancelled) setList(result);
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [attempt]);

  const reload = useCallback(() => setAttempt((a) => a + 1), []);

  return { list, loading, error, reload };
}
