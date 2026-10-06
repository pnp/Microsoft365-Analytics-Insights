/**
 * @jest-environment jsdom
 */

// The page bootstrap in AITracker.ts runs as soon as the script loads. Each test loads a fresh copy of the module, as
// SharePoint does: classic pages load it from a ScriptLink custom action before the page has loaded; on modern pages the
// SPFx extension loads it, possibly after the page has loaded, and a page can end up with one copy per tracked site.

import Cookies from 'js-cookie';
import { AI_TRACKER_VER } from '../src/AiTrackerConstants';

const mockAiInstances: any[] = [];

jest.mock('@microsoft/applicationinsights-web', () => ({
    ApplicationInsights: jest.fn().mockImplementation((settings: any) => {
        const instance = {
            config: settings.config,
            // As the SDK does for a connection string without an instrumentation key
            loadAppInsights: jest.fn(() => {
                if (settings.config.connectionString.indexOf('InstrumentationKey=') === -1) throw new Error('Please provide instrumentation key');
            }),
            setAuthenticatedUserContext: jest.fn(),
            trackPageView: jest.fn(),
            trackEvent: jest.fn(),
        };
        mockAiInstances.push(instance);
        return instance;
    }),
}));

jest.mock('timeme.js', () => ({
    __esModule: true,
    default: { initialize: jest.fn(), getTimeOnCurrentPageInSeconds: jest.fn(() => 12) },
}));

jest.mock('../src/Config/ConfigHandler', () => ({
    ConfigHandler: jest.fn().mockImplementation(() => ({
        getConfigFromCacheOrAppService: () => Promise.resolve({ metadataRefreshMinutes: 1440, expiry: new Date(Date.now() + 60000) }),
    })),
}));

const PAGE_URL = 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx';
const CONNECTION_STRING_HASH = btoa('InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/');

let readyState: DocumentReadyState = 'complete';
let navigationEntries: any[] = [];
let listeners: { target: EventTarget, type: string, listener: any }[] = [];

Object.defineProperty(document, 'readyState', { configurable: true, get: () => readyState });
Object.defineProperty(window.performance, 'getEntriesByType', {
    configurable: true,
    value: (type: string) => type === 'navigation' ? navigationEntries : [],
});

function setUrl(url: string) {
    Object.defineProperty(document, 'URL', { value: url, writable: true, configurable: true });
}

function loadTrackerCopy(): void {
    jest.isolateModules(() => { require('../src/AITracker'); });
}

async function settle(): Promise<void> {
    await new Promise(r => setTimeout(r, 0));
    await new Promise(r => setTimeout(r, 0));
}

function mousedownOnLink(text: string): void {
    const a = document.createElement('a');
    a.href = 'https://contoso.sharepoint.com/sites/test/SitePages/Other.aspx';
    a.textContent = text;
    document.body.appendChild(a);
    a.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
}

const eventsNamed = (instance: any, name: string) => instance.trackEvent.mock.calls.map((c: any[]) => c[0]).filter((e: any) => e.name === name);
const pageRequestIdOf = (instance: any, pageView: number) => instance.trackPageView.mock.calls[pageView][0].properties.pageRequestId;
const noPageRequestIdErrors = (errorSpy: jest.SpyInstance) => errorSpy.mock.calls.filter(c => String(c[0]).includes('no page request ID'));

