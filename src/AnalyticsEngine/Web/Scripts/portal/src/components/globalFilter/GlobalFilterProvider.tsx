import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { fetchEffectiveGlobalFilter } from '../../api/globalFilterApi';
import type { GlobalFilterEffective } from '../../types/globalFilter';

/**
 * The cookie that switches the administrator's global filter off for one administrator's own view -
 * `ReportScopeResolver.BypassCookie` on the server, which honours it only for a portal administrator.
 *
 * A cookie rather than a request header, so the Excel exports - plain links, which cannot send a header -
 * follow the same switch as the page they were opened from.
 */
export const GLOBAL_FILTER_BYPASS_COOKIE = 'GlobalFilterBypass';

/**
 * How long the page trusts what it last read before a navigation reads it again. The server caches the
 * filter for the same minute, so a change an administrator saves reaches every open page within about
 * that long - without polling.
 */
const STALE_AFTER_MS = 60_000;

export type GlobalFilterLoadStatus = 'loading' | 'ready' | 'error';

export interface GlobalFilterContextValue {
  status: GlobalFilterLoadStatus;
  /** What the server last said about the filter for this reader; null before the first answer. */
  effective: GlobalFilterEffective | null;
  /**
   * Changes whenever the figures a report shows would change - the filter was switched off or on, or an
   * administrator replaced it. The shell remounts the Insights pages on it, so every report fetches again
   * rather than showing figures from before the change under a bar describing after it.
   */
  viewKey: number;
  /** True while switching the filter off or on for the reader's own view. */
  switching: boolean;
  refresh: () => Promise<void>;
  /** Switches the filter off (true) or back on (false) for this administrator's own view. */
  setBypassed: (bypassed: boolean) => Promise<void>;
}

/** No provider - a component rendered on its own, as in most tests: there is no global filter. */
const NO_GLOBAL_FILTER: GlobalFilterContextValue = {
  status: 'ready',
  effective: null,
  viewKey: 0,
  switching: false,
  refresh: async () => {},
  setBypassed: async () => {},
};

const GlobalFilterContext = createContext<GlobalFilterContextValue>(NO_GLOBAL_FILTER);

/** Sets or clears the switch-off cookie. A session cookie: the filter comes back on with a new browser session. */
export function writeGlobalFilterBypassCookie(bypassed: boolean): void {
  const parts = bypassed ? [`${GLOBAL_FILTER_BYPASS_COOKIE}=1`] : [`${GLOBAL_FILTER_BYPASS_COOKIE}=`, 'max-age=0'];
  parts.push('path=/', 'SameSite=Strict');
  if (typeof window !== 'undefined' && window.location.protocol === 'https:') parts.push('Secure');
  document.cookie = parts.join('; ');
}

/** Whether the switch-off cookie is set, the way the server reads it. */
export function readGlobalFilterBypassCookie(): boolean {
  if (typeof document === 'undefined') return false;
  return document.cookie
    .split(';')
    .some((pair) => pair.trim() === `${GLOBAL_FILTER_BYPASS_COOKIE}=1`);
}

/** Whether the figures a reader is shown would differ between two answers. */
function viewChanged(before: GlobalFilterEffective | null, after: GlobalFilterEffective): boolean {
  if (!before) return false;
  return (
    before.revision !== after.revision ||
    before.active !== after.active ||
    before.applied !== after.applied ||
    before.bypassed !== after.bypassed ||
    before.invalid !== after.invalid
  );
}

interface State {
  status: GlobalFilterLoadStatus;
  effective: GlobalFilterEffective | null;
  viewKey: number;
}

/**
 * Holds the administrator's global filter as it applies to the signed-in reader, for the filter bar every
 * Insights page shows.
 *
 * Only ever describes the filter. Every report endpoint applies it on the server whatever this says, so a
 * failure to read it here costs the reader the explanation, never the protection.
 *
 * `value` fixes the context instead of reading the server - for tests.
 */
export function GlobalFilterProvider({
  children,
  value,
}: {
  children: ReactNode;
  value?: Partial<GlobalFilterContextValue>;
}) {
  if (value) {
    return <StaticGlobalFilterProvider value={value}>{children}</StaticGlobalFilterProvider>;
  }
  return <LiveGlobalFilterProvider>{children}</LiveGlobalFilterProvider>;
}

