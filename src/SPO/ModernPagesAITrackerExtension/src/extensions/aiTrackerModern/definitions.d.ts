
export interface spPageContextInfo {
  userLoginName: string,
  webAbsoluteUrl: string,
  siteAbsoluteUrl: string,
  webTitle: string
}

export interface SitesTrackedByExtension {
  siteUrls: string[];
  lastUrlTracked: string | undefined;
}

export interface IAiTrackerModernApplicationCustomizerProperties {
  appInsightsConnectionStringHash: string;
  insightsWebRootUrlHash?: string;
  cacheToken: string;
}
