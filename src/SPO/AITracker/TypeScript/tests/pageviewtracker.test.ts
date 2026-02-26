/**
 * @jest-environment jsdom
 */

import { PageViewTracker } from '../src/PageViewTracker';
import { AppInsightsWrapper } from '../src/AppInsightsWrapper';
import { ApplicationInsights } from '@microsoft/applicationinsights-web';
import { InMemoryPageStateManager } from '../src/PageProps/PageState';
import { spPageContextInfo } from '../src/Definitions';
import { TestPagePropertyManager, TestPageDataService } from './MockLoaders';
import Cookies from 'js-cookie';

function createMockAI(): ApplicationInsights {
    return {
        trackPageView: jest.fn(),
        trackEvent: jest.fn(),
        setAuthenticatedUserContext: jest.fn(),
        loadAppInsights: jest.fn(),
    } as unknown as ApplicationInsights;
}

function createContext(): spPageContextInfo {
    return {
        userLoginName: 'user@contoso.com',
        webAbsoluteUrl: 'https://contoso.sharepoint.com/sites/test',
        siteAbsoluteUrl: 'https://contoso.sharepoint.com',
        webTitle: 'Test Site',
        pageItemId: 1,
        listTitle: 'Site Pages'
    };
}

// Mock timeme.js
jest.mock('timeme.js', () => ({
    __esModule: true,
    default: {
        getTimeOnCurrentPageInSeconds: jest.fn(() => 10),
        initialize: jest.fn(),
    },
}));

