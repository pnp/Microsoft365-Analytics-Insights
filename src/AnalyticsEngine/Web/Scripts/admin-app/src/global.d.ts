import type { AuthenticationResult } from '@azure/msal-browser';

// Runtime configuration injected by the host page (index.html / the ASP.NET site).
// Centralised here so individual components don't each re-declare the Window shape.
declare global {
  interface Window {
    /** Azure AD app (client) id used for the client-side MSAL fallback. */
    o365AnalyticsClientId: string;
    /** Azure AD authority (tenant) URL used for the client-side MSAL fallback. */
    o365AnalyticsAuthority: string;
    /** Redirect URI for MSAL popup/redirect flows. */
    o365AnalyticsRedirectUri: string;
    /** Graph token acquired client-side, stashed for the Teams auth PUT call. */
    o365AnalyticsTeamsToken: AuthenticationResult;
    /** POST endpoint that returns the server-side delegated OAuth token. */
    o365AnalyticsTokenAPI: string;
    /** Endpoint for getting/setting Teams deep-analytics authorisation. */
    o365AnalyticsAuthAPI: string;
    /** Base endpoint for the user data lookup API. */
    o365AnalyticsUserLookupAPI: string;
    /** Endpoint for the system status (home page) API. */
    o365AnalyticsSystemStatusAPI: string;
    /** Endpoint for the install log (config history) API. */
    o365AnalyticsInstallLogAPI: string;
    /** Base endpoint for the profiling status (data freshness + trace logs) API. */
    o365AnalyticsProfilingStatusAPI: string;
    /** Endpoint for the system-health ("is it working?") API. */
    o365AnalyticsHealthAPI: string;
  }
}

export {};
