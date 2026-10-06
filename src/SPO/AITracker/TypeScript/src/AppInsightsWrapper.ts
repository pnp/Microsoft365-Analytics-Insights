import { ApplicationInsights, IEventTelemetry, IPageViewTelemetry } from "@microsoft/applicationinsights-web";
import { debug, debugObj, error, log, warn } from "./Logger";
import { PageProps } from "./PageProps/Models/PageProps";
import { ClickData, ClickEventProps, PageViewDataProperties, SearchEventProperties, TimingEventProperties } from "./Definitions";
import { AI_TRACKER_VER, EVENT_CLICK, EVENT_METADATA_UPDATE, EVENT_PAGE_EXIT } from "./AiTrackerConstants";
import { uuidv4 } from "./DataFunctions";

// Clicks made before the page's first page view is tracked are held for it, up to this many
const MAX_CLICKS_BEFORE_FIRST_PAGE_VIEW = 20;

export class AppInsightsWrapper {

    _ai: ApplicationInsights;
    _sessionId: string;
    _pageRequestId: string | null = null;               // Page request GUID to join before & after AI events together on import
    _lastTrackedUrl: string | null = null;
    _clicksBeforeFirstPageView: { data: ClickData, timeStamp: string }[] = [];

    constructor(instance: ApplicationInsights, sessionId: string) {
        this._ai = instance;
        this._sessionId = sessionId;
    }

    // Page views. Returns false if the URL was the page already tracked
    trackCurrentPageView(pageLoadDuration: number | undefined, spRequestDuration: number | null, webUrl: string, siteUrl: string, webTitle: string): boolean {

        if (this._lastTrackedUrl === document.URL) {
            debug("Ignoring duplicate pageview with request Id: " + this._pageRequestId);
            return false;
        }

        this._lastTrackedUrl = document.URL;

        // New page req
        this._pageRequestId = uuidv4();
        debug("New page request Id: " + this._pageRequestId);

        // Metadata
        var appInsightsPageViewData: PageViewDataProperties =
        {
            pageRequestId: this._pageRequestId,
            webUrl: webUrl,
            siteUrl: siteUrl,
            webTitle: webTitle,
            aiTrackerVersion: AI_TRACKER_VER,
            sessionId: this._sessionId,
            pageTitle: document.title,
            timeStamp: new Date().toISOString()
        };

        // Do we have SPRequestDuration in the page source?
        if (spRequestDuration)
            appInsightsPageViewData.spRequestDuration = spRequestDuration;

        const pv: IPageViewTelemetry =
        {
            uri: document.URL,
            name: document.title,
            properties: appInsightsPageViewData
        };
        if (pageLoadDuration !== undefined) {
            appInsightsPageViewData['pageLoad'] = pageLoadDuration;

            this._ai.trackPageView(pv);
            log('Uploaded page-view data with pageLoad override ' + pageLoadDuration + ' for pageRequestId: ' + this._pageRequestId + ', url: ' + document.URL + '. Page title: ' + document.title);
            debugObj('Page view telemetry:', pv);
        }
        else {

            // https://stackoverflow.com/questions/14341156/calculating-page-load-time-in-javascript
            // Use Navigation Timing Level 2 API if available, fall back to deprecated timing API
            let pageLoadTime = 0;
            if (window.performance.getEntriesByType) {
                const navEntries = window.performance.getEntriesByType('navigation') as PerformanceNavigationTiming[];
                if (navEntries.length > 0 && navEntries[0].loadEventEnd > 0) {
                    pageLoadTime = navEntries[0].loadEventEnd - navEntries[0].startTime;
                }
            }
            if (pageLoadTime <= 0 && window.performance.timing) {
                const perfData = window.performance.timing;
                pageLoadTime = perfData.loadEventEnd - perfData.navigationStart;
            }

            if (pageLoadTime > 0) {
                debug('Page load time is ' + pageLoadTime + ' milliseconds.');
            } else {
                debug('Page load time not yet available.');
            }

            // Set page-load with metadata as AppInsights doesn't report on this exactly any more
            appInsightsPageViewData['pageLoad'] = pageLoadTime > 0 ? pageLoadTime : 0;

            this._ai.trackPageView(pv);
            log('Uploaded page-view data for pageRequestId: ' + this._pageRequestId + ', url: ' + document.URL + '. Page title: ' + document.title);
            debugObj('Page view telemetry:', pv);
        }

        // Clicks made before this page view was tracked belong to this page
        this._clicksBeforeFirstPageView.splice(0).forEach(c => this.sendClick(c.data, this._pageRequestId!, c.timeStamp));

        return true;
    }

