import { AnalyticsPlugin } from '@microsoft/applicationinsights-analytics-js';
import { Sender } from '@microsoft/applicationinsights-channel-js';
import { AppInsightsCore, DEFAULT_BREEZE_PATH, PageViewDataType, PageViewPerformanceDataType, addPageHideEventListener, addPageUnloadEventListener, parseConnectionString } from '@microsoft/applicationinsights-core-js';
import type { IConfig, IConfiguration } from '@microsoft/applicationinsights-core-js';
import { PropertiesPlugin } from '@microsoft/applicationinsights-properties-js';
import { isOnTrackedSite } from './TrackedSites';

export type TrackerTelemetry = Pick<AnalyticsPlugin, 'trackPageView' | 'trackEvent'>;

export function createTrackerTelemetry(connectionString: string, userLoginName: string): TrackerTelemetry {
    const { ingestionendpoint, instrumentationkey } = parseConnectionString(connectionString);
    if (!instrumentationkey || !ingestionendpoint) throw new Error('Missing Application Insights instrumentation key or ingestion endpoint.');

    const analytics = new AnalyticsPlugin();
    const properties = new PropertiesPlugin();
    const sender = new Sender();
    const config: IConfiguration & IConfig = {
        connectionString,
        instrumentationKey: instrumentationkey,
        endpointUrl: ingestionendpoint + DEFAULT_BREEZE_PATH,
        disableExceptionTracking: true,
        cookieCfg: { enabled: false },
        isBeaconApiDisabled: true
    };
    // No dependency or remote-configuration plugin: never instrument SharePoint's fetch/XHR requests.
    const core = new AppInsightsCore();
    core.initialize(config, [properties, analytics, sender]);
    // Bind the operation while we know which site the browser is on. A late-started tracker may
    // send the previous tracked page's saved exit after SharePoint has left all registered sites.
    const trace = core.getTraceCtx();
    if (trace && !isOnTrackedSite(document.URL, window._o365AnalyticsInfo)) trace.pageName = 'SPOInsights';
    core.addTelemetryInitializer(item => {
        if (item.baseType === PageViewDataType && item.baseData) {
            // Analytics adds document.referrer even though we didn't supply it. The referring
            // document need not be a tracked site; the importer doesn't use it.
            delete item.baseData.refUri;
            const props = item.baseData.properties;
            if (props?.duration === 0) {
                // SDK 3 dates its first view from the browser's navigation entry, even for a soft
                // navigation a day later. The importer filters on envelope time before reading Part C.
                const viewed = Date.parse(props.timeStamp);
                if (!isNaN(viewed)) item.baseData.startTime = new Date(viewed);
            }
        }
        // The SDK also offers the old document's performance breakdown for a no-load navigation.
        if (item.baseType === PageViewPerformanceDataType && item.data?.duration === 0) return false;
    });
    properties.context.user.setAuthenticatedUserContext(userLoginName);

    // The full web SDK wires these itself. Keep short visits and clicks that navigate off the page.
    const flush = () => {
        sender.onunloadFlush();
        properties.context.sessionManager.backup();
    };
    addPageHideEventListener(flush);
    addPageUnloadEventListener(flush);
    return analytics;
}
