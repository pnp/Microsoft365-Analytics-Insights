import { override } from '@microsoft/decorators';
import { ApplicationCustomizerContext, BaseApplicationCustomizer } from '@microsoft/sp-application-base';
import { Guid, SPEventArgs } from '@microsoft/sp-core-library';
import { IAiTrackerModernApplicationCustomizerProperties, SitesTrackedByExtension, spPageContextInfo } from './definitions';

// AITracker.js function. That's where we drive the AppInsights telemetry.
declare function modernPageNav(webUrl: string, webTitle: string, siteUrl: string, listTitle?: string, listItemId?: number): void;

const AITRACKER_MODERN_VERSION: string = "1.0.1.54";

declare global {
  interface Window {
    _spPageContextInfo: spPageContextInfo,
    _o365AnalyticsInfo: SitesTrackedByExtension
  }
}

export default class AiTrackerModernApplicationCustomizer
  extends BaseApplicationCustomizer<IAiTrackerModernApplicationCustomizerProperties> {

  // Remeber URL to avoid tracking initial page, as AITracker will do that automatically
  private lastSite: string;
  private runtimeId: Guid = Guid.newGuid(); // A way to indentify what instance is running
  private lastTrackedUrlFromSpfx: string = "";

  // Debug URLs: use "gulp serve" with serve.json properties
  @override
  public async onInit(): Promise<void> {

    console.debug("SPOInsights ModernUI [" + this.runtimeId + "]: SPFx solution init.");

    // Check for _spoInsightsLoaded global variable to avoid double-load...
    const existingSitesLoaded = this.GetSitesConfigFromWindow();
    if (existingSitesLoaded.siteUrls.indexOf(this.context.pageContext.site.absoluteUrl) === -1) {
      existingSitesLoaded.siteUrls.push(this.context.pageContext.site.absoluteUrl);
      console.debug(`SPOInsights ModernUI debug [${this.runtimeId}]: Registered loaded for site ${this.context.pageContext.site.absoluteUrl}`);
    }
    else {
      console.debug(`SPOInsights ModernUI debug [${this.runtimeId}]: Already loaded SPFx extension for site ${this.context.pageContext.site.absoluteUrl} with another instance. Extension installed twice?`);

      // OnInit seems to fire twice, or maybe the extension is installed more than once. Make sure we continue only once.
      return Promise.resolve<void>(undefined);
    }

    console.info(`SPOInsights ModernUI [${this.runtimeId}]: version ${AITRACKER_MODERN_VERSION} tracking page.`);

    // Add _spPageContextInfo global variable if needed
    const w = (window as Window);
    if (!w._spPageContextInfo) {
      this.updateLegacyPageContext();
    }

    // Grab AppInsights key from SPFx extension properties & insert + AITracker into header
    if (this.properties.appInsightsConnectionStringHash) {
      console.log("SPOInsights ModernUI [" + this.runtimeId + "]: Injecting AITracker with connection-string " + atob(this.properties.appInsightsConnectionStringHash));
      let aiTrackeURL: string = this.context.pageContext.site.absoluteUrl + "/SPOInsights/AITracker.js";

      // Append refresh token to AITracker.js url?
      if (this.properties.cacheToken) {
        aiTrackeURL += "?ver=" + encodeURI(this.properties.cacheToken);
      }

      // Add AppInsights key to doc header
      const aiTrackerKeyScriptTag: HTMLScriptElement = document.createElement("script");
      aiTrackerKeyScriptTag.text = "var appInsightsConnectionStringHash = '" + this.properties.appInsightsConnectionStringHash + "';";
      aiTrackerKeyScriptTag.type = "text/javascript";
      document.head.appendChild(aiTrackerKeyScriptTag);

      // Add root web key to doc header, if there is one
      if (this.properties.insightsWebRootUrlHash) {
        
        console.debug("SPOInsights ModernUI debug [" + this.runtimeId + "]: We have a insightsWebRootUrlHash");
        const insightsWebRootUrlScriptTag: HTMLScriptElement = document.createElement("script");
        insightsWebRootUrlScriptTag.text = "var insightsWebRootUrlHash = '" + this.properties.insightsWebRootUrlHash + "';";
        insightsWebRootUrlScriptTag.type = "text/javascript";
        document.head.appendChild(insightsWebRootUrlScriptTag);
      }
      else {
        console.debug("SPOInsights ModernUI debug [" + this.runtimeId + "]: No insightsWebRootUrlHash found.");
      }

      // Add AITracker script to doc header
      const aiTrackerScriptTag: HTMLScriptElement = document.createElement("script");
      aiTrackerScriptTag.src = aiTrackeURL;
      aiTrackerScriptTag.type = "text/javascript";
      document.head.appendChild(aiTrackerScriptTag);

      // Wire-up page-changed SPFx event
      this.context.application.navigatedEvent.add(this, this.logNavigatedEvent);
    }
    else
      console.error("SPOInsights ModernUI [" + this.runtimeId + "]: FATAL: No key 'appInsightsConnectionStringHash' found with extension properties.");

    // Remember site for dispose event
    this.lastSite = this.context.pageContext.site.absoluteUrl;
    return Promise.resolve<void>(undefined);
  }

  private logNavigatedEvent(args: SPEventArgs): void {

    // Make sure we only call the once to AITracker. 
    if (this.lastTrackedUrlFromSpfx !== window.location.href) {

      this.lastTrackedUrlFromSpfx = window.location.href;
      this.updateLegacyPageContext();

      // Ignore initial navigation event as AITracker.js will pick that up
      const existingSitesLoaded: SitesTrackedByExtension = this.GetSitesConfigFromWindow();
      if (existingSitesLoaded.lastUrlTracked !== window.location.href) {

        console.debug("SPOInsights ModernUI debug [" + this.runtimeId + "]: Will invoke 'modernPageNav' on AITracker.js...");
        // Wait for the DOM to sort itself out, otherwise things like document.title won't have the new value
        setTimeout((context: ApplicationCustomizerContext) => {

          // Invoke AITracker.js function to upload new navigation
          modernPageNav(context.pageContext.web.absoluteUrl, context.pageContext.web.title,
            context.pageContext.site.absoluteUrl, context.pageContext.list?.title, context.pageContext.listItem?.id);
        }, 2000, this.context);

      }
    }
    else {
      console.debug("SPOInsights ModernUI debug [" + this.runtimeId + "]: Duplicate navigatedEvent detected? Ignoring.");
    }
  }

  // Get window var for tracking concurrent extension loading (shouldn't happen but can)
  private GetSitesConfigFromWindow(): SitesTrackedByExtension {
    const w = (window as Window);
    if (w._o365AnalyticsInfo) {
      return w._o365AnalyticsInfo;
    }
    else {
      const newWindowVar: SitesTrackedByExtension = { siteUrls: [], lastUrlTracked: undefined };
      w._o365AnalyticsInfo = newWindowVar;
      console.debug("SPOInsights ModernUI debug [" + this.runtimeId + "]: Setting new '_o365AnalyticsInfo' variable.");

      return newWindowVar;
    }
  }

  private updateLegacyPageContext(): void {
    const w = (window as Window);
    w._spPageContextInfo = this.context.pageContext.legacyPageContext;
    console.debug("SPOInsights ModernUI debug [" + this.runtimeId + "]: Setting new '_spPageContextInfo' variable.");
    console.debug(this.context.pageContext.legacyPageContext);
  }

  // Clean-up
  protected onDispose(): void {

    if (this.lastSite) {
      console.debug("SPOInsights ModernUI [" + this.runtimeId + "]: Disposing for " + this.lastSite + "");
    }
    else {
      console.debug("SPOInsights ModernUI [" + this.runtimeId + "]: Disposing duplicate extension");
      return;
    }

    this.context.application.navigatedEvent.remove(this, this.logNavigatedEvent);

    const existingSitesLoaded: SitesTrackedByExtension = this.GetSitesConfigFromWindow();
    const siteIndex = existingSitesLoaded.siteUrls.indexOf(this.lastSite);
    if (siteIndex > -1) {
      existingSitesLoaded.siteUrls.splice(siteIndex);
      return;
    }
  }
}
