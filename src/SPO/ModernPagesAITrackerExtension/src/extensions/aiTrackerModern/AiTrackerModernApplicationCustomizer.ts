import { BaseApplicationCustomizer } from '@microsoft/sp-application-base';
import { Guid, SPEventArgs } from '@microsoft/sp-core-library';
import { SPComponentLoader } from '@microsoft/sp-loader';
import { IAiTrackerModernApplicationCustomizerProperties, SitesTrackedByExtension, SpPageContextInfo } from './definitions';
import { Logger } from './Logger';
import { loadTrackerUnlessRunning } from './TrackerPresence';

// AITracker.js function. That's where we drive the AppInsights telemetry.
declare function modernPageNav(webUrl: string, webTitle: string, siteUrl: string, listTitle?: string, listItemId?: number): void;

const AITRACKER_MODERN_VERSION: string = "1.0.1.62";     // Keep in step with the version in config/package-solution.json
const NAV_EVENT_DELAY_MS: number = 2000;

declare global {
  interface Window {
    _spPageContextInfo: SpPageContextInfo;
    _o365AnalyticsInfo: SitesTrackedByExtension;
  }
}

export default class AiTrackerModernApplicationCustomizer
  extends BaseApplicationCustomizer<IAiTrackerModernApplicationCustomizerProperties> {

  // Remember URL to avoid tracking initial page, as AITracker will do that automatically
  private lastSite: string | undefined = undefined;
  private readonly runtimeId: Guid = Guid.newGuid();
  private lastTrackedUrlFromSpfx: string = "";
  private aiTrackerLoaded: boolean = false;

  // Debug URLs: use "gulp serve" with serve.json properties
  public override async onInit(): Promise<void> {

    // The installer enables this extension per site collection, giving it its settings. An instance without them, such as
    // one from the tenant-wide extensions list, has nothing to track - and must not register the site below either, or the
    // configured instance would take it for a duplicate and not track the site.
    if (!this.properties?.appInsightsConnectionStringHash) {
      if (this.properties && Object.keys(this.properties).length > 0) {
        Logger.error(`[${this.runtimeId}]: version ${AITRACKER_MODERN_VERSION}: no 'appInsightsConnectionStringHash' in the extension properties, so not tracking. Re-run the installer for this site.`);
      }
      else {
        Logger.verbose(`[${this.runtimeId}]: version ${AITRACKER_MODERN_VERSION}: no settings, so this instance isn't tracking the site.`);
      }
      return;
    }
    try {
      atob(this.properties.appInsightsConnectionStringHash); // Validate base64 encoding
    } catch {
      Logger.error(`[${this.runtimeId}]: appInsightsConnectionStringHash is not valid base64. Aborting init.`);
      return;
    }

    Logger.info(`[${this.runtimeId}]: SPFx solution init.`);

    // Check for _spoInsightsLoaded global variable to avoid double-load...
    const existingSitesLoaded = this.getSitesConfigFromWindow();
    if (existingSitesLoaded.siteUrls.indexOf(this.context.pageContext.site.absoluteUrl) === -1) {
      existingSitesLoaded.siteUrls.push(this.context.pageContext.site.absoluteUrl);
      Logger.verbose(`[${this.runtimeId}]: Registered loaded for site ${this.context.pageContext.site.absoluteUrl}`);
    }
    else {
      Logger.warn(`[${this.runtimeId}]: Already loaded SPFx extension for site ${this.context.pageContext.site.absoluteUrl} with another instance. Extension installed twice?`);

      // OnInit seems to fire twice, or maybe the extension is installed more than once. Make sure we continue only once.
      return;
    }

    Logger.info(`[${this.runtimeId}]: version ${AITRACKER_MODERN_VERSION} tracking page.`);

    // AITracker.js reads the page's site and list from this. Always this site's: after a navigation from another site it can still describe that one
    this.updateLegacyPageContext();

    // Insert AITracker into the page, giving it the AppInsights key from the extension properties
    Logger.info(`[${this.runtimeId}]: Injecting AITracker with connection-string (hash present).`);
    let aiTrackerUrl: string = this.context.pageContext.site.absoluteUrl + "/SPOInsights/AITracker.js";

    // Append refresh token to AITracker.js url?
    if (this.properties.cacheToken) {
      aiTrackerUrl += `?ver=${encodeURIComponent(this.properties.cacheToken)}`;
    }

    // Set AppInsights key as a window global (avoids CSP inline-script violation)
    (window as unknown as Record<string, unknown>).appInsightsConnectionStringHash = this.properties.appInsightsConnectionStringHash;

    // Set root web key as a window global, if there is one
    if (this.properties.insightsWebRootUrlHash) {
      Logger.verbose(`[${this.runtimeId}]: We have an insightsWebRootUrlHash.`);
      (window as unknown as Record<string, unknown>).insightsWebRootUrlHash = this.properties.insightsWebRootUrlHash;
    }
    else {
      Logger.verbose(`[${this.runtimeId}]: No insightsWebRootUrlHash found.`);
    }

    // Load AITracker script via SPComponentLoader (CSP-safe), unless a copy is already tracking this page. SharePoint navigates
    // between site collections without reloading, and each has its own copy: use the one that's running, or on its way.
    try {
      const runningTracker = await loadTrackerUnlessRunning(window, () => SPComponentLoader.loadScript(aiTrackerUrl, { globalExportsName: 'modernPageNav' }));
      this.aiTrackerLoaded = true;
      if (runningTracker) {
        Logger.verbose(`[${this.runtimeId}]: AITracker ${runningTracker} is already tracking this page, so this site's copy isn't loaded.`);
      }
      else {
        Logger.verbose(`[${this.runtimeId}]: AITracker.js loaded successfully.`);
      }
    } catch (e) {
      Logger.error(`[${this.runtimeId}]: Failed to load AITracker.js from ${aiTrackerUrl}: ${(e as Error).message}`);
    }

    // Wire-up page-changed SPFx event
    this.context.application.navigatedEvent.add(this, this.logNavigatedEvent);

    // Remember site for dispose event
    this.lastSite = this.context.pageContext.site.absoluteUrl;
  }

  private logNavigatedEvent(_args: SPEventArgs): void {

    // Make sure we only call the once to AITracker. 
    if (this.lastTrackedUrlFromSpfx !== window.location.href) {

      this.lastTrackedUrlFromSpfx = window.location.href;
      this.updateLegacyPageContext();

      // AITracker.js tracks the page it loads on itself, and ignores a report of the page it has already tracked
      const existingSitesLoaded: SitesTrackedByExtension = this.getSitesConfigFromWindow();
      if (existingSitesLoaded.lastUrlTracked !== window.location.href) {

        Logger.verbose(`[${this.runtimeId}]: Will invoke 'modernPageNav' on AITracker.js...`);
        // Wait for the DOM to sort itself out, otherwise things like document.title won't have the new value
        setTimeout(() => {
          if (!this.aiTrackerLoaded) {
            Logger.warn(`[${this.runtimeId}]: AITracker.js didn't load, so this navigation isn't tracked.`);
            return;
          }
          // AITracker.js publishes modernPageNav once it has tracked the page it loaded on, after that page's load event, and
          // tracks that page itself. So while the page is still loading there's nothing for this report to do.
          if (typeof modernPageNav !== "function") {
            Logger.verbose(`[${this.runtimeId}]: AITracker.js is still waiting for the page to load, and will track it then.`);
            return;
          }

          try {
            // Invoke AITracker.js function to upload new navigation
            modernPageNav(
              this.context.pageContext.web.absoluteUrl,
              this.context.pageContext.web.title,
              this.context.pageContext.site.absoluteUrl,
              this.context.pageContext.list?.title,
              this.context.pageContext.listItem?.id
            );

            // Update lastUrlTracked so other instances don't re-track this URL
            existingSitesLoaded.lastUrlTracked = window.location.href;
          } catch (e) {
            Logger.error(`[${this.runtimeId}]: Error calling modernPageNav: ${(e as Error).message}`);
          }
        }, NAV_EVENT_DELAY_MS);

      }
    }
    else {
      Logger.verbose(`[${this.runtimeId}]: Duplicate navigatedEvent detected. Ignoring.`);
    }
  }

  // Get window var for tracking concurrent extension loading (shouldn't happen but can)
  private getSitesConfigFromWindow(): SitesTrackedByExtension {
    const w = (window as Window);
    if (w._o365AnalyticsInfo) {
      return w._o365AnalyticsInfo;
    }
    else {
      const newWindowVar: SitesTrackedByExtension = { siteUrls: [], lastUrlTracked: undefined };
      w._o365AnalyticsInfo = newWindowVar;
      Logger.verbose(`[${this.runtimeId}]: Setting new '_o365AnalyticsInfo' variable.`);

      return newWindowVar;
    }
  }

  private updateLegacyPageContext(): void {
    const w = (window as Window);
    w._spPageContextInfo = this.context.pageContext.legacyPageContext;
    Logger.verbose(`[${this.runtimeId}]: Updated '_spPageContextInfo' variable.`);
  }

  // Clean-up
  protected override onDispose(): void {

    if (this.lastSite) {
      Logger.info(`[${this.runtimeId}]: Disposing for ${this.lastSite}.`);
    }
    else {
      Logger.verbose(`[${this.runtimeId}]: Disposing an instance that wasn't tracking.`);
      return;
    }

    this.context.application.navigatedEvent.remove(this, this.logNavigatedEvent);

    const existingSitesLoaded: SitesTrackedByExtension = this.getSitesConfigFromWindow();
    const siteIndex = existingSitesLoaded.siteUrls.indexOf(this.lastSite);
    if (siteIndex > -1) {
      existingSitesLoaded.siteUrls.splice(siteIndex, 1);
    }
  }
}