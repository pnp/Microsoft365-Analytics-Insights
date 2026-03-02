
export interface SpPageContextInfo {
  userLoginName: string;
  webAbsoluteUrl: string;
  siteAbsoluteUrl: string;
  webTitle: string;
}

export interface SitesTrackedByExtension {
  siteUrls: string[];
  lastUrlTracked: string | undefined;
}

/** Escapes a string for safe inclusion inside a JavaScript single-quoted string literal. */
export function escapeForJsString(value: string): string;

export interface IAiTrackerModernApplicationCustomizerProperties {
  appInsightsConnectionStringHash: string;
  insightsWebRootUrlHash?: string;
  cacheToken: string;
}
