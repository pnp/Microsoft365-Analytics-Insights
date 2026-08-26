/**
 * The Microsoft Graph access token the site mints for the signed-in admin, returned by
 * `POST api/SiteTokenAPI` (server-side `JSonToken`).
 *
 * Deliberately minimal: `accessToken` is the only field anything consumes. This replaces MSAL's
 * `AuthenticationResult`, which the page used to be typed against back when there was a
 * client-side MSAL sign-in fallback.
 */
export interface GraphAccessToken {
  accessToken: string;
}
