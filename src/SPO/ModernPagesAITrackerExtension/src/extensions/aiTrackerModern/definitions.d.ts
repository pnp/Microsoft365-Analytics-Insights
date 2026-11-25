import type { NavigationVisits } from './NavigationVisits';

export interface SpPageContextInfo {
  userLoginName: string;
  webAbsoluteUrl: string;
  siteAbsoluteUrl: string;
  webTitle: string;
}

export interface SitesTrackedByExtension {
  siteUrls: string[];
  visits?: NavigationVisits;
}

export interface IAiTrackerModernApplicationCustomizerProperties {
  appInsightsConnectionStringHash: string;
  insightsWebRootUrlHash?: string;
  cacheToken: string;
}
