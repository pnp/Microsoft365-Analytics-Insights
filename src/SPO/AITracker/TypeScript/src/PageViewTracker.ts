import { AppInsightsWrapper } from "./AppInsightsWrapper";
import { ClearLastPageStatsVal, GetLastPageStatsVal, SetLastPageStatsVal } from "./Cookies";
import { getSPRequestDuration } from "./DataFunctions";
import { debug, error, log, warn } from "./Logger";
import TimeMe from 'timeme.js'
import { PagePropertyManager } from "./PageProps/PagePropertyManager";
import { spPageContextInfo } from "./Definitions";

export class PageViewTracker {

    _ai: AppInsightsWrapper;
    _lastTimeTotalOnPages: number = 0;
    _context: spPageContextInfo;
    _pagePropLoader: PagePropertyManager;

    constructor(ai: AppInsightsWrapper, context: spPageContextInfo, pagePropLoader: PagePropertyManager) {
        this._ai = ai;
        this._context = context;
        this._pagePropLoader = pagePropLoader;
    }

    updatePageContext(context: spPageContextInfo) {
        this._context = context;
    }

    // Track the page the browser loaded, then the time spent on the page before it (saved to a cookie as the browser left it).
    // A pageLoadDuration of 0 means SharePoint navigated here without the browser loading a page.
    trackCurrentPageViewAndLastPageExit(url: string, listTitle: string, listItemId?: number, pageLoadDuration?: number): void {

        if (typeof window._spPageContextInfo === 'undefined') {
            error(`Didn't find legacy _spPageContextInfo on page.`);
            return;
        }

        // Track new page view
        this.trackCurrentPageView(pageLoadDuration, window._spPageContextInfo.webAbsoluteUrl,
            window._spPageContextInfo.siteAbsoluteUrl, window._spPageContextInfo.webTitle, url, listTitle, listItemId);


        // Track page event from last hit
        const lastPageStats = GetLastPageStatsVal();

        // Was there a last page to track? Do we have all the right properties?
        if (lastPageStats !== null && lastPageStats.secondsOnPage && lastPageStats.pageRequestId && lastPageStats.url) {
            var pageUrl = decodeURI(lastPageStats.url);

            // The time belongs to the page request it was saved for, not the one just tracked: the importer applies it to that hit
            this._ai.trackTimingEvent(pageUrl, lastPageStats.secondsOnPage, lastPageStats.pageRequestId);
        } else if (lastPageStats !== null) {
            warn("Last page stats cookie found but missing required properties (secondsOnPage, pageRequestId, or url)");
        } else {
            debug("No previous page stats to track");
        }

        // Clear cookie
        ClearLastPageStatsVal();
    }

    setPageUpdateIntervalMinutes(interval: number) {
        this._pagePropLoader.setPageUpdateIntervalMinutes(interval);
    }

    // Save last page stats to cookie, then track page view same as classic page
    handleModernPageNav(webUrl: string, webTitle: string, siteUrl: string, url: string, listTitle?: string, listItemId?: number) {

        // The SPFx extension also reports the page that was tracked when it loaded. That isn't a navigation: don't end
        // the page's time-on-page after a couple of seconds and restart it, which would undercount it.
        const lastUrl = this._ai._lastTrackedUrl;
        if (url === lastUrl) {
            debug(`Ignoring navigation to the page already tracked: ${url}`);
            return;
        }
        log('Modern page navigation called from SPFx component. New URL: ' + url);

        // As HandleModernUIPageNav can be called a lot in a single load, subtract the time of the last "page exits"
        var timeOnPage = this.getTimeOnPageAndResetLastTotalTime();

        // Track "page exit" of previous URL. Before tracking the new page, so it goes to the previous page's request ID.
        // This page's own last URL, rather than a cookie that every open tab writes to.
        if (lastUrl) {
            this._ai.trackTimingEvent(lastUrl, timeOnPage);
        }

        // Track page with "load-time" of 0 as we didn't actually load a page. 
        // If we don't supply 0, AppInsights will use the last page-load time instead, which would be invalid.
        this.trackCurrentPageView(0, webUrl, siteUrl, webTitle, url, listTitle, listItemId);
    }

    getTimeOnPageAndResetLastTotalTime(): number {
        // How long was the user on this page?
        const currentSecondsOnPage = TimeMe.getTimeOnCurrentPageInSeconds();

        // As this method can be called a lot, subtract the time of the last "page exit"
        const timeOnPage = currentSecondsOnPage - this._lastTimeTotalOnPages;
        debug("Time on page for this URL: " + timeOnPage);

        // Remember what we've spent for the next page nav (timer won't really reset until we properly navigate away)
        this._lastTimeTotalOnPages = currentSecondsOnPage;

        return timeOnPage;
    }

    trackCurrentPageView(pageLoadDuration: number | undefined,
        webUrl: string, siteUrl: string, webTitle: string, url: string, listTitle?: string, listItemId?: number): void {

        debug(`Tracking page view for URL: ${url}, listTitle: ${listTitle || 'none'}, listItemId: ${listItemId ?? 'none'}`);

        // SPRequestDuration is in the HTML SharePoint served for the document, so it only describes a page the browser loaded:
        // after a page navigation it's the duration of the first page's request. Not an officially supported API either.
        const spRequestDuration = pageLoadDuration === 0 ? null : getSPRequestDuration(document.body.innerHTML);
        if (!this._ai.trackCurrentPageView(pageLoadDuration, spRequestDuration, webUrl, siteUrl, webTitle)) {
            return;     // Same page as last time
        }

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

    // Track the page exit with an event. Run on page "unload" only
    savePageExitToCookie() {

        if (this._ai._pageRequestId) {
            // Get time on page
            var secondsOnPage = this.getTimeOnPageAndResetLastTotalTime();
            if (!secondsOnPage) {
                warn("Invalid time on page - skipping cookie save");
                return;
            }

            debug('Saving page exit stats with ' + secondsOnPage + ' seconds spent on ' + document.URL + ' to cookie SPOInsightsLastPageStats.');

            // Set cookie for next "page load" to pick-up & send, for 7 days. Can't send stats onpageunload due to time it would take to send.
            SetLastPageStatsVal({
                'pageRequestId': this._ai._pageRequestId, 'secondsOnPage': secondsOnPage, 'url': encodeURI(document.URL)
            });
        }
        else
            error("Can't save page-exit cookie: have no page-request ID");
    }
}
