jest.mock('@microsoft/sp-application-base', () => ({ BaseApplicationCustomizer: class {} }));
jest.mock('@microsoft/sp-core-library', () => ({ Guid: { newGuid: () => 'synthetic' } }));
jest.mock('@microsoft/sp-loader', () => ({ SPComponentLoader: { loadScript: jest.fn() } }));
jest.mock('./Logger', () => ({ Logger: { verbose: jest.fn(), info: jest.fn(), warn: jest.fn(), error: jest.fn() } }));

import { SPComponentLoader } from '@microsoft/sp-loader';
import AiTrackerModernApplicationCustomizer from './AiTrackerModernApplicationCustomizer';

class TestCustomizer extends AiTrackerModernApplicationCustomizer {
  public close(): void { this.onDispose(); }
}

describe('delayed SharePoint navigation reports', () => {
  const a = window.location.origin + '/sites/one';
  const b = window.location.origin + '/sites/one-more';
  const home = '/SitePages/Home.aspx';
  const form = '/Lists/Tasks/DispForm.aspx';
  const listeners = new Map<TestCustomizer, () => void>();
  const navigatedEvent = {
    add: (owner: TestCustomizer, callback: () => void): void => { listeners.set(owner, callback.bind(owner)); },
    remove: (owner: TestCustomizer): void => { listeners.delete(owner); }
  };
  const nav = jest.fn();

  function pageContext(site: string, title: string, id = 1): {
    site: { absoluteUrl: string; serverRequestPath?: string }; web: { absoluteUrl: string; title: string }; list: { title: string };
    listItem: { id: number }; legacyPageContext: { webTitle: string; siteAbsoluteUrl: string };
  } {
    return {
      site: { absoluteUrl: site, serverRequestPath: location.pathname + location.search }, web: { absoluteUrl: site, title }, list: { title: 'Pages' },
      listItem: { id }, legacyPageContext: { webTitle: title, siteAbsoluteUrl: site }
    };
  }

  async function install(context: ReturnType<typeof pageContext>, event = navigatedEvent): Promise<TestCustomizer> {
    const instance = new TestCustomizer();
    Object.assign(instance, {
      properties: { appInsightsConnectionStringHash: btoa('synthetic') },
      context: { pageContext: context, application: { navigatedEvent: event } }
    });
    await instance.onInit();
    return instance;
  }

  function show(url: string): void { window.history.pushState({}, '', url); }
  function fire(owner: TestCustomizer): void {
    const listener = listeners.get(owner);
    if (!listener) throw new Error('Expected a registered navigation listener');
    listener();
  }

  beforeEach(() => {
    jest.useFakeTimers();
    jest.clearAllMocks();
    // A running copy before 1.6.0 has only this void callback: no version marker or acceptance result.
    Object.assign(window, { _o365AnalyticsInfo: undefined, modernPageNav: nav });
    show(a + home);
  });
  afterEach(() => {
    listeners.clear();
    delete (window as { modernPageNav?: unknown }).modernPageNav;
    delete (window as { navigation?: EventTarget }).navigation;
    jest.useRealTimers();
  });

  it("lets the destination report even if the old site's timer fires before the new site registers", async () => {
    const first = await install(pageContext(a, 'One'));
    fire(first);
    show(b + home);
    jest.advanceTimersByTime(2000);
    const second = await install(pageContext(b, 'Two'));
    fire(second);
    // A not-yet-disposed listener may receive the event too, with its old pageContext.
    fire(first);
    jest.advanceTimersByTime(7000);

    expect(nav.mock.calls).toEqual([[b, 'Two', b, 'Pages', 1]]);
    expect(window._spPageContextInfo.webTitle).toBe('Two');
    expect(SPComponentLoader.loadScript).not.toHaveBeenCalled();
  });

  it('supersedes an earlier timer on the same site without postponing the first event for the destination', async () => {
    const context = pageContext(a, 'One');
    const owner = await install(context);
    show(a + home + '?q=one');
    fire(owner);
    jest.advanceTimersByTime(500);
    show(a + home + '?q=two');
    Object.assign(context, pageContext(a, 'Two', 2));
    context.site.absoluteUrl = a.toUpperCase() + '/';
    fire(owner);
    jest.advanceTimersByTime(1500);
    expect(nav).not.toHaveBeenCalled();
    show(a + home + '?q=two#heading');
    fire(owner);
    jest.advanceTimersByTime(500);
    expect(nav).toHaveBeenCalledTimes(1);
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav.mock.calls).toEqual([[a, 'Two', a, 'Pages', 2]]);
  });

  it('reports a return to the last reported URL even when the intervening visit never settled', async () => {
    const owner = await install(pageContext(a, 'One'));
    fire(owner);
    jest.advanceTimersByTime(2000);
    show(a + '/SitePages/Brief.aspx');
    fire(owner);
    jest.advanceTimersByTime(500);
    show(a + home);
    fire(owner);
    jest.advanceTimersByTime(1500);
    expect(nav).toHaveBeenCalledTimes(1);
    jest.advanceTimersByTime(500);
    expect(nav).toHaveBeenCalledTimes(2);
  });

  it('does not let the root-site listener borrow a managed-path destination or its updated context', async () => {
    show(home);
    const context = pageContext(window.location.origin, 'Root');
    const root = await install(context);
    fire(root);
    jest.advanceTimersByTime(2000);
    expect(nav).toHaveBeenCalledTimes(1);
    nav.mockClear();
    show(b + home);
    fire(root);
    jest.advanceTimersByTime(2000);
    expect(nav).not.toHaveBeenCalled();
    const destination = await install(pageContext(b, 'Destination'));
    fire(root);
    Object.assign(context, pageContext(b, 'Not the destination owner'));
    fire(root);
    jest.advanceTimersByTime(2000);
    expect(nav).not.toHaveBeenCalled();
    fire(destination);
    jest.advanceTimersByTime(2000);
    expect(nav.mock.calls).toEqual([[b, 'Destination', b, 'Pages', 1]]);
  });

  it('does not let a disposed owner report for a replacement on the same URL', async () => {
    const first = await install(pageContext(a, 'First'));
    fire(first);
    jest.advanceTimersByTime(500);
    first.close();
    const second = await install(pageContext(a, 'Replacement'));
    const duplicate = await install(pageContext(a, 'Duplicate'));
    expect(listeners.has(duplicate)).toBe(false);
    duplicate.close();
    fire(second);
    jest.advanceTimersByTime(1500);
    expect(nav).not.toHaveBeenCalled();
    jest.advanceTimersByTime(500);
    expect(nav.mock.calls).toEqual([[a, 'Replacement', a, 'Pages', 1]]);
    second.close();
    fire(await install(pageContext(a, 'Already reported')));
    jest.advanceTimersByTime(2000);
    expect(nav).toHaveBeenCalledTimes(1);
  });

  it('makes only a page-bound context available to a late tracker and retries a rejected report', async () => {
    const context = pageContext(a, 'Earlier web', 10);
    context.site.serverRequestPath = '/sites/one/SitePages/Earlier.aspx';
    const owner = await install(context);
    const current = (): unknown => window._o365AnalyticsInfo.visits?.currentPageContext();
    expect(current()).toBeUndefined();
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav).not.toHaveBeenCalled();
    expect(current()).toBeUndefined();

    Object.assign(context, pageContext(a, 'Current web', 20));
    context.site.serverRequestPath = location.pathname;
    nav.mockReturnValueOnce(false);
    fire(owner);
    expect(current()).toEqual({ url: a + home, binding: 'request', webUrl: a, webTitle: 'Current web', siteUrl: a, listTitle: 'Pages', listItemId: 20 });
    jest.advanceTimersByTime(2000);
    expect(nav).toHaveBeenCalledTimes(1);
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav).toHaveBeenCalledTimes(2);
    fire(owner); // The previous call returned void, as an older standalone does.
    jest.advanceTimersByTime(2000);
    expect(nav).toHaveBeenCalledTimes(2);
    owner.close();
    expect(current()).toBeUndefined();
  });

  it('binds an init snapshot to an exact request, not a later query at the same path', async () => {
    const context = pageContext(a, 'Bound at init');
    context.site.serverRequestPath = location.pathname.toUpperCase();
    await install(context);
    expect(window._o365AnalyticsInfo.visits?.currentPageContext()?.webTitle).toBe('Bound at init');
    show(a + home + '?q=new');
    expect(window._o365AnalyticsInfo.visits?.currentPageContext()).toBeUndefined();
  });

  it.each(['path only', 'matching query but different item', 'unverified search query'])('keeps an unbound sticky replay retryable: %s', async evidence => {
    // Navigator raises a sticky application.navigatedEvent. Use the pinned register/raise implementation, not an add-only mock.
    const name = 'application.navigatedEvent';
    const manager = jest.requireActual('@microsoft/sp-core-library/lib-commonjs/events/SPEventManager').default.instance;
    const EventType = jest.requireActual('@microsoft/sp-core-library/lib-commonjs/events/SPEvent').default;
    const event = new EventType(name);
    manager.raiseStickyEvent(name, {});
    try {
      show(a + (evidence === 'unverified search query' ? home + '?q=current' : form + '?ID=2'));
      const context = pageContext(a, 'Earlier', 1);
      context.site.serverRequestPath = location.pathname + (evidence === 'matching query but different item' ? location.search : '');
      const owner = await install(context, event);
      expect(window._o365AnalyticsInfo.visits?.currentPageContext()).toBeUndefined();
      jest.advanceTimersByTime(2000);
      expect(nav).not.toHaveBeenCalled();

      Object.assign(context, pageContext(a, 'Current', 2));
      context.site.serverRequestPath = location.pathname;
      manager.raiseStickyEvent(name, {});
      jest.advanceTimersByTime(2000);
      expect(nav.mock.calls).toEqual([[a, 'Current', a, 'Pages', 2]]);
      owner.close();
    } finally { manager.removeEvent(name); }
  });

  it.each(['exact request', 'matching item query'])('delivers an independently matched initial request even when subscription only replays: %s', async evidence => {
    if (evidence === 'matching item query') show(a + form + '?ID=2');
    const context = pageContext(a, 'Initial', 2);
    context.site.serverRequestPath = location.pathname;
    const replay = { ...navigatedEvent, add: (o: TestCustomizer, h: () => void): void => {
      navigatedEvent.add(o, h);
      h.call(o);
    } };
    await install(context, replay);
    jest.advanceTimersByTime(2000);
    expect(nav.mock.calls).toEqual([[a, 'Initial', a, 'Pages', 2]]);
  });

  it('an event without a request path reports the visit, not the mutable web title or list item', async () => {
    const context = pageContext(a, 'Unbound', 10);
    context.site.serverRequestPath = undefined;
    const owner = await install(context);
    expect(window._o365AnalyticsInfo.visits?.currentPageContext()).toBeUndefined();
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav.mock.calls).toEqual([[a, '', a, undefined, undefined]]);
    expect(window._o365AnalyticsInfo.visits?.currentPageContext()).toEqual({ url: a + home, siteUrl: a, binding: 'event' });
  });

  it('does not consume a visit when the list URL or requested list ID contradicts its item', async () => {
    const requested = '00000000-0000-0000-0000-000000000001';
    const context = pageContext(a, 'Candidate', 2);
    const owner = await install(context);
    show(a + form + '?ID=2');
    context.site.serverRequestPath = undefined;
    Object.assign(context.list, { serverRelativeUrl: '/sites/one/Lists/Other' });
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav).not.toHaveBeenCalled();
    show(a + form + '?ID=2&ListId=' + requested);
    Object.assign(context.list, { id: '00000000-0000-0000-0000-000000000002' });
    context.site.serverRequestPath = location.pathname;
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav).not.toHaveBeenCalled();
    Object.assign(context.list, { id: requested, serverRelativeUrl: '/sites/one/Lists/Tasks' });
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav.mock.calls).toEqual([[a, 'Candidate', a, 'Pages', 2]]);
  });

  it('reports the verified welcome URL without guessing its backing Site Pages item', async () => {
    show(a + '/');
    const context = pageContext(a, 'Home web', 10);
    Object.assign(context.list, { serverRelativeUrl: '/sites/one/SitePages' });
    const owner = await install(context);
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav.mock.calls).toEqual([[a, 'Home web', a, undefined, undefined]]);
    expect(window._o365AnalyticsInfo.visits?.currentPageContext()?.listItemId).toBeUndefined();
  });

  it('does not use a webpart ID as replay evidence or let it veto a fresh Details page', async () => {
    show(a + '/SitePages/Details.aspx?ID=20');
    const context = pageContext(a, 'Earlier', 20);
    context.site.serverRequestPath = location.pathname;
    Object.assign(context.list, { serverRelativeUrl: '/sites/one/SitePages' });
    const owner = await install(context);
    expect(window._o365AnalyticsInfo.visits?.currentPageContext()).toBeUndefined();
    context.listItem.id = 30;
    context.web.title = 'Destination';
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav.mock.calls).toEqual([[a, 'Destination', a, undefined, undefined]]);
  });

  it('retries a verified collection-only first visit in the positional-only mode supported by 1.6.12', async () => {
    Object.assign(window, { spoInsightsAITrackerVersion: '1.6.12' });
    try {
      const context = pageContext(a, 'Unbound', 10);
      context.site.serverRequestPath = undefined;
      const owner = await install(context);
      const registry = window._o365AnalyticsInfo, visits = registry.visits;
      nav.mockImplementation(() => {
        expect(window._o365AnalyticsInfo.siteUrls).toBe(registry.siteUrls);
        return !window._o365AnalyticsInfo.visits?.currentPageContext;
      });
      fire(owner);
      jest.advanceTimersByTime(2000);
      fire(owner);
      jest.advanceTimersByTime(2000);
      expect(visits?.forEvent(a, a)?.reported).toBe(true);
      expect(window._o365AnalyticsInfo.visits).toBe(visits);
      expect(nav.mock.calls.every(call => call[0] === a && call[1] === '' && call[2] === a && !call[3] && !call[4])).toBe(true);
    } finally {
      nav.mockReset();
      delete (window as { spoInsightsAITrackerVersion?: string }).spoInsightsAITrackerVersion;
    }
  });

  it('does not forward a pending snapshot contradicted by a later event for that visit', async () => {
    const context = pageContext(a, 'Source');
    const owner = await install(context);
    fire(owner);
    jest.advanceTimersByTime(2000);
    show(a + '/SitePages/Destination.aspx');
    Object.assign(context, pageContext(a, 'Candidate', 20));
    fire(owner);
    context.site.serverRequestPath = home;
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav).toHaveBeenCalledTimes(1);
    expect(window._o365AnalyticsInfo.visits?.currentPageContext()).toBeUndefined();
    Object.assign(context, pageContext(a, 'Destination', 21));
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav.mock.calls[1]).toEqual([a, 'Destination', a, 'Pages', 21]);
  });

  it('does not bind a different subweb or let an encoded managed path borrow the root registration', async () => {
    const origin = location.origin;
    show('/SitesArchive' + home);
    const root = await install(pageContext(origin, 'Root'));
    fire(root);
    jest.advanceTimersByTime(2000);
    nav.mockClear();
    show('/%74EAMS/Caf%C3%A9' + home);
    fire(root);
    jest.advanceTimersByTime(2000);
    expect(nav).not.toHaveBeenCalled();
    const site = origin + '/teams/café';
    const context = pageContext(site, 'Another subweb');
    context.site.serverRequestPath = location.pathname;
    context.web.absoluteUrl += '/another';
    const owner = await install(context);
    fire(owner);
    expect(window._o365AnalyticsInfo.visits?.currentPageContext()).toBeUndefined();
    Object.assign(context, pageContext(site, 'Destination', 20));
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav.mock.calls).toEqual([[site, 'Destination', site, 'Pages', 20]]);
    expect(window._o365AnalyticsInfo.visits?.currentPageContext()?.listItemId).toBe(20);
  });

  it('notifies the running tracker after actual registry mutations, never for a duplicate or a navigation report', async () => {
    const observed: string[][] = [];
    Object.assign(window, { _o365AnalyticsInfo: { siteUrls: [a], siteRegistrationChanged: (site: string): void => {
      observed.push([site, ...window._o365AnalyticsInfo.siteUrls]);
    } } });
    show(b + home);
    const owner = await install(pageContext(b, 'Two'));
    const duplicate = await install(pageContext(b, 'Duplicate'));
    duplicate.close();
    expect(observed).toEqual([[b, a, b]]);
    expect(nav).not.toHaveBeenCalled();
    owner.close();
    expect(observed).toEqual([[b, a, b], [b, a]]);
    expect(nav).not.toHaveBeenCalled();
  });

  it('does not revive a timer after a browser-observed detour before SharePoint reports the return', async () => {
    const historyChanges = new EventTarget();
    Object.defineProperty(window, 'navigation', { configurable: true, value: historyChanges });
    Object.defineProperty(window.performance, 'getEntriesByType', { configurable: true, value: () => [{ name: a + home }] });
    const owner = await install(pageContext(a, 'One'));
    expect(window._o365AnalyticsInfo.visits?.isOriginalDocumentVisit()).toBe(true);
    fire(owner);
    show(b + home);
    historyChanges.dispatchEvent(new Event('currententrychange'));
    show(a + home);
    historyChanges.dispatchEvent(new Event('currententrychange'));
    jest.advanceTimersByTime(2000);
    expect(window._o365AnalyticsInfo.visits?.isOriginalDocumentVisit()).toBe(false);
    expect(nav).not.toHaveBeenCalled();
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav).toHaveBeenCalledTimes(1);
    delete (window.performance as { getEntriesByType?: unknown }).getEntriesByType;
  });
});
