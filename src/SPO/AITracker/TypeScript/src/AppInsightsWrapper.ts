import { ApplicationInsights, IEventTelemetry, IPageViewTelemetry } from "@microsoft/applicationinsights-web";
import { SetLastTrackedPageVal } from "./Cookies";
import { debug, error, log } from "./Logger";
import { PageProps } from "./PageProps/Models/PageProps";
import { ClickData, ClickEventProps, PageViewDataProperties, SearchEventProperties, TimingEventProperties } from "./Definitions";
import { AI_TRACKER_VER, EVENT_CLICK, EVENT_METADATA_UPDATE, EVENT_PAGE_EXIT } from "./AiTrackerConstants";
import { uuidv4 } from "./DataFunctions";

export class AppInsightsWrapper {

    _ai: ApplicationInsights;
    _sessionId: string;
    _lastGeneratedPageRequestId: string;               // Page request GUID to join before & after AI events together on import
    _pageRequestId: string | null;
    _lastTrackedUrl: string | null;

    constructor(instance: ApplicationInsights, sessionId: string) {
        this._ai = instance;
        this._sessionId = sessionId;
    }

    // Page views
    trackCurrentPageView(pageLoadDuration: number | undefined, spRequestDuration: number | null, webUrl: string, siteUrl: string, webTitle: string) {

        if (this._lastTrackedUrl === document.URL) {
            console.debug("SPOInsights AI Tracker: ignoring duplicate pageview with request Id: " + this._pageRequestId);
            return;
        }

        this._lastTrackedUrl = document.URL;

        // New page req
        this._pageRequestId = uuidv4();
        console.debug("SPOInsights AI Tracker: New page request Id: " + this._pageRequestId);

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
            debug('Uploaded page-view data with pageLoad override ' + pageLoadDuration + ' for pageRequestId: ' + this._pageRequestId + ', url: ' + document.URL + '. Page title: ' + document.title);
        }
        else {

            // https://stackoverflow.com/questions/14341156/calculating-page-load-time-in-javascript
            // https://developer.mozilla.org/en-US/docs/Web/API/PerformanceTiming
            const perfData = window.performance.timing;
            const pageLoadTime = perfData.loadEventEnd - perfData.navigationStart;

            debug('Page load time is ' + pageLoadTime + ' milliseconds.');

            // Set page-load with metadata as AppInsights doesn't report on this exactly any more
            appInsightsPageViewData['pageLoad'] = pageLoadTime;

            this._ai.trackPageView(pv);
            log('Uploaded page-view data for pageRequestId: ' + this._pageRequestId + ', url: ' + document.URL + '. Page title: ' + document.title);
        }
        console.debug(pv);

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
            console.debug(e);
        }
        else {
            error(`Can't track ${EVENT_PAGE_EXIT}: no page request ID`);
        }
    }

    // Search event receiver
    trackSearch(searchTerm: string) {
        if (searchTerm !== '' && !searchTerm !== null) {
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
            console.debug(e);
            this._ai.trackEvent(e);
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

            console.debug(e);
            this._ai.trackEvent(e);
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
        console.debug(e);
        log("Posted page metadata to Application Insights");
    }
}
