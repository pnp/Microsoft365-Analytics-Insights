import { lazy } from 'react';
import type { ComponentType } from 'react';

const RELOAD_FLAG = 'portal:chunk-reloaded';

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
 * (a broken build, a bad deploy) surfaces as a real error rather than an infinite reload loop.
 */
export function lazyWithReload<T extends ComponentType<any>>( // eslint-disable-line @typescript-eslint/no-explicit-any
  factory: () => Promise<{ default: T }>,
) {
  return lazy(() =>
    factory()
      .then((module) => {
        // Loaded fine, so let a future stale chunk have its own reload.
        sessionStorage.removeItem(RELOAD_FLAG);
        return module;
      })
      .catch((error: unknown) => {
        if (sessionStorage.getItem(RELOAD_FLAG) === null) {
          sessionStorage.setItem(RELOAD_FLAG, '1');
          window.location.reload();
          // Deliberately never settles: the reload takes over before React can render anything.
          return new Promise<{ default: T }>(() => {});
        }

        throw error;
      }),
  );
}
