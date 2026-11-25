/**
 * @jest-environment jsdom
 */

// The page bootstrap in AITracker.ts runs as soon as the script loads. Each test loads a fresh copy of the module, as
// SharePoint does: classic pages load it from a ScriptLink custom action before the page has loaded; on modern pages the
// SPFx extension loads it, possibly after the page has loaded, and a page can end up with one copy per tracked site.

import Cookies from 'js-cookie';
import { AI_TRACKER_VER } from '../src/AiTrackerConstants';

const mockAiInstances: any[] = [];
let mockSecondsOnPage = 12;         // TimeMe's running total of active seconds on page
let mockActiveClock: (() => number) | undefined;

jest.mock('../src/TrackerTelemetry', () => ({
    createTrackerTelemetry: jest.fn((connectionString: string) => {
        const instance = { trackPageView: jest.fn(), trackEvent: jest.fn() };
        mockAiInstances.push(instance);
        if (!connectionString.includes('InstrumentationKey=')) throw new Error('Missing instrumentation key');
        return instance;
    }),
}));

jest.mock('timeme.js', () => ({
    __esModule: true,
    default: {
        initialize: jest.fn(),
        getTimeOnCurrentPageInSeconds: () => mockActiveClock ? mockActiveClock() / 1000 : mockSecondsOnPage,
        getTimeOnCurrentPageInMilliseconds: () => mockActiveClock ? mockActiveClock() : mockSecondsOnPage * 1000,
    },
}));

jest.mock('../src/Config/ConfigHandler', () => ({
    ConfigHandler: jest.fn().mockImplementation(() => ({
        getConfigFromCacheOrAppService: () => Promise.resolve({ metadataRefreshMinutes: 1440, expiry: new Date(Date.now() + 60000) }),
    })),
}));

const PAGE_URL = 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx';
const CONNECTION_STRING_HASH = btoa('InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/');

// On modern pages the SPFx extension of each tracked site collection registers it in the page (window._o365AnalyticsInfo)
const SITE_URL = 'https://contoso.sharepoint.com/sites/test';
const OTHER_SITE_URL = 'https://contoso.sharepoint.com/sites/other';
const OTHER_PAGE_URL = OTHER_SITE_URL + '/SitePages/News.aspx';
const UNTRACKED_SITE_URL = 'https://contoso.sharepoint.com/sites/untracked';
const UNTRACKED_PAGE_URL = UNTRACKED_SITE_URL + '/SitePages/Private.aspx';

let readyState: DocumentReadyState = 'complete';
let navigationEntries: any[] = [];
let historyChanges: EventTarget | undefined;
let listeners: { target: EventTarget, type: string, listener: any, options?: AddEventListenerOptions | boolean }[] = [];
let intervals: Function[] = [];

Object.defineProperty(document, 'readyState', { configurable: true, get: () => readyState });
Object.defineProperty(window, 'navigation', { configurable: true, get: () => historyChanges });
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

function mousedownOnLink(text: string, href: string = 'https://contoso.sharepoint.com/sites/test/SitePages/Other.aspx', button: number = 0): void {
    const a = document.createElement('a');
    a.href = href;
    a.textContent = text;
    document.body.appendChild(a);
    a.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, button: button }));
}

function sitesTrackedInPage(...siteUrls: string[]): void {
    (window as any)._o365AnalyticsInfo = { siteUrls: siteUrls, lastUrlTracked: undefined };
}

// SharePoint navigates to a site collection the extension isn't enabled on, without reloading the page. It disposes of the
// extension, which unregisters its site, and there is no extension for the site navigated to.
function navigateToUntrackedSite(): void {
    setUrl(UNTRACKED_PAGE_URL);
    sitesTrackedInPage();
}

// The tracker's regular check that the browser is still on a tracked site
const checkSite = () => intervals.forEach(check => check());

const eventsNamed = (instance: any, name: string) => instance.trackEvent.mock.calls.map((c: any[]) => c[0]).filter((e: any) => e.name === name);
const pageRequestIdOf = (instance: any, pageView: number) => instance.trackPageView.mock.calls[pageView][0].properties.pageRequestId;
const noPageRequestIdErrors = (errorSpy: jest.SpyInstance) => errorSpy.mock.calls.filter(c => String(c[0]).includes('no page request ID'));

