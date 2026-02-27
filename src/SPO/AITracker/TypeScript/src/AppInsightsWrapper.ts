import { ApplicationInsights, IEventTelemetry, IPageViewTelemetry } from "@microsoft/applicationinsights-web";
import { SetLastTrackedPageVal } from "./Cookies";
import { debug, debugObj, error, log } from "./Logger";
import { PageProps } from "./PageProps/Models/PageProps";
import { ClickData, ClickEventProps, PageViewDataProperties, SearchEventProperties, TimingEventProperties } from "./Definitions";
import { AI_TRACKER_VER, EVENT_CLICK, EVENT_METADATA_UPDATE, EVENT_PAGE_EXIT } from "./AiTrackerConstants";
import { uuidv4 } from "./DataFunctions";

export class AppInsightsWrapper {

    _ai: ApplicationInsights;
    _sessionId: string;
    _lastGeneratedPageRequestId: string = '';            // Page request GUID to join before & after AI events together on import
    _pageRequestId: string | null = null;
    _lastTrackedUrl: string | null = null;

    constructor(instance: ApplicationInsights, sessionId: string) {
        this._ai = instance;
        this._sessionId = sessionId;
    }

    // Page views
    trackCurrentPageView(pageLoadDuration: number | undefined, spRequestDuration: number | null, webUrl: string, siteUrl: string, webTitle: string) {

        if (this._lastTrackedUrl === document.URL) {
            debug("Ignoring duplicate pageview with request Id: " + this._pageRequestId);
            return;
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

        // Remember last tracked page. 
        SetLastTrackedPageVal(document.URL);

    }

    // Track Time on Page
    trackTimingEvent(pageUrl: string, secondsOnPage: number) {

        if (this._pageRequestId) {

            const customProps: TimingEventProperties =
            {
                pageRequestId: this._pageRequestId,
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
            log(`Uploaded page-stats for previous URL ${pageUrl} and pageRequestId ${this._pageRequestId}: seconds on page: ${secondsOnPage}`);

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

        if (this._pageRequestId) {
            log(`Link click detected: pageRequestId: ${this._pageRequestId}, title "${d.linkText}"; alt "${d.altText}"; classes "${d.classNames}"`);
            const props: ClickEventProps = { sessionId: this._sessionId, pageRequestId: this._pageRequestId, timeStamp: new Date().toISOString() };
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
        else {
            error(`Can't track ${EVENT_CLICK}: no page request ID`);
        }
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