    // Track Time on Page. pageRequestId is the page the time was spent on; the current page if not given
    trackTimingEvent(pageUrl: string, secondsOnPage: number, pageRequestId?: string) {

        const timedPageRequestId = pageRequestId ?? this._pageRequestId;
        if (timedPageRequestId) {

            const customProps: TimingEventProperties =
            {
                pageRequestId: timedPageRequestId,
                url: pageUrl,
                activeTime: secondsOnPage,
                aiTrackerVersion: AI_TRACKER_VER,
                sessionId: this._sessionId,
                timeStamp: new Date().toISOString()
            };

            // Track event, not page-view
            const e: IEventTelemetry =
            {
                name: EVENT_PAGE_EXIT,
                properties: customProps
            };
            log(`Uploaded page-stats for previous URL ${pageUrl} and pageRequestId ${timedPageRequestId}: seconds on page: ${secondsOnPage}`);

            this._ai.trackEvent(e);
            debugObj('Timing event telemetry:', e);
        }
        else {
            error(`Can't track ${EVENT_PAGE_EXIT}: no page request ID`);
        }
    }

    // Search event receiver
    trackSearch(searchTerm: string) {
        if (searchTerm !== '' && searchTerm !== null) {
            log("Searching for '" + searchTerm + "'");

            const searchProps : SearchEventProperties =
            {
                pageRequestId: this._pageRequestId ?? "",       // We don't depend on searches having a page reference
                sessionId: this._sessionId,
                timeStamp: new Date().toISOString(),
                userSearch: searchTerm
            };

            const e: IEventTelemetry =
            {
                name: "UserSearch", properties: searchProps
            };
            this._ai.trackEvent(e);
            debugObj('Search event telemetry:', e);
        }
        else {
            debug("Ignoring blank search term.");
        }
    }

    // Click event receiver
    trackClick(d: ClickData) {

        const timeStamp = new Date().toISOString();
        if (this._pageRequestId) {
            this.sendClick(d, this._pageRequestId, timeStamp);
        }
        else if (this._clicksBeforeFirstPageView.length < MAX_CLICKS_BEFORE_FIRST_PAGE_VIEW) {
            // The page view is tracked once the page has loaded. Hold the click for it rather than drop it
            debug(`Holding ${EVENT_CLICK} on "${d.linkText}" until this page's page view is tracked`);
            this._clicksBeforeFirstPageView.push({ data: d, timeStamp: timeStamp });
        }
        else {
            warn(`Can't track ${EVENT_CLICK}: no page request ID yet, and ${MAX_CLICKS_BEFORE_FIRST_PAGE_VIEW} clicks are already waiting for one`);
        }
    }

    sendClick(d: ClickData, pageRequestId: string, timeStamp: string) {

        log(`Link click detected: pageRequestId: ${pageRequestId}, title "${d.linkText}"; alt "${d.altText}"; classes "${d.classNames}"`);
        const props: ClickEventProps = { sessionId: this._sessionId, pageRequestId: pageRequestId, timeStamp: timeStamp };
        const e: IEventTelemetry =
        {
            name: EVENT_CLICK, properties: props
        };

        if (d.linkText && d.linkText !== "") {
            props.linkText = d.linkText;
        }
        if (d.altText && d.altText !== "") {
            props.altText = d.altText;         // Currently not actually stored in SQL
        }
        if (d.href && d.href !== "") {
            props.href = d.href;
        }
        if (d.classNames && d.classNames !== "") {
            props.classNames = d.classNames;
        }

        this._ai.trackEvent(e);
        debugObj('Click event telemetry:', e);
    }

    updatePageProps(props: PageProps): void {
        const e: IEventTelemetry =
        {
            name: EVENT_METADATA_UPDATE, properties: props
        };

        this._ai.trackEvent(e);
        debugObj('Page metadata event telemetry:', e);
        log("Posted page metadata to Application Insights");
    }
}
