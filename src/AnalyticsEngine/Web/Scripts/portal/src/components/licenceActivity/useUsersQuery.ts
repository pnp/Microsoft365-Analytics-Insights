import { useEffect, useRef, useState } from 'react';
import { fetchUsers } from '../../api/licenceActivityApi';
import type { LicenceActivityUsers, UsersParams } from '../../types/licenceActivity';

export interface UsersQueryState {
  data: LicenceActivityUsers | null;
  loading: boolean;
  error: unknown;
  reload: () => void;
}

interface KeyedResult {
  key: string;
  data: LicenceActivityUsers | null;
  error: unknown;
}

/**
 * Owns a single drill-down users request, with three guarantees the correctness pass calls for:
 *
 *  - CANCELLABLE: each run gets its own AbortController and the effect cleanup aborts it, so changing
 *    the licence / workload / page (or unmounting) actually stops the in-flight request.
 *  - STALE-SAFE: a monotonic sequence number drops any superseded response that still resolves.
 *  - REQUEST-SCOPE BOUND: the loaded result is tagged with the params key it was fetched for, and only
 *    surfaced while that key is still current. When the key changes (a new licence/workload/filter),
 *    the previous result's rows AND snapshot id are hidden on THAT SAME render - before the new fetch
 *    even starts - so an old licence can never be shown or exported against the new selection, and a
 *    different-key failure cannot fall back to the old population. Old data is preserved only across a
 *    same-key manual reload.
 */
export function useUsersQuery(params: UsersParams | null): UsersQueryState {
  const key = params ? JSON.stringify(params) : null;
  const [result, setResult] = useState<KeyedResult | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const seqRef = useRef(0);

  useEffect(() => {
    // Bump on every run (including going idle) so a late/aborted response from any prior run is dropped.
    const mySeq = (seqRef.current += 1);
    if (!params || key === null) return;

    const controller = new AbortController();
    fetchUsers(params, controller.signal)
      .then((r) => {
        if (mySeq !== seqRef.current) return; // superseded
        setResult({ key, data: r, error: null });
      })
      .catch((e) => {
        if (mySeq !== seqRef.current || controller.signal.aborted) return;
        if (e instanceof DOMException && e.name === 'AbortError') return;
        setResult({ key, data: null, error: e });
      });

    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, reloadKey]);

  // Only surface a result that belongs to the CURRENT key. A key change hides the previous key's data
  // and error on this render, before the effect that fetches the new key runs.
  const belongs = result !== null && result.key === key;
  const data = belongs ? result!.data : null;
  const error = belongs ? result!.error : null;
  const loading = key !== null && !belongs;

  return { data, loading, error, reload: () => setReloadKey((k) => k + 1) };
}
