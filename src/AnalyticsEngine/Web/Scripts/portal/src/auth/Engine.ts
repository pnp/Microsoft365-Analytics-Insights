import { msalApp, requiresInteraction, GRAPH_SCOPES } from './auth-utils';
import type { AuthenticationResult } from '@azure/msal-browser';

export function acquireGraphToken(): Promise<AuthenticationResult> {
  const account = msalApp.getAllAccounts()[0];
  const accessTokenRequest = {
    account: account,
    scopes: [
      GRAPH_SCOPES.OPENID,
      GRAPH_SCOPES.PROFILE,
      GRAPH_SCOPES.USER_READ,
      GRAPH_SCOPES.TEAMS_READ_BASIC,
      GRAPH_SCOPES.TEAMS_MESSAGES_READ,
      GRAPH_SCOPES.OFFLINE,
    ],
  };

  return msalApp.acquireTokenSilent(accessTokenRequest).catch((error) => {
    // Fall back to an interactive popup only when consent / interaction is actually required.
    if (requiresInteraction(error.errorCode)) {
      return msalApp.acquireTokenPopup(accessTokenRequest);
    } else {
      console.error('Non-interactive error:', error.errorCode);
      return Promise.reject(error);
    }
  });
}