describe('AITracker page bootstrap', () => {

    beforeEach(() => {
        // Record listeners each copy adds, so they can be removed again: the jsdom document outlives a test
        for (const target of [document, window] as EventTarget[]) {
            const original = target.addEventListener;
            jest.spyOn(target, 'addEventListener').mockImplementation(function (type: string, listener: any, options?: any) {
                listeners.push({ target, type, listener, options });
                return original.call(target, type, listener, options);
            } as any);
        }
        jest.spyOn(console, 'debug').mockImplementation();
        jest.spyOn(console, 'log').mockImplementation();
        intervals = [];
        jest.spyOn(window, 'setInterval').mockImplementation(((check: Function) => intervals.push(check)) as any);

        const w = window as any;
        delete w.spoInsightsAITrackerVersion;
        delete w.modernPageNav;
        delete w._o365AnalyticsInfo;
        w.onbeforeunload = null;
        w.appInsightsConnectionStringHash = CONNECTION_STRING_HASH;
        w.insightsWebRootUrlHash = btoa('https://contoso-analytics.example.invalid');
        w._spPageContextInfo = {
            userLoginName: 'user@contoso.com', webAbsoluteUrl: 'https://contoso.sharepoint.com/sites/test',
            siteAbsoluteUrl: 'https://contoso.sharepoint.com/sites/test', webTitle: 'Test'
        };

        mockAiInstances.length = 0;
        mockSecondsOnPage = 12;
        historyChanges = undefined;
        Object.keys(Cookies.get()).forEach(name => Cookies.remove(name));
        document.body.innerHTML = '';
        document.title = '';
        readyState = 'complete';
        setUrl(PAGE_URL);
        navigationEntries = [{ name: PAGE_URL, startTime: 0, loadEventEnd: 1500 }];
    });

    afterEach(async () => {
        await settle();     // Let a copy's pending setTimeout finish here, not in the next test
        listeners.forEach(l => l.target.removeEventListener(l.type, l.listener, l.options));
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

    test('returning to the document URL before the first view does not revive the original load or an intervening title', async () => {
        readyState = 'interactive';
        historyChanges = new EventTarget();
        sitesTrackedInPage(SITE_URL);
        loadTrackerCopy();
        setUrl(PAGE_URL + '?q=intervening');
        historyChanges.dispatchEvent(new Event('currententrychange'));
        document.title = 'Intervening title';
        setUrl(PAGE_URL);
        historyChanges.dispatchEvent(new Event('currententrychange'));
        readyState = 'complete';
        window.dispatchEvent(new Event('load'));
        await settle();
        const ai = mockAiInstances[0];
        expect(ai.trackPageView).not.toHaveBeenCalled();
        expect(window.modernPageNav(SITE_URL, 'Test', SITE_URL)).toBe(true);
        const view = ai.trackPageView.mock.calls[0][0];
        expect(view).toMatchObject({ uri: PAGE_URL, name: new URL(PAGE_URL).pathname, properties: { pageLoad: 0 } });
        expect(view.properties).not.toHaveProperty('pageTitle');
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

    describe('active time across visits', () => {
        const destination = PAGE_URL + '?q=next';
        const saved = () => JSON.parse(Cookies.get('SPOInsightsLastPageStats')!);
        const report = () => window.modernPageNav(SITE_URL, 'Test', SITE_URL);
        const navigate = (url: string) => {
            setUrl(url);
            historyChanges?.dispatchEvent(new Event('currententrychange'));
        };
        let inactiveMs: number;
        beforeEach(() => {
            jest.useFakeTimers({ doNotFake: ['performance'] });
            const start = Date.now();
            inactiveMs = 0;
            mockActiveClock = () => Date.now() - start - inactiveMs;
        });
        afterEach(() => { mockActiveClock = undefined; jest.useRealTimers(); });
        function startModern(notifications = true) {
            if (notifications) historyChanges = new EventTarget();
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            jest.advanceTimersByTime(0);
            return mockAiInstances[0];
        }

        test.each([
            ['currententrychange', 1000, 2.5, 2.2], ['popstate', 1000, 2.5, 2.2],
            ['poll', 1000, 3, 1.7], ['click before poll', 100, 2.6, 2.1],
        ] as const)('times both views from the first observation, not the delayed SPFx report (%s)', (signal, clickDelay, sourceTime, destinationTime) => {
            const ai = startModern(signal === 'currententrychange');
            jest.advanceTimersByTime(2500);
            mousedownOnLink('Source', destination);
            navigate(destination);
            if (signal === 'popstate') window.dispatchEvent(new PopStateEvent('popstate'));
            setTimeout(report, 2000);
            jest.advanceTimersByTime(clickDelay); // With no notification, the first poll on B is at 3 s.
            mousedownOnLink('Gap');
            jest.advanceTimersByTime(2000 - clickDelay);
            mousedownOnLink('After report', PAGE_URL);
            jest.advanceTimersByTime(200);
            window.dispatchEvent(new Event('pagehide'));

            const a = pageRequestIdOf(ai, 0), b = pageRequestIdOf(ai, 1);
            expect(ai.trackPageView.mock.calls.map(c => c[0].properties.pageLoad)).toEqual([1500, 0]);
            expect(eventsNamed(ai, 'LinkClick').map(e => e.properties.pageRequestId)).toEqual([a, b, b]);
            expect([...eventsNamed(ai, 'PAGE_EXIT').map(e => [e.properties.pageRequestId, e.properties.activeTime]),
                [saved().pageRequestId, saved().secondsOnPage]])
                .toEqual([[a, sourceTime], [b, destinationTime]]);
        });

        test('a persisted pagehide before the report cannot replace the accepted exit with a short delta on restore', () => {
            window.addEventListener('pageshow', e => {
                if (e.persisted) mousedownOnLink('Restored click', PAGE_URL);
            }, { once: true });
            const ai = startModern();
            jest.advanceTimersByTime(120000);
            navigate(destination);
            setTimeout(report, 2000);
            jest.advanceTimersByTime(1250);
            mousedownOnLink('Abandoned click');
            window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
            const handoff = saved();
            Cookies.remove('SPOInsightsLastPageStats'); // The intervening document sent this snapshot.

            jest.setSystemTime(Date.now() + 60000); // Suspended JS: the callback still has 750 ms to go.
            inactiveMs += 60000;                    // TimeMe stopped while away.
            document.dispatchEvent(new Event('visibilitychange')); // Edge makes the restored document visible before pageshow.
            window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
            jest.advanceTimersByTime(750);
            const a = pageRequestIdOf(ai, 0), b = pageRequestIdOf(ai, 1);
            const snapshots = [[handoff.pageRequestId, handoff.secondsOnPage],
                ...eventsNamed(ai, 'PAGE_EXIT').map(e => [e.properties.pageRequestId, e.properties.activeTime])];
            expect(snapshots.length).toBeLessThanOrEqual(2);
            expect(snapshots).toEqual(snapshots.map(() => [a, 120]));
            expect(eventsNamed(ai, 'LinkClick').map(e => [e.properties.linkText, e.properties.pageRequestId])).toEqual([['Restored click', b]]);
            jest.advanceTimersByTime(1000);
            window.dispatchEvent(new Event('pagehide'));
            expect([saved().pageRequestId, saved().secondsOnPage]).toEqual([b, 1.75]);
        });

        test('a cross-collection destination earns time from registration, not the next poll or its report', () => {
            const ai = startModern();
            const sites = window._o365AnalyticsInfo!;
            jest.advanceTimersByTime(2500);
            navigate(OTHER_PAGE_URL); // The browser moved before the new customizer started.
            jest.advanceTimersByTime(100);
            sites.siteUrls.push(OTHER_SITE_URL);
            sites.siteRegistrationChanged!(OTHER_SITE_URL);
            expect(ai.trackPageView).toHaveBeenCalledTimes(1);
            jest.advanceTimersByTime(2000);
            window.modernPageNav(OTHER_SITE_URL, 'Other', OTHER_SITE_URL);
            jest.advanceTimersByTime(200);
            window.dispatchEvent(new Event('pagehide'));
            expect(eventsNamed(ai, 'PAGE_EXIT').map(e => e.properties.activeTime)).toEqual([2.5]);
            expect([saved().pageRequestId, saved().secondsOnPage]).toEqual([pageRequestIdOf(ai, 1), 2.2]);
        });

        test('a still-registered root stops earning time when the browser enters an unregistered collection', () => {
            const root = new URL(SITE_URL).origin, home = root + '/SitePages/Home.aspx';
            setUrl(home);
            navigationEntries[0].name = home;
            historyChanges = new EventTarget();
            sitesTrackedInPage(root);
            Object.assign(window._spPageContextInfo, { webAbsoluteUrl: root, siteAbsoluteUrl: root });
            loadTrackerCopy();
            jest.advanceTimersByTime(0);
            const ai = mockAiInstances[0];
            jest.advanceTimersByTime(2500);
            navigate(root + '/Teams/Unregistered/Pages/Home.aspx?q=unregistered');
            mousedownOnLink('Unregistered link');
            jest.advanceTimersByTime(5000);
            expect(window.modernPageNav(root, 'Root', root)).toBe(false);
            window.dispatchEvent(new Event('pagehide'));
            expect(ai.trackPageView.mock.calls.map(c => c[0].uri)).toEqual([home]);
            expect(ai.trackEvent).not.toHaveBeenCalled();
            expect([decodeURI(saved().url), saved().pageRequestId, saved().secondsOnPage]).toEqual([home, pageRequestIdOf(ai, 0), 2.5]);
        });

        test('an unrelated registration does not reset a same-site visit; removing its own site stops its clock immediately', () => {
            const ai = startModern();
            const sites = window._o365AnalyticsInfo!;
            jest.advanceTimersByTime(2500);
            navigate(destination);
            jest.advanceTimersByTime(250);
            sites.siteUrls.push(OTHER_SITE_URL);
            sites.siteRegistrationChanged!(OTHER_SITE_URL);
            jest.advanceTimersByTime(1750);
            report();
            jest.advanceTimersByTime(200);
            sites.siteUrls.splice(sites.siteUrls.indexOf(SITE_URL), 1);
            sites.siteRegistrationChanged!(SITE_URL);
            jest.advanceTimersByTime(5000);
            mousedownOnLink('Outside registration');
            window.dispatchEvent(new Event('pagehide'));
            expect(eventsNamed(ai, 'LinkClick')).toEqual([]);
            expect(eventsNamed(ai, 'PAGE_EXIT').map(e => e.properties.activeTime)).toEqual([2.5]);
            expect([saved().pageRequestId, saved().secondsOnPage]).toEqual([pageRequestIdOf(ai, 1), 2.2]);
        });

        test('repeated classic pagehide snapshots include each active interval once under the original ID', () => {
            loadTrackerCopy();
            jest.advanceTimersByTime(0);
            const ai = mockAiInstances[0], id = pageRequestIdOf(ai, 0);
            for (const [seconds, total] of [[3, 3], [5, 8], [4, 12]]) {
                jest.advanceTimersByTime(seconds * 1000);
                window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
                expect([saved().pageRequestId, saved().secondsOnPage]).toEqual([id, total]);
                Cookies.remove('SPOInsightsLastPageStats');
                const handoff = () => Cookies.set('SPOInsightsLastPageStats', JSON.stringify({ pageRequestId: 'intervening-' + total,
                    secondsOnPage: seconds, url: encodeURI(OTHER_PAGE_URL) }));
                if (total !== 8) handoff();
                jest.setSystemTime(Date.now() + 60000); // No wall-clock gap may count, even if TimeMe was not paused.
                window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
                if (total === 8) handoff(); // Another renderer can finish its pagehide after our pageshow.
            }
            expect(ai.trackPageView).toHaveBeenCalledTimes(1);
            expect(eventsNamed(ai, 'PAGE_EXIT').map(e => [e.properties.pageRequestId, e.properties.activeTime, e.properties.url]))
                .toEqual([['intervening-3', 3, OTHER_PAGE_URL], ['intervening-8', 5, OTHER_PAGE_URL], ['intervening-12', 4, OTHER_PAGE_URL]]);
        });

        test('return to the last accepted page resumes its ID without counting an intervening unaccepted visit', () => {
            const ai = startModern();
            jest.advanceTimersByTime(2500);
            navigate(destination);
            mousedownOnLink('Abandoned');
            jest.advanceTimersByTime(400);
            navigate(PAGE_URL + '#back');
            jest.advanceTimersByTime(2000);
            report();
            window.dispatchEvent(new Event('pagehide'));
            expect(ai.trackPageView).toHaveBeenCalledTimes(1);
            expect(eventsNamed(ai, 'LinkClick')).toHaveLength(0);
            expect([saved().pageRequestId, saved().secondsOnPage]).toEqual([pageRequestIdOf(ai, 0), 4.5]);
        });

        test('only the final registered visit transfers its time; superseded queries, detours and registration gaps do not', () => {
            const ai = startModern();
            jest.advanceTimersByTime(2500);
            navigate(destination);
            jest.advanceTimersByTime(300);
            navigate(destination + '2');
            jest.advanceTimersByTime(300);
            navigate(destination);
            jest.advanceTimersByTime(300);
            sitesTrackedInPage(); // Still the same URL, but the extension unregistered its site.
            mousedownOnLink('Outside tracking');
            jest.advanceTimersByTime(4000);
            sitesTrackedInPage(SITE_URL);
            mousedownOnLink('Registered again');
            jest.advanceTimersByTime(800);
            navigate(destination + '#results');
            jest.advanceTimersByTime(700);
            report();
            jest.advanceTimersByTime(200);
            window.dispatchEvent(new Event('pagehide'));
            expect(eventsNamed(ai, 'PAGE_EXIT').map(e => e.properties.activeTime)).toEqual([2.5]);
            expect(saved().secondsOnPage).toBe(1.7);
            expect(eventsNamed(ai, 'LinkClick').map(e => e.properties.linkText)).toEqual(['Registered again']);
        });
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

        window.modernPageNav(OTHER_SITE_URL, 'Other', OTHER_SITE_URL);
        mousedownOnLink('Link');
        expect(eventsNamed(mockAiInstances[0], 'LinkClick').map(c => c.properties.pageRequestId)).toEqual([pageRequestIdOf(mockAiInstances[0], 1)]);
        expect(noPageRequestIdErrors(errorSpy)).toEqual([]);
    });

    test('leaves tracking to an earlier version of the script that is already tracking the page', async () => {
        (window as any).modernPageNav = jest.fn();
        loadTrackerCopy();
        await settle();

        expect(mockAiInstances.length).toBe(0);
    });

    test('a right-click on a link only opens the context menu, so it is not a click; a middle-click opens the link, so it is', async () => {
        loadTrackerCopy();
        await settle();
        const ai = mockAiInstances[0];

        mousedownOnLink('Right-clicked', 'https://contoso.sharepoint.com/sites/test/SitePages/Right.aspx', 2);
        mousedownOnLink('Back button', 'https://contoso.sharepoint.com/sites/test/SitePages/Back.aspx', 3);
        expect(eventsNamed(ai, 'LinkClick').length).toBe(0);

        mousedownOnLink('Middle-clicked', 'https://contoso.sharepoint.com/sites/test/SitePages/Middle.aspx', 1);
        mousedownOnLink('Left-clicked', 'https://contoso.sharepoint.com/sites/test/SitePages/Left.aspx', 0);
        expect(eventsNamed(ai, 'LinkClick').map((e: any) => e.properties.linkText)).toEqual(['Middle-clicked', 'Left-clicked']);
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

    // SharePoint navigates between site collections without reloading the page, so the tracker keeps running when it navigates to
    // one the installer didn't set up for tracking. Nothing on such a page may be collected.
    describe('only on tracked sites', () => {

        const sentAnything = (ai: any) => JSON.stringify([ai.trackPageView.mock.calls, ai.trackEvent.mock.calls]);
        const savedExit = () => JSON.parse(Cookies.get('SPOInsightsLastPageStats')!);

        test('root-only registration cannot bootstrap, queue clicks or accept a report in a managed-path collection', async () => {
            const root = new URL(SITE_URL).origin;
            sitesTrackedInPage(root);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];
            mousedownOnLink('Off-site click');
            checkSite();
            expect(window.modernPageNav(root, 'Root', root)).toBe(false);
            expect(window.modernPageNav(SITE_URL, 'Unregistered', SITE_URL)).toBe(false);
            window.dispatchEvent(new Event('pagehide'));
            expect(ai.trackPageView).not.toHaveBeenCalled();
            expect(ai.trackEvent).not.toHaveBeenCalled();
            expect(Cookies.get('SPOInsightsLastPageStats')).toBeUndefined();

            // The older extension has only siteUrls and a positional navigation report.
            window._o365AnalyticsInfo!.siteUrls.push(SITE_URL);
            expect(window.modernPageNav(root, 'Root', root)).toBe(false);
            mousedownOnLink('Registered click');
            expect(window.modernPageNav(SITE_URL, 'Test', SITE_URL)).toBe(true);
            expect(ai.trackPageView.mock.calls.map(c => c[0].uri)).toEqual([PAGE_URL]);
            expect(eventsNamed(ai, 'LinkClick').map(c => c.properties.linkText)).toEqual(['Registered click']);
        });

        test('a direct registered document still reports its title and load time if the legacy context belongs to another web', async () => {
            sitesTrackedInPage(SITE_URL);
            document.title = 'Loaded page';
            Object.assign(window._spPageContextInfo, { siteAbsoluteUrl: OTHER_SITE_URL, webAbsoluteUrl: OTHER_SITE_URL,
                webTitle: 'Earlier web', listTitle: 'Earlier list', pageItemId: 10 });
            loadTrackerCopy();
            await settle();
            const view = mockAiInstances[0].trackPageView.mock.calls[0][0];
            expect(view).toMatchObject({ name: 'Loaded page', uri: PAGE_URL,
                properties: { pageTitle: 'Loaded page', siteUrl: SITE_URL, pageLoad: 1500 } });
            expect(view.properties).not.toHaveProperty('webUrl');
            expect(view.properties).not.toHaveProperty('webTitle');
        });

        test.each(['mousedown and click', 'click only'])('source and destination clicks keep their own page IDs across the SPFx reporting gap: %s', async input => {
            const search = SITE_URL + '/_layouts/15/search.aspx/siteall?q=';
            setUrl(search + 'first');
            navigationEntries[0].name = document.URL;
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];
            const sourceId = pageRequestIdOf(ai, 0);

            setUrl(search + 'first#top');
            document.body.innerHTML = '<a>Next search</a><a>Open result</a>';
            const [go, result] = Array.from(document.querySelectorAll('a'));
            go.href = search + 'second';
            go.addEventListener('click', e => { e.preventDefault(); setUrl(go.href); });
            if (input === 'mousedown and click') go.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
            go.click(); // The router changes document.URL before the click bubbles to document.

            result.href = SITE_URL + '/Shared Documents/Result.pdf';
            result.addEventListener('click', e => e.preventDefault());
            const beforeClick = Date.now();
            result.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
            result.click();
            const afterClick = Date.now();

            window.modernPageNav(OTHER_SITE_URL, 'Other', OTHER_SITE_URL); // Stale callback cannot accept this view.
            expect(ai.trackPageView).toHaveBeenCalledTimes(1);
            expect(eventsNamed(ai, 'LinkClick').map(c => [c.properties.linkText, c.properties.pageRequestId]))
                .toEqual([['Next search', sourceId]]);

            await settle();
            setUrl(search + 'second#results');
            window.modernPageNav(SITE_URL, 'Test', SITE_URL);
            window.modernPageNav(SITE_URL, 'Test', SITE_URL);
            const destinationId = pageRequestIdOf(ai, 1);
            expect(destinationId).not.toBe(sourceId);
            const clicks = eventsNamed(ai, 'LinkClick');
            expect(clicks.map(c => [c.properties.linkText, c.properties.pageRequestId]))
                .toEqual([['Next search', sourceId], ['Open result', destinationId]]);
            expect(Date.parse(clicks[1].properties.timeStamp)).toBeGreaterThanOrEqual(beforeClick);
            expect(Date.parse(clicks[1].properties.timeStamp)).toBeLessThanOrEqual(afterClick);
            expect(eventsNamed(ai, 'PAGE_EXIT').map(e => e.properties.pageRequestId)).toEqual([sourceId]);
        });

        test.each(['different query', 'untracked site', 'already tracked page', 'history change between polls'])('does not replay an abandoned destination click on a later visit (%s)', async via => {
            if (via === 'history change between polls') historyChanges = new EventTarget();
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];
            const destination = PAGE_URL + '?q=one';
            setUrl(destination);
            mousedownOnLink('Abandoned visit');

            if (via === 'untracked site') {
                navigateToUntrackedSite();
                mousedownOnLink('Not tracked');
            } else if (via === 'already tracked page') {
                setUrl(PAGE_URL);
                window.modernPageNav(SITE_URL, 'Test', SITE_URL);
            } else {
                setUrl(PAGE_URL + '?q=two');
                if (historyChanges) historyChanges.dispatchEvent(new Event('currententrychange'));
                else checkSite();
            }

            setUrl(destination);
            sitesTrackedInPage(SITE_URL);
            mousedownOnLink('Current visit');
            window.modernPageNav(SITE_URL, 'Test', SITE_URL);
            expect(eventsNamed(ai, 'LinkClick').map(c => [c.properties.linkText, c.properties.pageRequestId]))
                .toEqual([['Current visit', pageRequestIdOf(ai, 1)]]);
        });

        test('leaving before SPFx reports the destination saves the accepted exit and never replays the abandoned click', async () => {
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];
            setUrl(PAGE_URL + '?q=next');
            mousedownOnLink('No destination view');
            window.dispatchEvent(new Event('pagehide'));
            expect(decodeURI(savedExit().url)).toBe(PAGE_URL);
            expect(savedExit().pageRequestId).toBe(pageRequestIdOf(ai, 0));
            expect(eventsNamed(ai, 'LinkClick')).toHaveLength(0);

            // A browser can restore this document from its back/forward cache, including its JS heap and delayed callbacks.
            mousedownOnLink('After restore');
            window.modernPageNav(SITE_URL, 'Test', SITE_URL);
            expect(eventsNamed(ai, 'LinkClick').map(c => [c.properties.linkText, c.properties.pageRequestId]))
                .toEqual([['After restore', pageRequestIdOf(ai, 1)]]);
        });

        test('tracked site, then one that isn\'t: its clicks aren\'t sent, and the time saved as the browser leaves is the tracked page\'s', async () => {
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];

            mockSecondsOnPage = 30;
            mousedownOnLink('Tracked link');
            expect(eventsNamed(ai, 'LinkClick').length).toBe(1);

            navigateToUntrackedSite();
            mockSecondsOnPage = 90;
            mousedownOnLink('Untracked link', UNTRACKED_SITE_URL + '/Shared Documents/Plan.docx');
            document.querySelectorAll('a')[1].dispatchEvent(new MouseEvent('click', { bubbles: true }));
            window.dispatchEvent(new Event('pagehide'));

            expect(eventsNamed(ai, 'LinkClick').length).toBe(1);
            expect(ai.trackPageView).toHaveBeenCalledTimes(1);
            expect(sentAnything(ai)).not.toContain('untracked');
            expect(savedExit().pageRequestId).toBe(pageRequestIdOf(ai, 0));
            expect(decodeURI(savedExit().url)).toBe(PAGE_URL);
            expect(savedExit().secondsOnPage).toBe(30);        // Until it was last seen on the tracked site
        });

        test('back on a tracked site, tracking resumes, and the time away from the tracked sites counts for no page', async () => {
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];

            mockSecondsOnPage = 30;
            mousedownOnLink('To a site that is not tracked', UNTRACKED_PAGE_URL);
            navigateToUntrackedSite();
            mockSecondsOnPage = 31;
            checkSite();
            mockSecondsOnPage = 100;

            // SharePoint navigates on to another tracked site, whose extension registers it, then reports its page
            setUrl(OTHER_PAGE_URL);
            sitesTrackedInPage(OTHER_SITE_URL);
            (window as any).modernPageNav(OTHER_SITE_URL, 'Other', OTHER_SITE_URL);

            expect(ai.trackPageView).toHaveBeenCalledTimes(2);
            expect(ai.trackPageView.mock.calls[1][0].uri).toBe(OTHER_PAGE_URL);
            const exits = eventsNamed(ai, 'PAGE_EXIT');
            expect(exits.length).toBe(1);
            expect(exits[0].properties.pageRequestId).toBe(pageRequestIdOf(ai, 0));
            expect(exits[0].properties.url).toBe(PAGE_URL);
            expect(exits[0].properties.activeTime).toBe(30);

            mousedownOnLink('Link on the tracked site', OTHER_SITE_URL + '/SitePages/Events.aspx');
            const clicks = eventsNamed(ai, 'LinkClick');
            expect(clicks.length).toBe(2);
            expect(clicks[1].properties.pageRequestId).toBe(pageRequestIdOf(ai, 1));
        });

        test('back on the page it left, which the extension has already reported: clicks are sent for it, and its time leaves out the time away', async () => {
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];

            mockSecondsOnPage = 30;
            checkSite();
            navigateToUntrackedSite();
            mockSecondsOnPage = 31;
            checkSite();
            mockSecondsOnPage = 95;

            setUrl(PAGE_URL);
            sitesTrackedInPage(SITE_URL);
            mousedownOnLink('Link');
            mockSecondsOnPage = 105;
            window.dispatchEvent(new Event('pagehide'));

            const clicks = eventsNamed(ai, 'LinkClick');
            expect(clicks.length).toBe(1);
            expect(clicks[0].properties.pageRequestId).toBe(pageRequestIdOf(ai, 0));
            expect(savedExit().pageRequestId).toBe(pageRequestIdOf(ai, 0));
            expect(savedExit().secondsOnPage).toBe(40);        // 30 before it left, and 10 since it came back
        });

        test('a navigation the extension reports after SharePoint has moved on to a site that isn\'t tracked isn\'t tracked', async () => {
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];

            // The extension reports a navigation a couple of seconds after it. By then SharePoint can have disposed of it, and
            // its page context can describe either site
            navigateToUntrackedSite();
            (window as any).modernPageNav(SITE_URL, 'Test', SITE_URL);
            (window as any).modernPageNav(UNTRACKED_SITE_URL, 'Untracked', UNTRACKED_SITE_URL);

            expect(ai.trackPageView).toHaveBeenCalledTimes(1);
            expect(eventsNamed(ai, 'PAGE_EXIT').length).toBe(0);
            expect(sentAnything(ai)).not.toContain('untracked');
        });

        test('a navigation reported for a site SharePoint has since left isn\'t tracked: the next site\'s extension reports its own page', async () => {
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];

            setUrl(OTHER_PAGE_URL);
            sitesTrackedInPage(OTHER_SITE_URL);
            (window as any).modernPageNav(SITE_URL, 'Test', SITE_URL);
            expect(ai.trackPageView).toHaveBeenCalledTimes(1);

            (window as any).modernPageNav(OTHER_SITE_URL, 'Other', OTHER_SITE_URL);
            expect(ai.trackPageView).toHaveBeenCalledTimes(2);
            expect(ai.trackPageView.mock.calls[1][0].properties.webUrl).toBe(OTHER_SITE_URL);
        });

        test('loaded after SharePoint moved on to a site that isn\'t tracked: tracks nothing there, then the next tracked page', async () => {
            const errorSpy = jest.spyOn(console, 'error').mockImplementation();
            sitesTrackedInPage(SITE_URL);
            setUrl(UNTRACKED_PAGE_URL);
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];

            expect(ai.trackPageView).not.toHaveBeenCalled();
            expect(typeof (window as any).modernPageNav).toBe('function');
            mousedownOnLink('Untracked link', UNTRACKED_SITE_URL + '/SitePages/Other.aspx');
            window.dispatchEvent(new Event('pagehide'));
            expect(ai.trackEvent).not.toHaveBeenCalled();
            expect(Cookies.get('SPOInsightsLastPageStats')).toBeUndefined();

            setUrl(OTHER_PAGE_URL);
            sitesTrackedInPage(OTHER_SITE_URL);
            (window as any).modernPageNav(OTHER_SITE_URL, 'Other', OTHER_SITE_URL);

            expect(ai.trackPageView).toHaveBeenCalledTimes(1);
            expect(ai.trackPageView.mock.calls[0][0].uri).toBe(OTHER_PAGE_URL);
            expect(ai.trackPageView.mock.calls[0][0].properties.pageLoad).toBe(0);
            expect(errorSpy).not.toHaveBeenCalled();
        });

        test('classic pages have no SPFx extension: every page is tracked, as before', async () => {
            const url = 'https://contoso.sharepoint.com/sites/test/_layouts/15/settings.aspx';
            setUrl(url);
            navigationEntries = [{ name: url, startTime: 0, loadEventEnd: 700 }];
            loadTrackerCopy();
            await settle();
            const ai = mockAiInstances[0];

            mousedownOnLink('People and groups', 'https://contoso.sharepoint.com/sites/test/_layouts/15/user.aspx');
            window.dispatchEvent(new Event('pagehide'));

            expect(ai.trackPageView).toHaveBeenCalledTimes(1);
            expect(eventsNamed(ai, 'LinkClick').length).toBe(1);
            expect(decodeURI(savedExit().url)).toBe(url);
            expect(savedExit().secondsOnPage).toBe(12);
            expect(window.setInterval).not.toHaveBeenCalled();
        });

        test('a second copy, loaded by the next tracked site\'s extension, still leaves tracking to the first', async () => {
            sitesTrackedInPage(SITE_URL);
            loadTrackerCopy();
            await settle();
            const listenersAddedByFirstCopy = listeners.length;
            expect(intervals.length).toBe(1);

            setUrl(OTHER_PAGE_URL);
            sitesTrackedInPage(OTHER_SITE_URL);
            loadTrackerCopy();
            await settle();

            expect(mockAiInstances.length).toBe(1);
            expect(listeners.length).toBe(listenersAddedByFirstCopy);
            expect(intervals.length).toBe(1);
            mousedownOnLink('Link', OTHER_SITE_URL + '/SitePages/Events.aspx');
            window.modernPageNav(OTHER_SITE_URL, 'Other', OTHER_SITE_URL);
            expect(eventsNamed(mockAiInstances[0], 'LinkClick').map(c => c.properties.pageRequestId)).toEqual([pageRequestIdOf(mockAiInstances[0], 1)]);
        });
    });
});
