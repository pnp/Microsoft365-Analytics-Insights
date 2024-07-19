import { AppInsightsWrapper } from "./AppInsightsWrapper";
import { ClearLastPageStatsVal, GetLastPageStatsVal, GetLastTrackedPageVal, SetLastPageStatsVal } from "./Cookies";
import { getSPRequestDuration } from "./DataFunctions";
import { debug, error, log } from "./Logger";
import TimeMe from 'timeme.js'
import { PagePropertyManager } from "./PageProps/PagePropertyManager";
import { spPageContextInfo } from "./Definitions";

export class PageViewTracker {

    _ai: AppInsightsWrapper;
    _lastTimeTotalOnPages: number = 0;
    _context: spPageContextInfo;
    _pagePropLoader: PagePropertyManager;

    _lastGeneratedPageRequestId: string | null = null;               // Page request GUID to join before & after AI events together on import
    _lastUrl: string | null = null;

    constructor(ai: AppInsightsWrapper, context: spPageContextInfo, pagePropLoader: PagePropertyManager) {
        this._ai = ai;
        this._context = context;
        this._pagePropLoader = pagePropLoader;
    }

    updatePageContext(context: spPageContextInfo) {
        this._context = context;
    }

    // Track the page view & previous saved page stats. Normally run before "load", but not for modern page reloads
    trackCurrentPageViewAndLastPageExit(url: string, listTitle: string, listItemId?: number): void {

        if (typeof window._spPageContextInfo === 'undefined') {
            error(`Didn't find legacy _spPageContextInfo on page.`);
            return;
        }

        // Track new page view
        this.trackCurrentPageView(undefined, window._spPageContextInfo.webAbsoluteUrl,
            window._spPageContextInfo.siteAbsoluteUrl, window._spPageContextInfo.webTitle, url, listTitle, listItemId);


        // Track page event from last hit
        const lastPageStats = GetLastPageStatsVal();

        // Was there a last page to track? Do we have all the right properties?
        if (lastPageStats !== null && lastPageStats.secondsOnPage !== null && lastPageStats.pageRequestId !== null && lastPageStats.url !== null) {
            var pageUrl = decodeURI(lastPageStats.url);
            this._ai.trackTimingEvent(pageUrl, lastPageStats.secondsOnPage);
        }

        // Clear cookie
        ClearLastPageStatsVal();
    }

    setPageUpdateIntervalMinutes(interval: number) {
        this._pagePropLoader.setPageUpdateIntervalMinutes(interval);
    }

    // Save last page stats to cookie, then track page view same as classic page
    handleModernPageNav(webUrl: string, webTitle: string, siteUrl: string, url: string, listTitle?: string, listItemId?: number) {
        log('Modern page navigation called from SPFx component. New URL: ' + url);

        // As HandleModernUIPageNav can be called a lot in a single load, subtract the time of the last "page exits"
        var timeOnPage = this.getTimeOnPageAndResetLastTotalTime();

        // Track "page exit" of previous URL
        const lastUrl = GetLastTrackedPageVal();
        if (lastUrl !== '') {
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

        // If needed, log page metadata
        this._pagePropLoader.handleNewPage(listItemId ?? -1, url, listTitle);

        // Try get performance metrics. Have a feeling it doesn't work properly as not using any officially supported API. 
        var spRequestDuration = getSPRequestDuration(document.body.innerHTML);
        this._ai.trackCurrentPageView(pageLoadDuration, spRequestDuration, webUrl, siteUrl, webTitle);
    }

    // Track the page exit with an event. Run on page "unload" only
    savePageExitToCookie() {

        if (this._ai._pageRequestId) {
            // Get time on page
            var secondsOnPage = this.getTimeOnPageAndResetLastTotalTime();
            if (!secondsOnPage) {
                error("Invalid time on page");
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
