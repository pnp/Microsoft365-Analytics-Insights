import { lazy } from 'react';
import type { ComponentType } from 'react';

const RELOAD_FLAG = 'portal:chunk-reloaded';

/**
 * `sessionStorage`, guarded, as `api/http.ts` does it.
 *
 * `window.sessionStorage` is a getter that throws `SecurityError` where site data is blocked (some
 * privacy settings and embedded browsers), and `setItem` can throw as well. Unguarded, a chunk that
 * loaded perfectly well became a rejection the moment the flag was cleared, and the route failed.
 * `undefined` means storage could not be read at all.
 */
function readFlag(): string | null | undefined {
  try {
    return window.sessionStorage.getItem(RELOAD_FLAG);
  } catch {
    return undefined;
  }
}

/** True only when the flag is known to be stored, so the next failure will not reload again. */
function recordReload(): boolean {
  try {
    window.sessionStorage.setItem(RELOAD_FLAG, '1');
    return window.sessionStorage.getItem(RELOAD_FLAG) === '1';
  } catch {
    return false;
  }
}

function clearFlag(): void {
  try {
    window.sessionStorage.removeItem(RELOAD_FLAG);
  } catch {
    // Nothing could have been stored, so there is nothing to clear.
  }
}

/**
 * Wraps `React.lazy` so a stale code-split chunk recovers itself instead of breaking the route.
 *
 * Vite emits content-hashed chunks and empties `build/assets` on every build, so a rebuild or a
 * redeploy invalidates the chunk URLs baked into any page that is already open. The next route
 * change then fails with "Failed to fetch dynamically imported module: .../assets/HealthPage-*.js"
 * and the route silently renders nothing - the user just sees a dead page until they refresh.
 *
 * Reloading pulls a fresh index.html carrying the current hashes, which is all that is needed. The
 * sessionStorage flag makes that a one-shot, so a chunk that is missing for some other reason
 * (a broken build, a bad deploy) surfaces as a real error rather than an infinite reload loop. Where
 * the flag cannot be stored, nothing would stop that loop, so the error is surfaced straight away
 * and the reader reloads by hand from the route's error message.
 */
export function lazyWithReload<T extends ComponentType<any>>( // eslint-disable-line @typescript-eslint/no-explicit-any
  factory: () => Promise<{ default: T }>,
) {
  return lazy(() =>
    factory()
      .then((module) => {
        // Loaded fine, so let a future stale chunk have its own reload.
        clearFlag();
        return module;
      })
      .catch((error: unknown) => {
        if (readFlag() === null && recordReload()) {
          window.location.reload();
          // Deliberately never settles: the reload takes over before React can render anything.
          return new Promise<{ default: T }>(() => {});
        }

        throw error;
      }),
  );
}
