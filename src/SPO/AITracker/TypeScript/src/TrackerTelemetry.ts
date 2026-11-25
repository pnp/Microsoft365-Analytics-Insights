import { AnalyticsPlugin } from '@microsoft/applicationinsights-analytics-js';
import { Sender } from '@microsoft/applicationinsights-channel-js';
import { AppInsightsCore, DEFAULT_BREEZE_PATH, PageViewDataType, PageViewPerformanceDataType, addPageHideEventListener, addPageUnloadEventListener, parseConnectionString } from '@microsoft/applicationinsights-core-js';
import type { IConfig, IConfiguration } from '@microsoft/applicationinsights-core-js';
import { PropertiesPlugin } from '@microsoft/applicationinsights-properties-js';

export type TrackerTelemetry = Pick<AnalyticsPlugin, 'trackPageView' | 'trackEvent'>;

export function createTrackerTelemetry(connectionString: string, userLoginName: string, beforePageHideFlush?: (persisted: boolean) => void): TrackerTelemetry {
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
    // A site registration can outlive its page while SPFx waits for this script. No view has
    // been accepted yet: a saved exit must not inherit a name from the ambient browser location.
    let unnamedTrace = core.getTraceCtx();
    if (unnamedTrace) unnamedTrace.pageName = 'SPOInsights';
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
    const flush = (event: Event) => {
        try {
            // Replacing the shared exit cookie can enqueue another hit. A tab closed while
            // already hidden has no later visibilitychange: drain the sender after the handoff.
            if (event.type === 'pagehide') beforePageHideFlush?.((event as PageTransitionEvent).persisted);
        } finally {
            sender.onunloadFlush();
            properties.context.sessionManager.backup();
        }
    };
    addPageHideEventListener(flush);
    addPageUnloadEventListener(flush);
    return {
        trackEvent: (event, customProperties) => analytics.trackEvent(event, customProperties),
        trackPageView(view, customProperties) {
            // Keep one operation per document as before. Bind its name to the first explicitly
            // tracked view, synchronously: the SDK may queue a view until its timings are ready.
            if (unnamedTrace && view?.uri) {
                try {
                    const page = new URL(view.uri);
                    unnamedTrace.pageName = page.pathname + page.hash;
                    unnamedTrace = null;
                } catch { /* No usable page address: leave the name neutral. */ }
            }
            analytics.trackPageView(view, customProperties);
        }
    };
}
