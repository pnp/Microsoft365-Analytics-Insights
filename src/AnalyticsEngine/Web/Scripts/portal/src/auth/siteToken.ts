import { apiFetch } from '../api/http';
import type { TranslationKey } from '../i18n/catalog';
import { translateActive } from '../i18n/runtime';
import type { GraphAccessToken } from '../types/graphToken';

/**
 * Codes `POST api/SiteTokenAPI` answers a 503 with (`SiteTokenAPIController.*Code`), and the sentence for each.
 * The API sends the code, never text; `i18n/lint/serverAuthoredText.test.ts` checks this against the C#.
 */
export const SITE_TOKEN_ERROR_KEYS = {
  entraUnavailable: 'admin.teamsPermissions.entraUnavailable.message',
} as const satisfies Record<string, TranslationKey>;

export type SiteTokenErrorCode = keyof typeof SITE_TOKEN_ERROR_KEYS;

/**
 * The site couldn't mint a Graph token for a reason connecting again won't fix: Microsoft Entra ID couldn't be
 * reached, timed out, was throttling or failing. The Teams connection's own sign-in ends by redeeming a code at
 * that same token endpoint, from the same server, so the remedy is to try again later.
 */
export class SiteTokenUnavailableError extends Error {
  readonly code: SiteTokenErrorCode;
  readonly messageKey: TranslationKey;

  constructor(code: SiteTokenErrorCode) {
    super(translateActive(SITE_TOKEN_ERROR_KEYS[code]));
    this.name = 'SiteTokenUnavailableError';
    this.code = code;
    this.messageKey = SITE_TOKEN_ERROR_KEYS[code];
  }
}

async function siteTokenUnavailable(response: Response): Promise<SiteTokenUnavailableError | null> {
  if (response.status !== 503) return null;

  try {
    const body = (await response.clone().json()) as { code?: unknown } | null;
    const code = body?.code;
    return typeof code === 'string' && Object.prototype.hasOwnProperty.call(SITE_TOKEN_ERROR_KEYS, code)
      ? new SiteTokenUnavailableError(code as SiteTokenErrorCode)
      : null;
  } catch {
    return null;
  }
}

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
 * Returns `null` when the site is signed in but has no working Graph token for this session: the
 * admin hasn't connected Microsoft Teams yet (signing in no longer captures one - issue #670), or
 * the stored token has expired or been revoked. The caller then offers "Connect to Microsoft Teams"
 * (`src/auth/teamsConnect.ts`). A genuinely expired *site* session is handled by
 * {@link apiFetch}, which re-authenticates instead of returning.
 *
 * Throws {@link SiteTokenUnavailableError} when the site answered with a code instead - Microsoft
 * Entra ID couldn't be reached - because connecting again would fail the same way.
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

  if (!response) {
    return null;
  }

  const unavailable = await siteTokenUnavailable(response);
  if (unavailable) {
    throw unavailable;
  }

  if (!response.ok) {
    return null;
  }

  return response.json().catch((error: unknown) => {
    console.error('Error deserialising server-side OAuth token:');
    console.error(error);
    return null;
  }) as Promise<GraphAccessToken | null>;
}
