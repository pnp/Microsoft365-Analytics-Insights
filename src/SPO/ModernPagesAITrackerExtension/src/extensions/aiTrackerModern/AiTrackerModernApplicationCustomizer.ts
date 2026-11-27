import { BaseApplicationCustomizer } from '@microsoft/sp-application-base';
import { Guid, SPEventArgs } from '@microsoft/sp-core-library';
import { SPComponentLoader } from '@microsoft/sp-loader';
import { IAiTrackerModernApplicationCustomizerProperties, SitesTrackedByExtension, SpPageContextInfo } from './definitions';
import { Logger } from './Logger';
import { contextForVisit, INavigationVisit, NavigationVisits, sameSite } from './NavigationVisits';
import { loadTrackerUnlessRunning, trackerAlreadyRunning } from './TrackerPresence';

// AITracker.js function. That's where we drive the AppInsights telemetry.
declare function modernPageNav(webUrl: string, webTitle: string, siteUrl: string, listTitle?: string, listItemId?: number): boolean | void;

const AITRACKER_MODERN_VERSION: string = "1.0.1.69";     // Keep in step with the version in config/package-solution.json
const NAV_EVENT_DELAY_MS: number = 2000;

declare global {
  interface Window {
    _spPageContextInfo: SpPageContextInfo;
    _o365AnalyticsInfo: SitesTrackedByExtension;
  }
}

