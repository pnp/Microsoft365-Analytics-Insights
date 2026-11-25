import { AppInsightsWrapper } from "./AppInsightsWrapper";
import { GetLastPageStatsVal, SetLastPageStatsVal } from "./Cookies";
import { getSPRequestDurationFromScripts, isSamePage } from "./DataFunctions";
import { debug, error, log, warn } from "./Logger";
import TimeMe from 'timeme.js'
import { PagePropertyManager } from "./PageProps/PagePropertyManager";
import { spPageContextInfo } from "./Definitions";
import { isOnTrackedSite, isUrlInSite, registeredSiteFor, type ModernPageContext } from "./TrackedSites";

type TimedVisit = { url: string, activeMs: number, atStartup?: boolean };

export class PageViewTracker {

    _ai: AppInsightsWrapper;
    _context: spPageContextInfo;
    _pagePropLoader: PagePropertyManager;

    // Usually these are the same visit. While SPFx is waiting to report a destination, only the
    // visible visit gains time; only the accepted visit has a request ID and can be reported.
    private visibleVisit: TimedVisit | null;
    private acceptedVisit: TimedVisit | null = null;
    private lastActiveMs = 0;
    private suspended = false;

    constructor(ai: AppInsightsWrapper, context: spPageContextInfo, pagePropLoader: PagePropertyManager) {
        this._ai = ai;
        this._context = context;
        this._pagePropLoader = pagePropLoader;
        const url = document.URL;
        this.visibleVisit = isOnTrackedSite(url, window._o365AnalyticsInfo) ? { url, activeMs: 0, atStartup: true } : null;
    }

    updatePageContext(context: spPageContextInfo) {
        this._context = context;
    }

    // Track the page the browser loaded, then the time spent on the page before it (saved to a cookie as the browser left it).
    // A pageLoadDuration of 0 means SharePoint navigated here without the browser loading a page.
    trackCurrentPageViewAndLastPageExit(url: string, listTitle?: string, listItemId?: number, pageLoadDuration?: number): void {

        if (!this.observeCurrentPage()) {
            debug("Not tracking this page: it isn't on a tracked site");
        } else if (pageLoadDuration === 0 || (window._o365AnalyticsInfo && !this.visibleVisit?.atStartup)) {
            this.trackFirstSoftView(url);
        } else {
            const context = window._spPageContextInfo;
            const bound = this.boundPageContext(url);
            if (bound) {
                this.trackCurrentPageView(url, pageLoadDuration, bound);
            } else if (!context) {
                error(`Didn't find legacy _spPageContextInfo on page.`);
            } else if (!window._o365AnalyticsInfo || this.contextIsOnPage(url, context.siteAbsoluteUrl, context.webAbsoluteUrl)) {
                this.trackCurrentPageView(url, pageLoadDuration, { webUrl: context.webAbsoluteUrl, siteUrl: context.siteAbsoluteUrl,
                    webTitle: context.webTitle, listTitle, listItemId });
            } else {
                // The browser loaded this registered page; its title and timings are still its own.
                this.trackCurrentPageView(url, pageLoadDuration, { siteUrl: registeredSiteFor(url, window._o365AnalyticsInfo) });
            }
        }

        this.trackLastPageExit();
    }

    private trackLastPageExit(replacingFor?: string): void {
        // On load/restore consume the cookie. On hide its replacement is the write that clears it.
        const saved = GetLastPageStatsVal(!replacingFor);
        if (!saved || (replacingFor && saved.pageRequestId === replacingFor)) return;
        if (!saved.secondsOnPage || !saved.pageRequestId || !saved.url) {
            warn("Last page stats cookie found but missing required properties (secondsOnPage, pageRequestId, or url)");
            return;
        }
        let url: string;
        try { url = decodeURI(saved.url); }
        catch { warn("Invalid URL in last page stats cookie"); return; }
        this._ai.trackTimingEvent(url, saved.secondsOnPage, saved.pageRequestId);
    }

    setPageUpdateIntervalMinutes(interval: number) {
        this._pagePropLoader.setPageUpdateIntervalMinutes(interval);
    }

    private contextIsOnPage(url: string, siteUrl: string, webUrl: string): boolean {
        return !!siteUrl && isOnTrackedSite(url, window._o365AnalyticsInfo, siteUrl) &&
            isUrlInSite(webUrl, siteUrl) && isUrlInSite(url, webUrl);
    }

    private boundPageContext(url: string): ModernPageContext | undefined {
        const page = window._o365AnalyticsInfo?.visits?.currentPageContext?.();
        return page && isSamePage(page.url, url) && this.contextIsOnPage(url, page.siteUrl, page.webUrl) ? page : undefined;
    }

    private trackFirstSoftView(url: string, reportedSite?: string): boolean {
        // The DOM and the legacy global can still belong to the loaded document, even in the same web.
        // SPFx 1.0.1.67 can supply its own visit-bound snapshot, never a document title.
        const sites = window._o365AnalyticsInfo;
        const page = this.boundPageContext(url);
        if (page) {
            this.trackCurrentPageView(url, 0, page, null);
        } else if (!sites || (reportedSite && !sites.visits?.currentPageContext)) {
            // Older callbacks name a collection but not the page their web/list details came from.
            this.trackCurrentPageView(url, 0, { siteUrl: sites && registeredSiteFor(url, sites) }, null);
        } else {
            return false; // Do not consume the destination's later, correctly bound report.
        }
        return true;
    }

