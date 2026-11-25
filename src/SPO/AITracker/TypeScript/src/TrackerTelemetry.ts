import { AnalyticsPlugin } from '@microsoft/applicationinsights-analytics-js';
import { Sender } from '@microsoft/applicationinsights-channel-js';
import { AppInsightsCore, DEFAULT_BREEZE_PATH, addPageHideEventListener, addPageUnloadEventListener, parseConnectionString } from '@microsoft/applicationinsights-core-js';
import type { IConfig, IConfiguration } from '@microsoft/applicationinsights-core-js';
import { PropertiesPlugin } from '@microsoft/applicationinsights-properties-js';

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
    new AppInsightsCore().initialize(config, [properties, analytics, sender]);
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
