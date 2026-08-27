/**
 * Shared fetch wrapper for the portal's calls to the site's own `[Authorize]`'d API.
 *
 * Why this exists: the site signs the admin in with a server-side OIDC redirect and keeps the
 * session in an encrypted auth cookie. When that session ends - it expires, or the App Service
 * recycles and the new instance can't decrypt the old cookie - the next API call is no longer
 * authenticated. The OIDC middleware runs in Active mode, so it answers with a 302 to
 * login.microsoftonline.com; `fetch` follows that cross-origin, the login page carries no CORS
 * headers, and the call rejects with an opaque `TypeError: Failed to fetch`. Leave the portal open
 * long enough and every page starts failing with a network-looking error that is really just
 * "please sign in again", and nothing recovers until the user reloads by hand.
 *
 * `Startup.ConfigureAuth` now suppresses that redirect for API requests and returns a plain 401
 * with the `X-Auth-Session-Expired` header instead. This helper watches for it and re-authenticates
 * with a full-page navigation, which is the one thing that CAN complete the OIDC round-trip - and
 * usually completes silently, because the user's Entra session normally outlives the site's.
 *
 * The header matters: a bare 401 is NOT enough to conclude the session is gone. `SiteTokenAPI`
 * returns 401 to mean "you are signed in, but I have no Graph refresh token for you", and the Teams
 * page shows a specific message for that. Only the header-flagged 401 triggers a sign-in.
 */

/** Set by the server on the 401 that replaces the sign-in redirect. Keep in step with `Startup.SessionExpiredHeader`. */
const SESSION_EXPIRED_HEADER = 'X-Auth-Session-Expired';

/** One-shot guard, so a session that cannot be re-established fails loudly instead of looping. */
const REAUTH_FLAG = 'portal:reauth-attempted';

/** Where the user was, so re-authenticating doesn't dump them back on the default page. */
const RETURN_ROUTE_KEY = 'portal:return-route';

/**
 * Set once this document starts navigating to sign-in.
 *
 * Pages fire several API calls in parallel, so a request that was already in flight can complete
 * *after* re-authentication has begun - a long-running query, or one served by an instance that
 * could still read the cookie. Without this, that late success would clear the one-shot flag below
 * before the document unloaded, re-arming the bounce and defeating the loop guard. Module state is
 * the right scope: it dies with the document, so the freshly loaded page starts clean.
 */
let reauthNavigationStarted = false;

/**
 * Handed to every caller once the sign-in navigation has begun. It deliberately never settles: the
 * document is on its way out, so no caller should render an error - or anything else - over a page
 * that is being replaced.
 */
const NEVER_SETTLES: Promise<Response> = new Promise<Response>(() => {});

/** Thrown when the session has expired and re-authenticating has already been tried once. */
export class SessionExpiredError extends Error {
  constructor() {
    super('Your session has expired. Reload the page to sign in again.');
    this.name = 'SessionExpiredError';
  }
}

/**
 * Restores the route the user was on before an expired session bounced them through sign-in.
 * Called once from the app entry point, before the router reads the URL.
 *
 * The SPA uses HashRouter, so the route lives in the fragment and never reaches the server - the
 * OIDC round-trip always lands back on the default page. Stashing it is the only way to return the
 * user to where they were.
 */
export function restoreRouteAfterReauth(): void {
  const saved = sessionStorage.getItem(RETURN_ROUTE_KEY);
  sessionStorage.removeItem(RETURN_ROUTE_KEY);

  if (saved && saved !== window.location.hash) {
    window.location.hash = saved;
  }
}

function reauthenticate(): void {
  reauthNavigationStarted = true;
  sessionStorage.setItem(REAUTH_FLAG, '1');

  if (window.location.hash) {
    sessionStorage.setItem(RETURN_ROUTE_KEY, window.location.hash);
  }

  // A full-page navigation, not a fetch: only a top-level request can follow the redirect to Entra
  // and back. '/' is [Authorize]'d, so it triggers the sign-in the failed API call could not.
  window.location.assign('/');
}

/**
 * `fetch` for the site's own API. Always sends the auth cookie, marks the call as an XHR so the
 * server answers with 401 rather than a sign-in redirect, and transparently re-authenticates a
 * session that has expired.
 *
 * On an expired session the returned promise deliberately never settles - the page is navigating
 * away, so callers must not render an error for a request that is being retried by the sign-in.
 */
export async function apiFetch(input: string, init: RequestInit = {}): Promise<Response> {
  const response = await fetch(input, {
    credentials: 'same-origin',
    ...init,
    headers: {
      'X-Requested-With': 'XMLHttpRequest',
      ...(init.headers ?? {}),
    },
  });

  if (response.status === 401 && response.headers.get(SESSION_EXPIRED_HEADER)) {
    // A sibling request may already have started the navigation - pages fire several calls at once.
    if (reauthNavigationStarted) {
      return NEVER_SETTLES;
    }

    if (sessionStorage.getItem(REAUTH_FLAG) === null) {
      reauthenticate();
      return NEVER_SETTLES;
    }

    // This document came back FROM a sign-in and the session is still gone, so signing in again
    // would only loop. Surface it so the user sees a real error instead of a blank page.
    throw new SessionExpiredError();
  }

  // A response that got through means the session is healthy, so let a later expiry have its own
  // sign-in attempt. Skipped once we're navigating away, so a late in-flight success can't re-arm
  // the guard for the document that is already on its way to sign-in.
  if (!reauthNavigationStarted) {
    sessionStorage.removeItem(REAUTH_FLAG);
  }

  return response;
}
