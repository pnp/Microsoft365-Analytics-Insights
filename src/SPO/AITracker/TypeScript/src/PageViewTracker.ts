import { AppInsightsWrapper } from "./AppInsightsWrapper";
import { GetLastPageStatsVal, SetLastPageStatsVal } from "./Cookies";
import { getSPRequestDurationFromScripts, isSamePage } from "./DataFunctions";
import { debug, error, log, warn } from "./Logger";
import TimeMe from 'timeme.js'
import { PagePropertyManager } from "./PageProps/PagePropertyManager";
import { spPageContextInfo } from "./Definitions";
import { isOnTrackedSite, isUrlInSite } from "./TrackedSites";

type TimedVisit = { url: string, activeMs: number };

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
        this.visibleVisit = isOnTrackedSite(url, window._o365AnalyticsInfo) ? { url, activeMs: 0 } : null;
    }

    updatePageContext(context: spPageContextInfo) {
        this._context = context;
    }

    // Track the page the browser loaded, then the time spent on the page before it (saved to a cookie as the browser left it).
    // A pageLoadDuration of 0 means SharePoint navigated here without the browser loading a page.
    trackCurrentPageViewAndLastPageExit(url: string, listTitle: string, listItemId?: number, pageLoadDuration?: number): void {

        if (this.observeCurrentPage()) {
            if (!window._spPageContextInfo) {
                error(`Didn't find legacy _spPageContextInfo on page.`);
                return;
            }
            this.trackCurrentPageView(pageLoadDuration, window._spPageContextInfo.webAbsoluteUrl,
                window._spPageContextInfo.siteAbsoluteUrl, window._spPageContextInfo.webTitle, url, listTitle, listItemId);
        } else {
            debug("Not tracking this page: it isn't on a tracked site");
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

    handleModernPageNav(webUrl: string, webTitle: string, siteUrl: string, url: string, listTitle?: string, listItemId?: number) {
        // SharePoint may already be on an unregistered site, or on another instance's site.
        if (!this.observeCurrentPage()) {
            debug("Not tracking a navigation outside an active tracked visit");
            return;
        }
        if (siteUrl && !isUrlInSite(url, siteUrl)) {
            debug("Not tracking a navigation reported for another site: SharePoint has moved on since");
            return;
        }
        this.updatePageContext(window._spPageContextInfo);

        // The SPFx extension also reports the page that was tracked when it loaded. That isn't a navigation: don't end
        // the page's time-on-page after a couple of seconds and restart it, which would undercount it.
        const lastUrl = this._ai._lastTrackedUrl;
        if (lastUrl && isSamePage(lastUrl, url)) {
            debug(`Ignoring navigation to the page already tracked: ${url}`);
            return;
        }
        log('Modern page navigation called from SPFx component. New URL: ' + url);

        if (lastUrl && this.acceptedVisit) {
            this._ai.trackTimingEvent(lastUrl, this.acceptedVisit.activeMs / 1000);
        }

        // Track page with "load-time" of 0 as we didn't actually load a page. 
        // If we don't supply 0, AppInsights will use the last page-load time instead, which would be invalid.
        this.trackCurrentPageView(0, webUrl, siteUrl, webTitle, url, listTitle, listItemId);
    }

    observeCurrentPage(navigationBoundary = false): boolean {
        if (this.suspended) return false;
        const url = document.URL;
        const tracked = isOnTrackedSite(url, window._o365AnalyticsInfo);
        this._ai.discardPendingClicksUnlessOn(tracked ? url : null);

        const visit = this.visibleVisit;
        if (!visit && !tracked) return false;
        const now = TimeMe.getTimeOnCurrentPageInMilliseconds();
        // With an actual URL-change notification we know when the previous page ended. If a
        // poll/click merely discovers an unregistered site, stop at the last confirmed sample.
        if (visit && (tracked || (navigationBoundary && !isSamePage(visit.url, url)))) {
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

    trackCurrentPageView(pageLoadDuration: number | undefined,
        webUrl: string, siteUrl: string, webTitle: string, url: string, listTitle?: string, listItemId?: number): void {

        debug(`Tracking page view for URL: ${url}, listTitle: ${listTitle || 'none'}, listItemId: ${listItemId ?? 'none'}`);

        // SPRequestDuration is in the HTML SharePoint served for the document, so it only describes a page the browser loaded:
        // after a page navigation it's the duration of the first page's request. Not an officially supported API either.
        const spRequestDuration = pageLoadDuration === 0 ? null : getSPRequestDurationFromScripts(document.body);
        if (!this._ai.trackCurrentPageView(pageLoadDuration, spRequestDuration, webUrl, siteUrl, webTitle)) {
            return;     // Same page as last time
        }
        this.acceptedVisit = this.visibleVisit;

        // If needed, log page metadata. Read from this page's own web: it's not the one the script loaded in after a navigation
        this._pagePropLoader.handleNewPage(webUrl, listItemId ?? -1, url, listTitle);

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
