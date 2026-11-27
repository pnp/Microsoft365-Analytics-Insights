/**
 * @jest-environment jsdom
 * @jest-environment-options {"url":"https://contoso.example/sites/one/Pages/Earlier.aspx","referrer":"https://contoso.example/sites/unregistered/Referrer.aspx"}
 */
import { AppInsightsWrapper } from '../src/AppInsightsWrapper';
import { SetLastPageStatsVal } from '../src/Cookies';
import { InMemoryPageStateManager } from '../src/PageProps/PageState';
import { SpoPagePropertyManager } from '../src/PageProps/SpoImplementation/SpoPagePropertyManager';
import { WebPageDataService } from '../src/PageProps/SpoImplementation/WebPageDataService';
import { PageViewTracker } from '../src/PageViewTracker';
import type { ModernPageContext } from '../src/TrackedSites';
import { createTrackerTelemetry } from '../src/TrackerTelemetry';

jest.mock('timeme.js', () => ({ __esModule: true, default: { getTimeOnCurrentPageInMilliseconds: () => 1000 } }));

test('first and later soft views, clicks, searches and list metadata use only the verified destination in the serialized SDK batch', async () => {
    jest.useFakeTimers({ doNotFake: ['performance'] });
    const previous = location.href, site = location.origin + '/sites/one';
    const web = site + '/sub', destination = web + '/Pages/Current.aspx?q=contoso';
    let snapshot: ModernPageContext | undefined;
    const stale = { siteAbsoluteUrl: site, webAbsoluteUrl: web, userLoginName: 'reader@contoso.example',
        webTitle: 'Earlier web title', listTitle: 'Earlier list', pageItemId: 10 };
    const originalFetch = window.fetch;
    const batches: any[] = [];
    // Keep the shipping analytics, context, serializer and sender. Replace only browser I/O.
    jest.spyOn(XMLHttpRequest.prototype, 'send').mockImplementation(body => {
        batches.push(...JSON.parse(String(body)));
    });
    const fetch = jest.fn(async (url: string) => {
        const data = url.endsWith('/properties') ? { Marker: 'Current metadata' } :
            url.includes('likedByInformation') ? { likedBy: { results: [] } } : { results: [] };
        return { ok: true, text: async () => JSON.stringify({ d: data }) };
    });
    window.fetch = fetch as unknown as typeof window.fetch;
    try {
        document.title = 'Earlier document title';
        window._spPageContextInfo = stale;
        window._o365AnalyticsInfo = { siteUrls: [site], visits: { currentPageContext: () => snapshot } };
        history.pushState(null, '', destination);
        SetLastPageStatsVal({ url: encodeURI(site + '/Pages/Accepted.aspx'), secondsOnPage: 12, pageRequestId: 'prior-view' });
        const sdk = createTrackerTelemetry('InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://capture.invalid', stale.userLoginName);
        const ai = new AppInsightsWrapper(sdk, 'synthetic-session');
        const tracker = new PageViewTracker(ai, stale,
            new SpoPagePropertyManager(new InMemoryPageStateManager(), new WebPageDataService(ai)));
        ai.trackClick({ linkText: 'Current link', altText: '', classNames: null, href: site });
        tracker.trackCurrentPageViewAndLastPageExit(destination, stale.listTitle, stale.pageItemId, 0);
        // Same reader, registered collection and web, but not the query being visited.
        snapshot = { url: destination + '-earlier', siteUrl: site, webUrl: web, webTitle: stale.webTitle,
            listItemId: stale.pageItemId, listTitle: stale.listTitle };
        expect(tracker.handleModernPageNav(web, stale.webTitle, site, destination)).toBe(false);
        expect(ai._pageRequestId).toBeNull();
        expect(fetch).not.toHaveBeenCalled();

        snapshot = { binding: 'request', url: destination, webUrl: web, siteUrl: site, webTitle: 'Current web', listTitle: 'Current pages', listItemId: 20 };
        expect(tracker.handleModernPageNav(web, stale.webTitle, site, destination, stale.listTitle, stale.pageItemId)).toBe(true);
        await jest.advanceTimersByTimeAsync(20000);
        expect(fetch).toHaveBeenCalledTimes(3);
        for (const [url] of fetch.mock.calls) expect(url).toContain(web + "/_api/web/lists/getbytitle('Current%20pages')/items(20)/");

        const views = batches.filter(e => e.data.baseType === 'PageviewData');
        expect(views).toHaveLength(1);
        expect(views[0].tags['ai.user.authUserId']).toBe(stale.userLoginName);
        const view = views[0].data.baseData;
        expect(view).toMatchObject({ url: destination, name: new URL(destination).pathname,
            properties: { webUrl: web, siteUrl: site, webTitle: 'Current web', pageRequestId: ai._pageRequestId } });
        expect(view.properties).not.toHaveProperty('pageTitle');
        expect(view.properties).not.toHaveProperty('refUri');
        const events = batches.filter(e => e.data.baseType === 'EventData').map(e => e.data.baseData);
        expect(events.map(e => e.name).sort()).toEqual(['LinkClick', 'PAGE_EXIT', 'PageMetadataUpdate', 'UserSearch']);
        const props = (name: string) => events.find(e => e.name === name).properties;
        expect(props('PAGE_EXIT')).toMatchObject({ url: site + '/Pages/Accepted.aspx', pageRequestId: 'prior-view' });
        expect(props('LinkClick')).toMatchObject({ linkText: 'Current link', pageRequestId: ai._pageRequestId });
        expect(props('UserSearch')).toMatchObject({ userSearch: 'contoso', pageRequestId: ai._pageRequestId });
        expect(props('PageMetadataUpdate').url).toBe(destination);
        expect(JSON.parse(props('PageMetadataUpdate').props)).toMatchObject({ Marker: 'Current metadata' });
        expect(JSON.stringify(batches)).not.toMatch(/Earlier|Referrer\.aspx/);

        const firstId = ai._pageRequestId;
        const next = web + '/Pages/Next.aspx?q=next';
        history.pushState(null, '', next);
        ai.trackClick({ linkText: 'Next link', altText: '', classNames: null, href: site });
        // A timer cannot substitute its positional fields for a missing or stale visit snapshot.
        expect(tracker.handleModernPageNav(web, stale.webTitle, site, next, stale.listTitle, 10)).toBe(false);
        snapshot = undefined;
        expect(tracker.handleModernPageNav(web, stale.webTitle, site, next, stale.listTitle, 10)).toBe(false);
        expect(ai._pageRequestId).toBe(firstId);
        expect(fetch).toHaveBeenCalledTimes(3);
        snapshot = { binding: 'request', url: next, webUrl: web, siteUrl: site, webTitle: 'Next web', listTitle: 'Next pages', listItemId: 30 };
        expect(tracker.handleModernPageNav(web, stale.webTitle, site, next, stale.listTitle, 10)).toBe(true);
        await jest.advanceTimersByTimeAsync(20000);
        expect(ai._pageRequestId).not.toBe(firstId);
        expect(fetch).toHaveBeenCalledTimes(6);
        for (const [url] of fetch.mock.calls.slice(3)) expect(url).toContain(web + "/_api/web/lists/getbytitle('Next%20pages')/items(30)/");
        const nextViews = batches.filter(e => e.data.baseType === 'PageviewData' && e.data.baseData.url === next);
        expect(nextViews).toHaveLength(1);
        expect(nextViews[0].data.baseData).toMatchObject({ name: new URL(next).pathname, properties: { webTitle: 'Next web', pageLoad: '0' } });
        expect(nextViews[0].data.baseData.properties).not.toHaveProperty('pageTitle');
        const nextEvents = batches.filter(e => e.data.baseType === 'EventData').map(e => e.data.baseData);
        expect(nextEvents.filter(e => e.name === 'PAGE_EXIT').map(e => e.properties.pageRequestId)).toEqual(['prior-view', firstId]);
        expect(nextEvents.find(e => e.properties.linkText === 'Next link').properties.pageRequestId).toBe(ai._pageRequestId);
        expect(nextEvents.find(e => e.properties.userSearch === 'next').properties.pageRequestId).toBe(ai._pageRequestId);
        expect(nextEvents.filter(e => e.name === 'PageMetadataUpdate').map(e => e.properties.url)).toEqual([destination, next]);
        expect(JSON.stringify(batches)).not.toMatch(/Earlier|Referrer\.aspx/);
    } finally {
        window._o365AnalyticsInfo = undefined;
        history.replaceState(null, '', previous);
        window.fetch = originalFetch;
        jest.restoreAllMocks();
        jest.useRealTimers();
    }
});
