import type { NavigationVisits } from './NavigationVisits';

export interface SpPageContextInfo {
  userLoginName: string;
  webAbsoluteUrl: string;
  siteAbsoluteUrl: string;
  webTitle: string;
}

export interface SitesTrackedByExtension {
  siteUrls: string[];
  siteRegistrationChanged?: (site: string) => void;
  visits?: NavigationVisits;
}

export interface IAiTrackerModernApplicationCustomizerProperties {
  appInsightsConnectionStringHash: string;
  insightsWebRootUrlHash?: string;
  cacheToken: string;
}