describe('AITracker page bootstrap', () => {

    beforeEach(() => {
        // Record listeners each copy adds, so they can be removed again: the jsdom document outlives a test
        for (const target of [document, window] as EventTarget[]) {
            const original = target.addEventListener;
            jest.spyOn(target, 'addEventListener').mockImplementation(function (type: string, listener: any, options?: any) {
                listeners.push({ target, type, listener });
                return original.call(target, type, listener, options);
            } as any);
        }
        jest.spyOn(console, 'debug').mockImplementation();
        jest.spyOn(console, 'log').mockImplementation();

        const w = window as any;
        delete w.spoInsightsAITrackerVersion;
        delete w.modernPageNav;
        w.onbeforeunload = null;
        w.appInsightsConnectionStringHash = CONNECTION_STRING_HASH;
        w.insightsWebRootUrlHash = btoa('https://contoso-analytics.example.invalid');
        w._spPageContextInfo = {
            userLoginName: 'user@contoso.com', webAbsoluteUrl: 'https://contoso.sharepoint.com/sites/test',
            siteAbsoluteUrl: 'https://contoso.sharepoint.com/sites/test', webTitle: 'Test'
        };

        mockAiInstances.length = 0;
        Object.keys(Cookies.get()).forEach(name => Cookies.remove(name));
        document.body.innerHTML = '';
        readyState = 'complete';
        setUrl(PAGE_URL);
        navigationEntries = [{ name: PAGE_URL, startTime: 0, loadEventEnd: 1500 }];
    });

    afterEach(async () => {
        await settle();     // Let a copy's pending setTimeout finish here, not in the next test
        listeners.forEach(l => l.target.removeEventListener(l.type, l.listener));
        listeners = [];
        jest.restoreAllMocks();
    });

    test('loaded before the page has loaded (classic pages, normal modern page loads): tracks the page on load', async () => {
        readyState = 'interactive';
        loadTrackerCopy();
        await settle();

        const ai = mockAiInstances[0];
        expect(ai.trackPageView).not.toHaveBeenCalled();
        expect((window as any).modernPageNav).toBeUndefined();

        readyState = 'complete';
        window.dispatchEvent(new Event('load'));
        await settle();

        expect(ai.trackPageView).toHaveBeenCalledTimes(1);
        expect(ai.trackPageView.mock.calls[0][0].properties.pageLoad).toBe(1500);
        expect(typeof (window as any).modernPageNav).toBe('function');
    });

    test('a link clicked before the page has loaded is sent with the page view, not dropped', async () => {
        const errorSpy = jest.spyOn(console, 'error').mockImplementation();
        readyState = 'interactive';
        loadTrackerCopy();
        mousedownOnLink('Early link');

        const ai = mockAiInstances[0];
        expect(eventsNamed(ai, 'LinkClick').length).toBe(0);

        readyState = 'complete';
        window.dispatchEvent(new Event('load'));
        await settle();

        const clicks = eventsNamed(ai, 'LinkClick');
        expect(clicks.length).toBe(1);
        expect(clicks[0].properties.pageRequestId).toBe(pageRequestIdOf(ai, 0));
        expect(noPageRequestIdErrors(errorSpy)).toEqual([]);
    });

    test('a click on a page SharePoint leaves before it has loaded is not counted against the next page', async () => {
        jest.spyOn(console, 'error').mockImplementation();
        readyState = 'interactive';
        loadTrackerCopy();
        mousedownOnLink('News');

        // SharePoint navigates to the next page without reloading, before the first one's load event
        setUrl('https://contoso.sharepoint.com/sites/test/SitePages/News.aspx');
        readyState = 'complete';
        window.dispatchEvent(new Event('load'));
        await settle();

        const ai = mockAiInstances[0];
        expect(ai.trackPageView).toHaveBeenCalledTimes(1);
        expect(ai.trackPageView.mock.calls[0][0].uri).toBe('https://contoso.sharepoint.com/sites/test/SitePages/News.aspx');
        expect(eventsNamed(ai, 'LinkClick').length).toBe(0);
    });

    test('a copy whose App Insights can\'t start leaves the page to a correctly configured copy', async () => {
        const errorSpy = jest.spyOn(console, 'error').mockImplementation();
        (window as any).appInsightsConnectionStringHash = btoa('IngestionEndpoint=https://example.invalid/');
        loadTrackerCopy();
        await settle();

        expect((window as any).spoInsightsAITrackerVersion).toBeUndefined();
        expect((window as any).modernPageNav).toBeUndefined();
        expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining("Couldn't start Application Insights"));

        // SharePoint navigates to another site, whose copy is configured correctly
        (window as any).appInsightsConnectionStringHash = CONNECTION_STRING_HASH;
        loadTrackerCopy();
        await settle();

        expect(mockAiInstances.length).toBe(2);
        expect(mockAiInstances[1].trackPageView).toHaveBeenCalledTimes(1);
        expect((window as any).spoInsightsAITrackerVersion).toBe(AI_TRACKER_VER);
    });

    test('removes the page-address cookie earlier versions wrote, in a browser session that started before the upgrade', async () => {
        Cookies.set('SPOInsightsSessionID', '00000000-0000-0000-0000-000000000001');
        Cookies.set('SPOInsightsLastTrackedUrl', PAGE_URL);
        loadTrackerCopy();
        await settle();

        expect(Cookies.get('SPOInsightsLastTrackedUrl')).toBeUndefined();
        expect(Cookies.get('SPOInsightsSessionID')).toBe('00000000-0000-0000-0000-000000000001');
        expect(mockAiInstances[0].trackPageView).toHaveBeenCalledTimes(1);
    });

    test('loaded after the page has loaded: tracks the page straight away and can track clicks', async () => {
        const errorSpy = jest.spyOn(console, 'error').mockImplementation();
        loadTrackerCopy();
        await settle();

        const ai = mockAiInstances[0];
        expect(ai.trackPageView).toHaveBeenCalledTimes(1);
        expect(ai.trackPageView.mock.calls[0][0].properties.pageLoad).toBe(1500);     // The browser loaded this URL
        expect(typeof (window as any).modernPageNav).toBe('function');
        expect((window as any).spoInsightsAITrackerVersion).toBe(AI_TRACKER_VER);

        mousedownOnLink('Link');
        expect(eventsNamed(ai, 'LinkClick').length).toBe(1);
        expect(noPageRequestIdErrors(errorSpy)).toEqual([]);
    });

    test('loaded after SharePoint navigated here from an untracked site: reports a load time of 0', async () => {
        navigationEntries = [{ name: 'https://contoso.sharepoint.com/sites/untracked', startTime: 0, loadEventEnd: 1500 }];
        loadTrackerCopy();
        await settle();

        expect(mockAiInstances[0].trackPageView.mock.calls[0][0].properties.pageLoad).toBe(0);
    });

    test('a second copy loaded into the same page, from another tracked site, leaves tracking to the first', async () => {
        const errorSpy = jest.spyOn(console, 'error').mockImplementation();
        loadTrackerCopy();
        await settle();
        const listenersAddedByFirstCopy = listeners.length;

        setUrl('https://contoso.sharepoint.com/sites/other');
        loadTrackerCopy();
        await settle();

        expect(mockAiInstances.length).toBe(1);
        expect(listeners.length).toBe(listenersAddedByFirstCopy);

        mousedownOnLink('Link');
        expect(eventsNamed(mockAiInstances[0], 'LinkClick').length).toBe(1);
        expect(noPageRequestIdErrors(errorSpy)).toEqual([]);
    });

    test('leaves tracking to an earlier version of the script that is already tracking the page', async () => {
        (window as any).modernPageNav = jest.fn();
        loadTrackerCopy();
        await settle();

        expect(mockAiInstances.length).toBe(0);
    });

    test('a click on an icon link without text is sent with its accessible label, so the importer keeps it', async () => {
        loadTrackerCopy();
        await settle();

        const a = document.createElement('a');
        a.href = 'https://contoso.sharepoint.com';
        a.setAttribute('aria-label', 'Contoso');
        a.innerHTML = '<svg></svg>';
        document.body.appendChild(a);
        a.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));

        const clicks = eventsNamed(mockAiInstances[0], 'LinkClick');
        expect(clicks.length).toBe(1);
        expect(clicks[0].properties.linkText).toBe('Contoso');
    });

    test('forwards SharePoint page navigations from the SPFx extension, ignoring its report of the current page', async () => {
        loadTrackerCopy();
        await settle();
        const ai = mockAiInstances[0];
        const firstPageRequestId = pageRequestIdOf(ai, 0);

        (window as any).modernPageNav('https://contoso.sharepoint.com/sites/test', 'Test', 'https://contoso.sharepoint.com/sites/test');
        expect(ai.trackPageView).toHaveBeenCalledTimes(1);
        expect(eventsNamed(ai, 'PAGE_EXIT').length).toBe(0);

        setUrl('https://contoso.sharepoint.com/sites/test/SitePages/News.aspx');
        (window as any).modernPageNav('https://contoso.sharepoint.com/sites/test', 'Test', 'https://contoso.sharepoint.com/sites/test');

        expect(ai.trackPageView).toHaveBeenCalledTimes(2);
        expect(ai.trackPageView.mock.calls[1][0].properties.pageLoad).toBe(0);
        const exits = eventsNamed(ai, 'PAGE_EXIT');
        expect(exits.length).toBe(1);
        expect(exits[0].properties.pageRequestId).toBe(firstPageRequestId);
    });

    test('saves time on page as the browser leaves, without replacing SharePoint\'s window.onbeforeunload', async () => {
        const sharePointHandler = jest.fn();
        (window as any).onbeforeunload = sharePointHandler;
        loadTrackerCopy();
        await settle();

        expect((window as any).onbeforeunload).toBe(sharePointHandler);

        window.dispatchEvent(new Event('pagehide'));
        const saved = JSON.parse(Cookies.get('SPOInsightsLastPageStats')!);
        expect(saved.pageRequestId).toBe(pageRequestIdOf(mockAiInstances[0], 0));
        expect(saved.secondsOnPage).toBe(12);
    });

    test('tracks the search on a search results page the browser loaded', async () => {
        const url = 'https://contoso.sharepoint.com/sites/test/_layouts/15/search.aspx/siteall?q=contoso';
        setUrl(url);
        navigationEntries = [{ name: url, startTime: 0, loadEventEnd: 900 }];
        loadTrackerCopy();
        await settle();

        expect(eventsNamed(mockAiInstances[0], 'UserSearch').map((e: any) => e.properties.userSearch)).toEqual(['contoso']);
    });

    test('does not send SharePoint\'s own requests to App Insights', () => {
        loadTrackerCopy();

        const config = mockAiInstances[0].config;
        expect(config.disableAjaxTracking).toBe(true);
        expect(config.disableFetchTracking).toBe(true);
        expect(config.enableDebug).toBeFalsy();
    });

    test('without a connection string, does not start - so a configured copy still can', async () => {
        const errorSpy = jest.spyOn(console, 'error').mockImplementation();
        delete (window as any).appInsightsConnectionStringHash;
        loadTrackerCopy();
        await settle();

        expect(mockAiInstances.length).toBe(0);
        expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('No valid Application Insights connection string'));

        (window as any).appInsightsConnectionStringHash = CONNECTION_STRING_HASH;
        loadTrackerCopy();
        await settle();
        expect(mockAiInstances.length).toBe(1);
    });
});