    handleModernPageNav(webUrl: string, webTitle: string, siteUrl: string, url: string, listTitle?: string, listItemId?: number): boolean {
        // SharePoint may already be on an unregistered site, or on another instance's site.
        if (!this.observeCurrentPage()) {
            debug("Not tracking a navigation outside an active tracked visit");
            return false;
        }
        if (!this.contextIsOnPage(url, siteUrl, webUrl)) {
            debug("Not tracking a navigation reported for another site: SharePoint has moved on since");
            return false;
        }
        this.updatePageContext(window._spPageContextInfo);

        // The SPFx extension also reports the page that was tracked when it loaded. That isn't a navigation: don't end
        // the page's time-on-page after a couple of seconds and restart it, which would undercount it.
        const lastUrl = this._ai._lastTrackedUrl;
        if (lastUrl && isSamePage(lastUrl, url)) {
            debug(`Ignoring navigation to the page already tracked: ${url}`);
            return true;
        }
        if (!lastUrl && window._o365AnalyticsInfo) return this.trackFirstSoftView(url, siteUrl);
        log('Modern page navigation called from SPFx component. New URL: ' + url);

        if (lastUrl && this.acceptedVisit) {
            this._ai.trackTimingEvent(lastUrl, this.acceptedVisit.activeMs / 1000);
        }

        // Track page with "load-time" of 0 as we didn't actually load a page. 
        // If we don't supply 0, AppInsights will use the last page-load time instead, which would be invalid.
        this.trackCurrentPageView(url, 0, { webUrl, siteUrl, webTitle, listTitle, listItemId });
        return true;
    }

    observeCurrentPage(boundary?: 'navigation' | 'registration'): boolean {
        if (this.suspended) return false;
        const url = document.URL;
        const tracked = isOnTrackedSite(url, window._o365AnalyticsInfo);
        this._ai.discardPendingClicksUnlessOn(tracked ? url : null);

        const visit = this.visibleVisit;
        if (!visit && !tracked) return false;
        const now = TimeMe.getTimeOnCurrentPageInMilliseconds();
        // With an actual URL-change notification we know when the previous page ended. If a
        // poll/click merely discovers an unregistered site, stop at the last confirmed sample.
        if (visit && (tracked || (boundary === 'navigation' && !isSamePage(visit.url, url)) ||
            (boundary === 'registration' && isSamePage(visit.url, url)))) {
            visit.activeMs += now - this.lastActiveMs;
        }
        this.lastActiveMs = now;
        if (!tracked) {
            this.visibleVisit = null;
            log("Not on a tracked site, so nothing is tracked until SharePoint shows a page of one");
        } else if (!visit || !isSamePage(visit.url, url)) {
            this.visibleVisit = this.acceptedVisit && isSamePage(this.acceptedVisit.url, url)
                ? this.acceptedVisit : { url, activeMs: 0 };
        }
        return tracked;
    }

    private trackCurrentPageView(url: string, pageLoadDuration: number | undefined,
        { webUrl, siteUrl, webTitle, listTitle, listItemId }: Partial<ModernPageContext>, pageTitle: string | null = document.title): void {

        debug(`Tracking page view for URL: ${url}, listTitle: ${listTitle || 'none'}, listItemId: ${listItemId ?? 'none'}`);

        // SPRequestDuration is in the HTML SharePoint served for the document, so it only describes a page the browser loaded:
        // after a page navigation it's the duration of the first page's request. Not an officially supported API either.
        const spRequestDuration = pageLoadDuration === 0 ? null : getSPRequestDurationFromScripts(document.body);
        if (!this._ai.trackCurrentPageView(pageLoadDuration, spRequestDuration, webUrl, siteUrl, webTitle, pageTitle)) {
            return;     // Same page as last time
        }
        this.acceptedVisit = this.visibleVisit;

        // If needed, log page metadata. Read from this page's own web: it's not the one the script loaded in after a navigation
        if (webUrl) this._pagePropLoader.handleNewPage(webUrl, listItemId ?? -1, url, listTitle);

        this.trackSearchInUrl(url);
    }

    // Search results pages carry the search in the URL. Modern search uses "q", classic search "k".
    trackSearchInUrl(url: string): void {
        let searchParams: URLSearchParams;
        try {
            searchParams = new URL(url).searchParams;
        } catch {
            return;
        }

        const searchTerm = searchParams.get("k") ?? searchParams.get("q");
        if (searchTerm !== null) {
            this._ai.trackSearch(searchTerm);
        }
    }

    savePageExitToCookie(persisted = false): void {
        if (this.suspended) return;
        this.observeCurrentPage();
        this._ai.discardPendingClicksUnlessOn(null);
        const accepted = this.acceptedVisit;
        if (accepted && accepted.activeMs > 0 && this._ai._pageRequestId) {
            // A back traversal can show this document before the departing renderer writes its
            // cookie. Deliver any other request still in the shared slot before replacing it.
            this.trackLastPageExit(this._ai._pageRequestId);
            const url = this.visibleVisit === accepted ? document.URL : this._ai._lastTrackedUrl!;
            const secondsOnPage = accepted.activeMs / 1000;
            debug(`Saving page exit stats with ${secondsOnPage} seconds spent on ${url} to cookie SPOInsightsLastPageStats.`);
            SetLastPageStatsVal({ pageRequestId: this._ai._pageRequestId, secondsOnPage, url: encodeURI(url) });
        }
        if (persisted) {
            // The document may resume with the same request ID. Keep its cumulative time, not
            // a delta since this cookie. An unaccepted destination and its clicks end here.
            this.visibleVisit = null;
            this.suspended = true;
        }
    }

    resumeFromCache(): void {
        if (!this.suspended) return;
        this.suspended = false;
        this.observeCurrentPage();
        // A real document can have saved its own exit before the browser restored this one.
        this.trackLastPageExit();
    }
}
