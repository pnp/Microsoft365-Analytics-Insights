/**
 * @jest-environment jsdom
 * @jest-environment-options {"url":"https://contoso.example/sites/tracked/home","referrer":"https://contoso.example/sites/other/start"}
 */
import { createTrackerTelemetry } from '../src/TrackerTelemetry';
import { DiagnosticLogger, EventDataType, PageViewDataType, PageViewPerformanceDataType } from '@microsoft/applicationinsights-core-js';
import type { IConfiguration, ITelemetryItem } from '@microsoft/applicationinsights-core-js';
import { AppInsightsWrapper } from '../src/AppInsightsWrapper';

const mockItems: ITelemetryItem[] = [];
let mockCurrentFlush: jest.Mock;
let mockConfig: IConfiguration;

// Keep the real analytics and context plugins; capture what reaches the sender without using the network.
jest.mock('@microsoft/applicationinsights-channel-js', () => ({
    Sender: class {
        identifier = 'AppInsightsChannelPlugin';
        priority = 1001;
        initialize(config: IConfiguration) { mockConfig = config; mockCurrentFlush = this.onunloadFlush; }
        processTelemetry(item: ITelemetryItem) { mockItems.push(item); }
        onunloadFlush = jest.fn();
    }
}));

const connectionString = 'InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://contoso.example/';

// Use the shipping SDK's envelope conversion, but never its network transport.
function envelope(baseType: string) {
    const item = mockItems.find(i => i.baseType === baseType);
    expect(item).toBeDefined();
    const { Sender } = jest.requireActual<typeof import('@microsoft/applicationinsights-channel-js')>('@microsoft/applicationinsights-channel-js');
    return Sender.constructEnvelope(item!, item!.iKey!, new DiagnosticLogger());
}

beforeEach(() => { mockItems.length = 0; });

test('a tracked view keeps its own address but does not send the ambient document referrer', () => {
    expect(document.referrer).not.toBe('');
    const telemetry = createTrackerTelemetry(connectionString, 'reader@contoso.example');
    telemetry.trackPageView({ name: 'Home', uri: document.URL });
    const view = envelope(PageViewDataType);
    expect(view.data.baseData.url).toBe(document.URL);
    expect(view.data.baseData.properties.refUri).toBeUndefined();
});

test.each(['tracked view', 'off-site startup', 'root registration retired'])('a saved event uses only explicitly tracked page context: %s', scenario => {
    const tracked = '/sites/tracked/home';
    const other = '/sites/other/home';
    const originalUrl = location.href;
    const originalSites = window._o365AnalyticsInfo;
    try {
        const startedOnTracked = scenario === 'tracked view';
        window._o365AnalyticsInfo = { siteUrls: [scenario === 'root registration retired' ? location.origin : location.origin + '/sites/tracked'] };
        history.replaceState(null, '', startedOnTracked ? tracked : other);
        const telemetry = createTrackerTelemetry(connectionString, 'reader@contoso.example');
        if (startedOnTracked) telemetry.trackPageView({ uri: document.URL, name: 'Home' });
        window._o365AnalyticsInfo.siteUrls.length = 0;
        history.replaceState(null, '', other);
        // A saved exit is about the previous tracked page, not the current browser location.
        telemetry.trackEvent({ name: 'PAGE_EXIT', properties: { url: location.origin + tracked, pageRequestId: 'previous-view' } });
        const event = envelope(EventDataType);
        expect(event.data.baseData.properties.pageRequestId).toBe('previous-view');
        expect(event.tags['ai.operation.id']).toBeTruthy();
        expect(event.tags['ai.operation.name']).toBe(startedOnTracked ? tracked : 'SPOInsights');
        expect(JSON.stringify(event)).not.toContain(other);
        if (!startedOnTracked) {
            history.replaceState(null, '', tracked);
            window._o365AnalyticsInfo.siteUrls.push(location.origin + '/sites/tracked');
            telemetry.trackPageView({ uri: document.URL, name: 'Home' });
            const view = envelope(PageViewDataType);
            expect(view.tags['ai.operation.id']).toBe(event.tags['ai.operation.id']);
            expect(view.tags['ai.operation.name']).toBe(tracked);
        }
    } finally {
        history.replaceState(null, '', originalUrl);
        window._o365AnalyticsInfo = originalSites;
    }
});

