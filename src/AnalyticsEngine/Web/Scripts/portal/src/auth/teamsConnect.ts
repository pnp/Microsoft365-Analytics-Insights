import type { TranslationKey } from '../i18n/catalog';

/**
 * The Teams connection: how an admin grants the portal the delegated Teams permissions that Teams deep
 * analytics needs (issue #670).
 *
 * Signing in to the portal doesn't ask for these, so a tenant that hasn't granted them - often because
 * it doesn't use Teams deep analytics - can still use everything else. The Teams permissions page sends
 * the admin to {@link TEAMS_CONNECT_URL} instead (`AccountController.ConnectTeams`). That re-runs the
 * Entra ID sign-in with the Teams scopes and comes back to the page. When Entra ID says no, the server
 * adds an outcome key and Entra ID's error code to the route's query string.
 *
 * The server sends keys, not text. {@link TEAMS_CONNECT_OUTCOME_KEYS} turns each key into a translated
 * sentence, and `i18n/lint/serverAuthoredText.test.ts` checks it against the C# constants in
 * `App_Start/DelegatedGraphConsent.cs`.
 */

/** Full-page navigation target: a server round trip through Entra ID, not an API call. */
export const TEAMS_CONNECT_URL = '/Account/ConnectTeams';

/** `DelegatedGraphConsent.OutcomeParameter` */
export const TEAMS_CONNECT_OUTCOME_PARAM = 'teamsConnect';

/** `DelegatedGraphConsent.ErrorCodeParameter` */
export const TEAMS_CONNECT_ERROR_PARAM = 'teamsConnectError';

/** Outcome keys the server can send (`DelegatedGraphConsent.Outcome*`), and the sentence for each. */
export const TEAMS_CONNECT_OUTCOME_KEYS = {
  consent_required: 'admin.teamsPermissions.connect.outcome.consent_required',
  access_denied: 'admin.teamsPermissions.connect.outcome.access_denied',
  failed: 'admin.teamsPermissions.connect.outcome.failed',
} as const satisfies Record<string, TranslationKey>;

export interface TeamsConnectOutcome {
  /** Catalog key for the explanation. */
  messageKey: TranslationKey;
  /** Entra ID's error code, e.g. `AADSTS65001`, when the server passed one on. */
  errorCode: string | null;
}

/** The same shape the server allows through; anything else is dropped rather than shown. */
const DISPLAYABLE_ERROR_CODE = /^[A-Za-z0-9_]{1,40}$/;

/**
 * Reads a failed connection's outcome from the route's query string, or `null` when there isn't one.
 *
 * A key this build doesn't know - a newer server, a hand-edited link - still reads as a failure, with
 * the generic explanation, rather than being ignored.
 */
export function readTeamsConnectOutcome(params: URLSearchParams): TeamsConnectOutcome | null {
  const outcome = params.get(TEAMS_CONNECT_OUTCOME_PARAM);
  if (!outcome) {
    return null;
  }

  const known = Object.prototype.hasOwnProperty.call(TEAMS_CONNECT_OUTCOME_KEYS, outcome);
  const messageKey = known
    ? TEAMS_CONNECT_OUTCOME_KEYS[outcome as keyof typeof TEAMS_CONNECT_OUTCOME_KEYS]
    : TEAMS_CONNECT_OUTCOME_KEYS.failed;

  const rawCode = params.get(TEAMS_CONNECT_ERROR_PARAM);
  const errorCode = rawCode && DISPLAYABLE_ERROR_CODE.test(rawCode) ? rawCode : null;

  return { messageKey, errorCode };
}

/** Removes the outcome from a query string once it has been read, so a refresh doesn't show it again. */
export function withoutTeamsConnectOutcome(params: URLSearchParams): URLSearchParams | null {
  if (!params.has(TEAMS_CONNECT_OUTCOME_PARAM) && !params.has(TEAMS_CONNECT_ERROR_PARAM)) {
    return null;
  }

  const next = new URLSearchParams(params);
  next.delete(TEAMS_CONNECT_OUTCOME_PARAM);
  next.delete(TEAMS_CONNECT_ERROR_PARAM);
  return next;
}