describe('PageViewTracker', () => {
    let mockAppInsights: ApplicationInsights;
    let wrapper: AppInsightsWrapper;
    let tracker: PageViewTracker;
    let context: spPageContextInfo;
    let stateManager: InMemoryPageStateManager;
    let pagePropManager: TestPagePropertyManager;

    beforeEach(() => {
        // Clear all cookies
        Object.keys(Cookies.get()).forEach(name => Cookies.remove(name));

        mockAppInsights = createMockAI();
        wrapper = new AppInsightsWrapper(mockAppInsights, 'session-123');
        context = createContext();
        stateManager = new InMemoryPageStateManager();
        pagePropManager = new TestPagePropertyManager('testVal', stateManager, new TestPageDataService());
        tracker = new PageViewTracker(wrapper, context, pagePropManager);

        // Setup window._spPageContextInfo
        (window as any)._spPageContextInfo = context;
        Object.defineProperty(document, 'URL', { value: 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx', writable: true, configurable: true });
        Object.defineProperty(document, 'title', { value: 'Home', writable: true, configurable: true });
    });

    describe('trackCurrentPageViewAndLastPageExit', () => {
        test('tracks current page view', () => {
            tracker.trackCurrentPageViewAndLastPageExit(
                'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx',
                'Site Pages', 1
            );

            expect(mockAppInsights.trackPageView).toHaveBeenCalledTimes(1);
        });

        test('tracks last page stats from cookie if present', () => {
            // Set up a last-page cookie
            Cookies.set('SPOInsightsLastPageStats', JSON.stringify({
                pageRequestId: 'prev-req-id',
                secondsOnPage: 30,
                url: 'https%3A%2F%2Fcontoso.sharepoint.com%2Fsites%2Ftest%2FSitePages%2FOld.aspx'
            }));

            tracker.trackCurrentPageViewAndLastPageExit(
                'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx',
                'Site Pages', 1
            );

            // Page view + timing event for previous page
            expect(mockAppInsights.trackPageView).toHaveBeenCalledTimes(1);
        });

        test('clears last page stats cookie after tracking', () => {
            Cookies.set('SPOInsightsLastPageStats', JSON.stringify({
                pageRequestId: 'req-1',
                secondsOnPage: 5,
                url: 'https://old.com'
            }));

            tracker.trackCurrentPageViewAndLastPageExit(
                'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx',
                'Site Pages', 1
            );

            expect(Cookies.get('SPOInsightsLastPageStats')).toBeUndefined();
        });

        test('does nothing if _spPageContextInfo is undefined', () => {
            delete (window as any)._spPageContextInfo;
            (window as any)._spPageContextInfo = undefined;

            tracker.trackCurrentPageViewAndLastPageExit('https://test.com', 'Site Pages', 1);
            expect(mockAppInsights.trackPageView).not.toHaveBeenCalled();
        });
    });

    describe('handleModernPageNav', () => {
        test('tracks timing for previous page and new page view', () => {
            // First track a page to establish state
            tracker.trackCurrentPageViewAndLastPageExit(
                'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx',
                'Site Pages', 1
            );

            // Set up a "last tracked page" cookie
            Cookies.set('SPOInsightsLastTrackedUrl', 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx');

            Object.defineProperty(document, 'URL', {
                value: 'https://contoso.sharepoint.com/sites/test/SitePages/About.aspx',
                writable: true, configurable: true
            });

            tracker.handleModernPageNav(
                'https://contoso.sharepoint.com/sites/test',
                'Test Site',
                'https://contoso.sharepoint.com',
                'https://contoso.sharepoint.com/sites/test/SitePages/About.aspx',
                'Site Pages', 2
            );

            // Should have tracked page exit for old page (via trackEvent) and new page view
            expect(mockAppInsights.trackPageView).toHaveBeenCalledTimes(2);
            expect(mockAppInsights.trackEvent).toHaveBeenCalled();
        });

        test('tracks new page with 0 load duration', () => {
            // Navigate somewhere first
            Cookies.set('SPOInsightsLastTrackedUrl', 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx');

            Object.defineProperty(document, 'URL', {
                value: 'https://contoso.sharepoint.com/sites/test/SitePages/New.aspx',
                writable: true, configurable: true
            });

            tracker.handleModernPageNav(
                'https://contoso.sharepoint.com/sites/test',
                'Test Site',
                'https://contoso.sharepoint.com',
                'https://contoso.sharepoint.com/sites/test/SitePages/New.aspx'
            );

            expect(mockAppInsights.trackPageView).toHaveBeenCalledTimes(1);
            const callArg = (mockAppInsights.trackPageView as jest.Mock).mock.calls[0][0];
            expect(callArg.properties['pageLoad']).toBe(0);
        });
    });

    describe('getTimeOnPageAndResetLastTotalTime', () => {
        test('returns time on page, accounting for previous subtraction', () => {
            const time = tracker.getTimeOnPageAndResetLastTotalTime();
            expect(time).toBe(10); // mocked timeme returns 10

            // Second call should return 0 since the mock still returns 10
            const time2 = tracker.getTimeOnPageAndResetLastTotalTime();
            expect(time2).toBe(0);
        });
    });

    describe('savePageExitToCookie', () => {
        test('saves page exit stats when page request ID exists', () => {
            // Track a page first to get a request ID
            tracker.trackCurrentPageViewAndLastPageExit(
                'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx',
                'Site Pages', 1
            );

            tracker.savePageExitToCookie();

            const stats = Cookies.get('SPOInsightsLastPageStats');
            expect(stats).toBeDefined();
            const parsed = JSON.parse(stats!);
            expect(parsed.pageRequestId).toBeTruthy();
            expect(parsed.url).toBeDefined();
        });

        test('does not save when no page request ID', () => {
            tracker.savePageExitToCookie();
            expect(Cookies.get('SPOInsightsLastPageStats')).toBeUndefined();
        });
    });

    describe('updatePageContext', () => {
        test('updates internal context', () => {
            const newContext: spPageContextInfo = {
                ...context,
                webTitle: 'New Title',
                userLoginName: 'other@contoso.com'
            };
            tracker.updatePageContext(newContext);
            expect(tracker._context.webTitle).toBe('New Title');
            expect(tracker._context.userLoginName).toBe('other@contoso.com');
        });
    });

    describe('setPageUpdateIntervalMinutes', () => {
        test('delegates to page property manager', () => {
            const spy = jest.spyOn(pagePropManager, 'setPageUpdateIntervalMinutes');
            tracker.setPageUpdateIntervalMinutes(120);
            expect(spy).toHaveBeenCalledWith(120);
        });
    });
});