export default class AiTrackerModernApplicationCustomizer
  extends BaseApplicationCustomizer<IAiTrackerModernApplicationCustomizerProperties> {

  private subscribed: boolean = false;
  private readonly runtimeId: Guid = Guid.newGuid();
  private visits?: NavigationVisits;
  private contextVisit?: INavigationVisit;
  private pending?: { visit: INavigationVisit; timer: number };
  private aiTrackerLoaded: boolean = false;
  private registeredSite: string | undefined = undefined;    // This instance's entry in _o365AnalyticsInfo.siteUrls
  private disposed: boolean = false;

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

    // This instance's site. Read it now: SharePoint updates the page context when it navigates, which it can do while this
    // instance waits for AITracker below
    const site: string = this.context.pageContext.site.absoluteUrl;

    // Check for _spoInsightsLoaded global variable to avoid double-load...
    const existingSitesLoaded = this.getSitesConfigFromWindow();
    if (!existingSitesLoaded.siteUrls.some(registered => sameSite(registered, site))) {
      existingSitesLoaded.siteUrls.push(site);
      this.registeredSite = site;
      existingSitesLoaded.siteRegistrationChanged?.(site);
      Logger.verbose(`[${this.runtimeId}]: Registered loaded for site ${site}`);
    }
    else {
      Logger.warn(`[${this.runtimeId}]: Already loaded SPFx extension for site ${site} with another instance. Extension installed twice?`);

      // OnInit seems to fire twice, or maybe the extension is installed more than once. Make sure we continue only once.
      return;
    }

    Logger.info(`[${this.runtimeId}]: version ${AITRACKER_MODERN_VERSION} tracking page.`);
    this.visits = existingSitesLoaded.visits ??= new NavigationVisits(window, existingSitesLoaded.siteUrls);
    const initialVisit = this.visits.forEvent(site, site);
    if (initialVisit) this.capturePageContext(initialVisit, false);

    // A tracker can start before the destination snapshot is ready. It still needs the signed-in identity.
    if (!window._spPageContextInfo) window._spPageContextInfo = { userLoginName: this.context.pageContext.legacyPageContext?.userLoginName };
    // Capture a destination's navigatedEvent even if it precedes the script download or document load.
    this.context.application.navigatedEvent.add(this, this.logNavigatedEvent);
    this.subscribed = true;

    // Insert AITracker into the page, giving it the AppInsights key from the extension properties
    Logger.info(`[${this.runtimeId}]: Injecting AITracker with connection-string (hash present).`);
    let aiTrackerUrl: string = site + "/SPOInsights/AITracker.js";

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
  }

  private capturePageContext(visit: INavigationVisit, freshEvent: boolean): void {
    const page = this.context.pageContext;
    const snapshot = visit.pageContext = contextForVisit(visit, page, freshEvent);
    this.contextVisit = visit;
    if (snapshot?.binding === 'request') {
      // Older trackers also read these fields from the global. Do not restore the raw list from a previous visit.
      window._spPageContextInfo = { ...page.legacyPageContext, siteAbsoluteUrl: snapshot.siteUrl, webAbsoluteUrl: snapshot.webUrl,
        webTitle: snapshot.webTitle, listTitle: snapshot.listTitle, pageItemId: snapshot.listItemId };
    }
  }

  private logNavigatedEvent(_args: SPEventArgs): void {
    if (this.disposed || !this.registeredSite || !this.visits) return;
    const page = this.context.pageContext;
    const visit = this.visits.forEvent(this.registeredSite, page.site.absoluteUrl);
    if (!visit) return;
    if (visit.reported) return;
    this.capturePageContext(visit, this.subscribed);
    if (!visit.pageContext) {
      this.cancelPendingReport();
      return;
    }
    // Revalidate duplicates too: keep the original deadline, but only deliver the current evidence.
    if (this.pending?.visit === visit) return;
    this.cancelPendingReport();
    Logger.verbose(`[${this.runtimeId}]: Will invoke 'modernPageNav' on AITracker.js...`);
    // Keep the established delivery delay for older copies, without borrowing the later DOM or page context.
    const timer = window.setTimeout(() => {
      if (this.pending?.visit !== visit) return;
      this.pending = undefined;
      if (this.disposed || !this.visits?.isCurrent(visit, this.context.pageContext.site.absoluteUrl) || visit.reported || !visit.pageContext) return;
      if (!this.aiTrackerLoaded) {
        Logger.warn(`[${this.runtimeId}]: AITracker.js didn't load, so this navigation isn't tracked.`);
        return;
      }
      // A hard load belongs to the standalone script. If the initial page has changed, it can
      // instead be waiting for the destination's report.
      if (typeof modernPageNav !== "function") {
        Logger.verbose(`[${this.runtimeId}]: AITracker.js is still waiting for the page to load, and will track it then.`);
        return;
      }
      try {
        const snapshot = visit.pageContext;
        // An older standalone returns void; only an explicit rejection leaves the visit available.
        visit.reported = modernPageNav(snapshot.webUrl ?? visit.site, snapshot.webTitle ?? '', visit.site,
          snapshot.listTitle, snapshot.listItemId) !== false;
        if (!visit.reported && snapshot.binding === 'event' && trackerAlreadyRunning(window) === '1.6.12' &&
            this.visits.isCurrent(visit, this.context.pageContext.site.absoluteUrl) && visit.pageContext === snapshot) {
          // 1.6.12 rejects its first collection-only snapshot while a snapshot reader exists.
          // Its positional-only first-view path records the registered collection and no web/list.
          const registry = window._o365AnalyticsInfo;
          window._o365AnalyticsInfo = { ...registry, visits: undefined };
          try { visit.reported = modernPageNav(visit.site, '', visit.site) !== false; }
          finally { window._o365AnalyticsInfo = registry; }
        }
      } catch (e) {
        Logger.error(`[${this.runtimeId}]: Error calling modernPageNav: ${(e as Error).message}`);
      }
    }, NAV_EVENT_DELAY_MS);
    this.pending = { visit, timer };
  }

  private cancelPendingReport(): void {
    if (this.pending) {
      window.clearTimeout(this.pending.timer);
      this.pending = undefined;
    }
  }

  // Get window var for tracking concurrent extension loading (shouldn't happen but can)
  private getSitesConfigFromWindow(): SitesTrackedByExtension {
    const w = (window as Window);
    if (w._o365AnalyticsInfo) {
      return w._o365AnalyticsInfo;
    }
    else {
      const newWindowVar: SitesTrackedByExtension = { siteUrls: [] };
      w._o365AnalyticsInfo = newWindowVar;
      Logger.verbose(`[${this.runtimeId}]: Setting new '_o365AnalyticsInfo' variable.`);

      return newWindowVar;
    }
  }

  // Clean-up
  protected override onDispose(): void {
    this.disposed = true;
    this.cancelPendingReport();
    this.visits?.observeLocation();

    if (this.subscribed) {
      Logger.info(`[${this.runtimeId}]: Disposing for ${this.registeredSite}.`);
      this.context.application.navigatedEvent.remove(this, this.logNavigatedEvent);
    }
    else {
      Logger.verbose(`[${this.runtimeId}]: Disposing an instance that wasn't following navigations.`);
    }

    // Unregister the site this instance registered, or the next instance for it takes itself for a duplicate and doesn't track
    if (this.registeredSite) {
      if (this.contextVisit) this.contextVisit.pageContext = undefined;
      const existingSitesLoaded: SitesTrackedByExtension = this.getSitesConfigFromWindow();
      const siteIndex = existingSitesLoaded.siteUrls.indexOf(this.registeredSite);
      if (siteIndex > -1) {
        existingSitesLoaded.siteUrls.splice(siteIndex, 1);
        existingSitesLoaded.siteRegistrationChanged?.(this.registeredSite);
      }
    }
  }
}