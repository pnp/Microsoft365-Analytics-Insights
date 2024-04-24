import { PublicClientApplication, LogLevel } from "@azure/msal-browser";

export const requiresInteraction = (errorMessage : string) => {
    if (!errorMessage || !errorMessage.length) {
        return false;
    }

    return (
        errorMessage.indexOf("consent_required") > -1 ||
        errorMessage.indexOf("interaction_required") > -1 ||
        errorMessage.indexOf("login_required") > -1
    );
};

export const fetchMsGraph = async (url : string, accessToken : string) => {
    const response = await fetch(url, {
        headers: {
            Authorization: `Bearer ${accessToken}`
        }
    });

    return response.json();
};

export const isIE = () => {
    const ua = window.navigator.userAgent;
    const msie = ua.indexOf("MSIE ") > -1;
    const msie11 = ua.indexOf("Trident/") > -1;

    // If you as a developer are testing using Edge InPrivate mode, please add "isEdge" to the if check
    // const isEdge = ua.indexOf("Edge/") > -1;

    return msie || msie11;
};

export const GRAPH_SCOPES = {
    OPENID: "openid",
    PROFILE: "profile",
    USER_READ: "User.Read",
    TEAMS_READ_BASIC: "Team.ReadBasic.All",
    TEAMS_MESSAGES_READ: "ChannelMessage.Read.All",
    OFFLINE: "offline_access"
};

export const GRAPH_ENDPOINTS = {
    ME: "https://graph.microsoft.com/v1.0/me",
    JOINED_TEAMS: "https://graph.microsoft.com/v1.0/me/joinedTeams?$select=id,displayName"
};

export const GRAPH_REQUESTS = {
    LOGIN: {
        scopes: [
            GRAPH_SCOPES.OPENID,
            GRAPH_SCOPES.PROFILE,
            GRAPH_SCOPES.USER_READ,
            GRAPH_SCOPES.TEAMS_READ_BASIC,
            GRAPH_SCOPES.TEAMS_MESSAGES_READ,
            GRAPH_SCOPES.OFFLINE
        ]
    },
    TEAMS_READ: {
        scopes: [GRAPH_SCOPES.TEAMS_MESSAGES_READ]
    }
};


export const msalApp = new PublicClientApplication({
    auth: {
        clientId: window.o365AnalyticsClientId,
        authority: window.o365AnalyticsAuthority,
        redirectUri: window.o365AnalyticsRedirectUri,
        postLogoutRedirectUri: window.o365AnalyticsRedirectUri,
        navigateToLoginRequestUrl: false
    },
    cache: {
        cacheLocation: "localStorage", // Configures cache location. "sessionStorage" is more secure, but "localStorage" gives you SSO.
        storeAuthStateInCookie: false, // If you wish to store cache items in cookies as well as browser cache, set this to "true".
      },
      system: {
        loggerOptions: {
          loggerCallback: (level, message, containsPii) => {
            switch (level) {
              case LogLevel.Error:
                console.error(message);
                return;
              case LogLevel.Info:
                console.info(message);
                return;
              case LogLevel.Verbose:
                console.debug(message);
                return;
              case LogLevel.Warning:
                console.warn(message);
                return;
            }
          }
        }
    }
});