test.each([true, false])('the first view reports only its own document timing (soft navigation: %s)', softNavigation => {
    jest.useFakeTimers({ doNotFake: ['performance'] });
    const descriptors = Object.getOwnPropertyDescriptors(performance);
    const origin = Date.UTC(2000, 0, 1);
    const nav = { name: softNavigation ? document.referrer : document.URL, startTime: 0, connectEnd: 5, requestStart: 10, responseStart: 20, responseEnd: 30,
        loadEventStart: 90, loadEventEnd: 100, duration: 100 };
    const timing = { navigationStart: origin, domainLookupStart: origin + 1, domLoading: origin + 40,
        connectEnd: origin + 5, requestStart: origin + 10, responseStart: origin + 20, responseEnd: origin + 30, loadEventEnd: origin + 100 };
    const viewed = new Date(origin + 86400000);
    jest.setSystemTime(viewed);
    Object.defineProperties(performance, {
        timeOrigin: { configurable: true, value: origin },
        getEntriesByType: { configurable: true, value: () => [nav] },
        timing: { configurable: true, value: timing },
    });
    try {
        const telemetry = createTrackerTelemetry(connectionString, 'reader@contoso.example');
        const wrapper = new AppInsightsWrapper(telemetry, 'synthetic-session');
        wrapper.trackClick({ linkText: 'Early link', altText: '', classNames: null, href: '#link' });
        wrapper.trackCurrentPageView(softNavigation ? 0 : undefined, null, location.origin + '/sites/tracked', location.origin + '/sites/tracked', 'Home');
        jest.advanceTimersByTime(50); // A visit can end before the SDK's first 100 ms performance check.
        expect(mockItems.map(i => i.baseType)).toEqual([PageViewDataType, EventDataType]);
        const view = envelope(PageViewDataType);
        expect(view.time).toBe((softNavigation ? viewed : new Date(origin + nav.loadEventStart)).toISOString());
        expect(view.data.baseData.duration).toBe(softNavigation ? '00:00:00.000' : '00:00:00.100');
        expect(Number(view.data.baseData.properties.pageLoad)).toBe(softNavigation ? 0 : 100);
        expect(envelope(EventDataType).data.baseData.properties.pageRequestId).toBe(view.data.baseData.properties.pageRequestId);
        jest.advanceTimersByTime(200);
        expect(mockItems.filter(i => i.baseType === PageViewDataType)).toHaveLength(1);
        expect(mockItems.filter(i => i.baseType === PageViewPerformanceDataType)).toHaveLength(softNavigation ? 0 : 1);
    } finally {
        for (const key of ['timeOrigin', 'getEntriesByType', 'timing'] as const) {
            if (descriptors[key]) Object.defineProperty(performance, key, descriptors[key]);
            else Reflect.deleteProperty(performance, key);
        }
        jest.useRealTimers();
    }
});

test('keeps authenticated event context and the configured destination without hooking SharePoint requests, and flushes on pagehide', () => {
    const xhrOpen = XMLHttpRequest.prototype.open;
    const fetch = window.fetch;
    const key = '00000000-0000-0000-0000-000000000000';
    const telemetry = createTrackerTelemetry(`InstrumentationKey=${key};IngestionEndpoint=https://contoso.example/`, 'reader@contoso.example');
    telemetry.trackEvent({ name: 'LinkClick', properties: { pageRequestId: 'page-one' } });

    expect(mockConfig.endpointUrl).toBe('https://contoso.example/v2/track');
    expect(mockItems).toContainEqual(expect.objectContaining({
        iKey: key, baseType: 'EventData',
        baseData: expect.objectContaining({ name: 'LinkClick', properties: { pageRequestId: 'page-one' } }),
        ext: expect.objectContaining({ user: expect.objectContaining({ authId: 'reader@contoso.example' }) })
    }));
    expect(XMLHttpRequest.prototype.open).toBe(xhrOpen);
    expect(window.fetch).toBe(fetch);
    expect(document.cookie).not.toMatch(/(?:^|;\s*)ai_(?:user|session)=/);
    expect(mockCurrentFlush).not.toHaveBeenCalled();
    window.dispatchEvent(new Event('pagehide'));
    expect(mockCurrentFlush).toHaveBeenCalled();
});

test('refuses an endpoint without a key before installing any page handlers', () => {
    const listener = jest.spyOn(window, 'addEventListener');
    try {
        expect(() => createTrackerTelemetry('IngestionEndpoint=https://contoso.example/', 'reader@contoso.example')).toThrow();
        expect(listener).not.toHaveBeenCalled();
    } finally {
        listener.mockRestore();
    }
});

test('a hidden tab hands off its cookie on pagehide, not beforeunload or visibilitychange, before the final flush', () => {
    const handoff = jest.fn((_persisted: boolean) => telemetry.trackEvent({ name: 'PAGE_EXIT', properties: { pageRequestId: 'previous' } }));
    const telemetry = createTrackerTelemetry(connectionString, 'reader@contoso.example', handoff);
    const flushed: ITelemetryItem[] = [];
    mockCurrentFlush.mockImplementation(() => flushed.push(...mockItems.splice(0)));
    const hidden = jest.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden');
    try {
        document.dispatchEvent(new Event('visibilitychange'));
        window.dispatchEvent(new Event('beforeunload'));
        expect(handoff).not.toHaveBeenCalled();
        window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
        expect(handoff.mock.calls).toEqual([[true]]);
        expect(flushed.map(i => i.baseData?.properties.pageRequestId)).toEqual(['previous']);
    } finally {
        handoff.mockReset();
        mockCurrentFlush.mockReset();
        hidden.mockRestore();
    }
});
