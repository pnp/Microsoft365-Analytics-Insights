/**
 * @jest-environment jsdom
 */

import { AppInsightsWrapper } from '../src/AppInsightsWrapper';
import { ApplicationInsights, IEventTelemetry, IPageViewTelemetry } from '@microsoft/applicationinsights-web';
import { ClickData } from '../src/Definitions';
import { PageProps } from '../src/PageProps/Models/PageProps';

// Mock ApplicationInsights
function createMockAI() {
    return {
        trackPageView: jest.fn(),
        trackEvent: jest.fn(),
        setAuthenticatedUserContext: jest.fn(),
        loadAppInsights: jest.fn(),
    } as unknown as ApplicationInsights;
}

describe('AppInsightsWrapper', () => {

    let mockAI: ApplicationInsights;
    let wrapper: AppInsightsWrapper;
    const sessionId = 'test-session-id';

    beforeEach(() => {
        mockAI = createMockAI();
        wrapper = new AppInsightsWrapper(mockAI, sessionId);

        // Set up a minimal document for URL and title
        Object.defineProperty(document, 'URL', { value: 'https://contoso.sharepoint.com/sites/test', writable: true, configurable: true });
        Object.defineProperty(document, 'title', { value: 'Test Page', writable: true, configurable: true });
    });

    describe('trackCurrentPageView', () => {
        test('tracks page view with explicit page load duration', () => {
            wrapper.trackCurrentPageView(150, null, 'https://web', 'https://site', 'Test Web');

            expect(mockAI.trackPageView).toHaveBeenCalledTimes(1);
            const callArg = (mockAI.trackPageView as jest.Mock).mock.calls[0][0] as IPageViewTelemetry;
            expect(callArg.uri).toBe('https://contoso.sharepoint.com/sites/test');
            expect(callArg.name).toBe('Test Page');
            expect(callArg.properties!['pageLoad']).toBe(150);
            expect(callArg.properties!.webUrl).toBe('https://web');
            expect(callArg.properties!.siteUrl).toBe('https://site');
            expect(callArg.properties!.sessionId).toBe(sessionId);
        });

        test('sets page request ID after tracking', () => {
            expect(wrapper._pageRequestId).toBeNull();
            wrapper.trackCurrentPageView(100, null, 'https://web', 'https://site', 'Web');
            expect(wrapper._pageRequestId).not.toBeNull();
            expect(typeof wrapper._pageRequestId).toBe('string');
        });

        test('includes spRequestDuration when provided', () => {
            wrapper.trackCurrentPageView(100, 42, 'https://web', 'https://site', 'Web');

            const callArg = (mockAI.trackPageView as jest.Mock).mock.calls[0][0] as IPageViewTelemetry;
            expect(callArg.properties!.spRequestDuration).toBe(42);
        });

        test('does not include spRequestDuration when null', () => {
            wrapper.trackCurrentPageView(100, null, 'https://web', 'https://site', 'Web');

            const callArg = (mockAI.trackPageView as jest.Mock).mock.calls[0][0] as IPageViewTelemetry;
            expect(callArg.properties!.spRequestDuration).toBeUndefined();
        });

        test('ignores duplicate page view for same URL', () => {
            wrapper.trackCurrentPageView(100, null, 'https://web', 'https://site', 'Web');
            wrapper.trackCurrentPageView(200, null, 'https://web', 'https://site', 'Web');

            expect(mockAI.trackPageView).toHaveBeenCalledTimes(1);
        });

        test('tracks when URL changes', () => {
            wrapper.trackCurrentPageView(100, null, 'https://web', 'https://site', 'Web');

            Object.defineProperty(document, 'URL', { value: 'https://contoso.sharepoint.com/sites/other', writable: true, configurable: true });
            wrapper.trackCurrentPageView(200, null, 'https://web', 'https://site', 'Web');

            expect(mockAI.trackPageView).toHaveBeenCalledTimes(2);
        });

        test('pageRequestId changes between different page views', () => {
            wrapper.trackCurrentPageView(100, null, 'https://web', 'https://site', 'Web');
            const firstPageReqId = wrapper._pageRequestId;

            Object.defineProperty(document, 'URL', { value: 'https://contoso.sharepoint.com/sites/other', writable: true, configurable: true });
            wrapper.trackCurrentPageView(200, null, 'https://web', 'https://site', 'Web');
            const secondPageReqId = wrapper._pageRequestId;

            expect(firstPageReqId).not.toBe(secondPageReqId);
        });

        test('uses performance API when no pageLoadDuration given', () => {
            wrapper.trackCurrentPageView(undefined, null, 'https://web', 'https://site', 'Web');
            expect(mockAI.trackPageView).toHaveBeenCalledTimes(1);

            const callArg = (mockAI.trackPageView as jest.Mock).mock.calls[0][0] as IPageViewTelemetry;
            expect(callArg.properties!['pageLoad']).toBeDefined();
            expect(typeof callArg.properties!['pageLoad']).toBe('number');
        });
    });

    describe('trackTimingEvent', () => {
        test('tracks timing event when page request ID exists', () => {
            // First track a page to get a page request ID
            wrapper.trackCurrentPageView(100, null, 'https://web', 'https://site', 'Web');
            (mockAI.trackEvent as jest.Mock).mockClear();

            wrapper.trackTimingEvent('https://contoso.sharepoint.com/sites/test', 45.5);

            expect(mockAI.trackEvent).toHaveBeenCalledTimes(1);
            const callArg = (mockAI.trackEvent as jest.Mock).mock.calls[0][0] as IEventTelemetry;
            expect(callArg.name).toBe('PAGE_EXIT');
            expect(callArg.properties!.activeTime).toBe(45.5);
            expect(callArg.properties!.url).toBe('https://contoso.sharepoint.com/sites/test');
            expect(callArg.properties!.sessionId).toBe(sessionId);
        });

        test('does not track when no page request ID', () => {
            wrapper.trackTimingEvent('https://test.com', 10);
            expect(mockAI.trackEvent).not.toHaveBeenCalled();
        });
    });

    describe('trackSearch', () => {
        test('tracks non-empty search term', () => {
            wrapper.trackSearch('sharepoint migration');

            expect(mockAI.trackEvent).toHaveBeenCalledTimes(1);
            const callArg = (mockAI.trackEvent as jest.Mock).mock.calls[0][0] as IEventTelemetry;
            expect(callArg.name).toBe('UserSearch');
            expect(callArg.properties!.userSearch).toBe('sharepoint migration');
            expect(callArg.properties!.sessionId).toBe(sessionId);
        });

        test('does not track empty search term', () => {
            wrapper.trackSearch('');
            expect(mockAI.trackEvent).not.toHaveBeenCalled();
        });

        test('tracks search with special characters', () => {
            wrapper.trackSearch('test "quoted" & <special>');
            expect(mockAI.trackEvent).toHaveBeenCalledTimes(1);
        });
    });

    describe('trackClick', () => {
        test('tracks click when page request ID exists', () => {
            wrapper.trackCurrentPageView(100, null, 'https://web', 'https://site', 'Web');
            (mockAI.trackEvent as jest.Mock).mockClear();

            const clickData: ClickData = {
                linkText: 'Click me',
                altText: 'Alt text',
                classNames: 'btn primary',
                href: 'https://target.com'
            };
            wrapper.trackClick(clickData);

            expect(mockAI.trackEvent).toHaveBeenCalledTimes(1);
            const callArg = (mockAI.trackEvent as jest.Mock).mock.calls[0][0] as IEventTelemetry;
            expect(callArg.name).toBe('LinkClick');
            expect(callArg.properties!.linkText).toBe('Click me');
            expect(callArg.properties!.altText).toBe('Alt text');
            expect(callArg.properties!.href).toBe('https://target.com');
            expect(callArg.properties!.classNames).toBe('btn primary');
        });

        test('does not track click when no page request ID', () => {
            const clickData: ClickData = { linkText: 'Link', altText: '', classNames: null, href: '' };
            wrapper.trackClick(clickData);
            expect(mockAI.trackEvent).not.toHaveBeenCalled();
        });

        test('omits empty optional click properties', () => {
            wrapper.trackCurrentPageView(100, null, 'https://web', 'https://site', 'Web');
            (mockAI.trackEvent as jest.Mock).mockClear();

            const clickData: ClickData = { linkText: '', altText: '', classNames: null, href: '' };
            wrapper.trackClick(clickData);

            expect(mockAI.trackEvent).toHaveBeenCalledTimes(1);
            const callArg = (mockAI.trackEvent as jest.Mock).mock.calls[0][0] as IEventTelemetry;
            expect(callArg.properties!.linkText).toBeUndefined();
            expect(callArg.properties!.altText).toBeUndefined();
            expect(callArg.properties!.href).toBeUndefined();
            expect(callArg.properties!.classNames).toBeUndefined();
        });

        test('includes only non-empty click properties', () => {
            wrapper.trackCurrentPageView(100, null, 'https://web', 'https://site', 'Web');
            (mockAI.trackEvent as jest.Mock).mockClear();

            const clickData: ClickData = { linkText: 'Text', altText: '', classNames: null, href: 'http://x.com' };
            wrapper.trackClick(clickData);

            const callArg = (mockAI.trackEvent as jest.Mock).mock.calls[0][0] as IEventTelemetry;
            expect(callArg.properties!.linkText).toBe('Text');
            expect(callArg.properties!.altText).toBeUndefined();
            expect(callArg.properties!.classNames).toBeUndefined();
            expect(callArg.properties!.href).toBe('http://x.com');
        });
    });

    describe('updatePageProps', () => {
        test('sends page properties as event', () => {
            const props = new PageProps('https://test.com', { title: 'Test' });
            wrapper.updatePageProps(props);

            expect(mockAI.trackEvent).toHaveBeenCalledTimes(1);
            const callArg = (mockAI.trackEvent as jest.Mock).mock.calls[0][0] as IEventTelemetry;
            expect(callArg.name).toBe('PageMetadataUpdate');
        });
    });
});
