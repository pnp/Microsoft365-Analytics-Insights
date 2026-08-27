import { apiFetch } from '../api/http';
import type { GraphAccessToken } from '../types/graphToken';

/**
 * Gets a Microsoft Graph access token for the signed-in admin from `POST api/SiteTokenAPI`.
 *
 * Used for the SPA's **direct** calls to `graph.microsoft.com` (the Teams page's profile and
 * joined-teams lookups). It is deliberately NOT used for the Teams authorisation save: that goes to
 * this site's own API, and `TeamsAuthAPIController.Put` authorises each Team with the refresh token
 * it already holds in the auth cookie, so a token in the request body would simply be ignored.
 *
 * Always asks the server for a fresh token rather than caching one on the page. Graph access tokens
 * last roughly an hour, so a token minted when a page mounted can easily be dead by the time the
 * admin acts on it. The server mints a new one from the long-lived refresh token on every call, so
 * fetching at the point of use is both correct and cheap.
 *
 * Returns `null` when the site is signed in but has no Graph refresh token for this session (the
 * caller shows a "sign out and back in" message). A genuinely expired *site* session is handled by
 * {@link apiFetch}, which re-authenticates instead of returning.
 */
export async function fetchGraphToken(): Promise<GraphAccessToken | null> {
  // Undefined when the SPA runs outside ASP.NET (the Vite dev server), where there is no token API.
  if (!window.o365AnalyticsTokenAPI) {
    console.error("Couldn't get server-side OAuth token from website: no token endpoint configured.");
    return null;
  }

  const response = await apiFetch(window.o365AnalyticsTokenAPI, { method: 'POST' }).catch((error: unknown) => {
    console.error("Couldn't get server-side OAuth token from website.");
    console.error(error);
    return null;
  });

  if (!response || !response.ok) {
    return null;
  }

  return response.json().catch((error: unknown) => {
    console.error('Error deserialising server-side OAuth token:');
    console.error(error);
    return null;
  }) as Promise<GraphAccessToken | null>;
}