function StaticGlobalFilterProvider({ children, value }: { children: ReactNode; value: Partial<GlobalFilterContextValue> }) {
  const merged = useMemo(() => ({ ...NO_GLOBAL_FILTER, ...value }), [value]);
  return <GlobalFilterContext.Provider value={merged}>{children}</GlobalFilterContext.Provider>;
}

function LiveGlobalFilterProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<State>({ status: 'loading', effective: null, viewKey: 0 });
  const [switching, setSwitching] = useState(false);
  const loadedAt = useRef(0);
  const inFlight = useRef<AbortController | null>(null);
  const location = useLocation();

  const load = useCallback(async (remount: boolean): Promise<'ready' | 'error' | 'aborted'> => {
    inFlight.current?.abort();
    const controller = new AbortController();
    inFlight.current = controller;

    try {
      const next = await fetchEffectiveGlobalFilter(controller.signal);
      if (controller.signal.aborted) return 'aborted';
      loadedAt.current = Date.now();
      setState((previous) => ({
        status: 'ready',
        effective: next,
        viewKey: previous.viewKey + (remount || viewChanged(previous.effective, next) ? 1 : 0),
      }));
      return 'ready';
    } catch {
      if (controller.signal.aborted) return 'aborted';
      // What was known stays shown: a failed refresh does not mean the filter went away.
      setState((previous) => ({ ...previous, status: 'error', viewKey: previous.viewKey + (remount ? 1 : 0) }));
      return 'error';
    } finally {
      if (inFlight.current === controller) inFlight.current = null;
    }
  }, []);

  // Read on first render, and again on a later navigation once what was read is older than the server's
  // own cache - so a change an administrator made reaches a page left open, without polling.
  useEffect(() => {
    if (inFlight.current || Date.now() - loadedAt.current < STALE_AFTER_MS) return;
    void load(false);
  }, [location.pathname, load]);

  // The switch-off cookie is the browser's, not this tab's: switching the filter off in one tab changes what
  // every other tab's reports return. So when a tab comes back into view, a cookie that no longer agrees with
  // what it last read means its figures and its bar no longer match - read again and remount. Only while a
  // filter is defined: with none, the server reports it "not switched off" whatever the cookie says.
  const effectiveRef = useRef<GlobalFilterEffective | null>(null);
  effectiveRef.current = state.effective;
  useEffect(() => {
    const check = () => {
      if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return;
      const current = effectiveRef.current;
      if (!current?.canBypass || !current.active || inFlight.current) return;
      if (readGlobalFilterBypassCookie() !== current.bypassed) void load(true);
    };
    window.addEventListener('focus', check);
    document.addEventListener('visibilitychange', check);
    return () => {
      window.removeEventListener('focus', check);
      document.removeEventListener('visibilitychange', check);
    };
  }, [load]);

  useEffect(
    () => () => {
      inFlight.current?.abort();
      // Cleared here rather than when the aborted request settles: a remount - React's development
      // double-mount, or a test - runs its effects before then, and would otherwise skip the first read.
      inFlight.current = null;
    },
    [],
  );

  const refresh = useCallback(async () => {
    await load(false);
  }, [load]);

  const setBypassed = useCallback(
    async (bypassed: boolean) => {
      setSwitching(true);
      writeGlobalFilterBypassCookie(bypassed);
      try {
        // Remount whatever the answer: the cookie has changed, so every report's figures have too.
        const outcome = await load(true);
        if (outcome === 'error' && readGlobalFilterBypassCookie() === bypassed) {
          // The reports now follow the cookie - the server honours it for an administrator, and only an
          // administrator is offered the switch - so the bar must too, rather than keep describing the view
          // from before it. The status stays 'error' until a read succeeds.
          setState((previous) =>
            previous.effective?.canBypass && previous.effective.active
              ? { ...previous, effective: { ...previous.effective, bypassed, applied: !bypassed } }
              : previous,
          );
        }
      } finally {
        setSwitching(false);
      }
    },
    [load],
  );

  const contextValue = useMemo<GlobalFilterContextValue>(
    () => ({ ...state, switching, refresh, setBypassed }),
    [state, switching, refresh, setBypassed],
  );

  return <GlobalFilterContext.Provider value={contextValue}>{children}</GlobalFilterContext.Provider>;
}

export function useGlobalFilter(): GlobalFilterContextValue {
  return useContext(GlobalFilterContext);
}
