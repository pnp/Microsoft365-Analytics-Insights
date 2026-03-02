import { BaseApplicationCustomizer } from '@microsoft/sp-application-base';
import { Guid, SPEventArgs } from '@microsoft/sp-core-library';
import { SPComponentLoader } from '@microsoft/sp-loader';
import { IAiTrackerModernApplicationCustomizerProperties, SitesTrackedByExtension, SpPageContextInfo } from './definitions';
import { Logger } from './Logger';

// AITracker.js function. That's where we drive the AppInsights telemetry.
declare function modernPageNav(webUrl: string, webTitle: string, siteUrl: string, listTitle?: string, listItemId?: number): void;

const AITRACKER_MODERN_VERSION: string = "1.0.1.56";
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

    // Add _spPageContextInfo global variable if needed
    const w = (window as Window);
    if (!w._spPageContextInfo) {
      this.updateLegacyPageContext();
    }

    // Grab AppInsights key from SPFx extension properties & insert + AITracker into header
    if (this.properties.appInsightsConnectionStringHash) {
      try {
        atob(this.properties.appInsightsConnectionStringHash); // Validate base64 encoding
      } catch {
        Logger.error(`[${this.runtimeId}]: appInsightsConnectionStringHash is not valid base64. Aborting init.`);
        return;
      }
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

      // Load AITracker script via SPComponentLoader (CSP-safe)
      try {
        await SPComponentLoader.loadScript(aiTrackerUrl, { globalExportsName: 'modernPageNav' });
        this.aiTrackerLoaded = true;
        Logger.verbose(`[${this.runtimeId}]: AITracker.js loaded successfully.`);
      } catch (e) {
        Logger.error(`[${this.runtimeId}]: Failed to load AITracker.js from ${aiTrackerUrl}: ${(e as Error).message}`);
      }

      // Wire-up page-changed SPFx event
      this.context.application.navigatedEvent.add(this, this.logNavigatedEvent);
    }
    else {
      Logger.error(`[${this.runtimeId}]: FATAL: No key 'appInsightsConnectionStringHash' found with extension properties.`);
    }

    // Remember site for dispose event
    this.lastSite = this.context.pageContext.site.absoluteUrl;
  }

  private logNavigatedEvent(_args: SPEventArgs): void {

    // Make sure we only call the once to AITracker. 
    if (this.lastTrackedUrlFromSpfx !== window.location.href) {

      this.lastTrackedUrlFromSpfx = window.location.href;
      this.updateLegacyPageContext();

      // Ignore initial navigation event as AITracker.js will pick that up
      const existingSitesLoaded: SitesTrackedByExtension = this.getSitesConfigFromWindow();
      if (existingSitesLoaded.lastUrlTracked !== window.location.href) {

        Logger.verbose(`[${this.runtimeId}]: Will invoke 'modernPageNav' on AITracker.js...`);
        // Wait for the DOM to sort itself out, otherwise things like document.title won't have the new value
        setTimeout(() => {
          // Guard: ensure AITracker.js has loaded and modernPageNav is available
          if (!this.aiTrackerLoaded || typeof modernPageNav !== "function") {
            Logger.warn(`[${this.runtimeId}]: modernPageNav not available yet. Navigation event skipped.`);
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
      Logger.verbose(`[${this.runtimeId}]: Disposing duplicate extension.`);
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