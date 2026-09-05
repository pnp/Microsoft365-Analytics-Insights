// Runtime configuration injected by the host page (index.html / the ASP.NET site).
// Centralised here so individual components don't each re-declare the Window shape.
declare global {
  interface Window {
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
    /** Base endpoint for the lite in-app Reports API (enabled areas + weekly usage charts). */
    o365AnalyticsReportsAPI: string;
    /** Base endpoint for the Copilot licence-adoption API (summary, user lists, CSV exports). */
    o365AnalyticsCopilotAdoptionAPI: string;
    /** Base endpoint for the Licence activity API (availability, overview, users, Excel export). */
    o365AnalyticsLicenceActivityAPI: string;
    /** Endpoint for the system-health ("is it working?") API. */
    o365AnalyticsHealthAPI: string;
    /** Endpoint for the "is there a newer release?" check. */
    o365AnalyticsUpdateCheckAPI: string;
  }
}

export {};
