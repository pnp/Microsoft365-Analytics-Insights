/** @jest-environment jsdom */
import { createTrackerTelemetry } from '../src/TrackerTelemetry';
import type { IConfiguration, ITelemetryItem } from '@microsoft/applicationinsights-core-js';

const mockItems: ITelemetryItem[] = [];
const mockFlush = jest.fn();
let mockConfig: IConfiguration;

// Keep the real analytics and context plugins; capture what reaches the sender without using the network.
jest.mock('@microsoft/applicationinsights-channel-js', () => ({
    Sender: class {
        identifier = 'AppInsightsChannelPlugin';
        priority = 1001;
        initialize(config: IConfiguration) { mockConfig = config; }
        processTelemetry(item: ITelemetryItem) { mockItems.push(item); }
        onunloadFlush = mockFlush;
    }
}));

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
    window.dispatchEvent(new Event('pagehide'));
    expect(mockFlush).toHaveBeenCalled();
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
