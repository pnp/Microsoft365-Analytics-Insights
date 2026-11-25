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
  const listeners = new Map<TestCustomizer, () => void>();
  const navigatedEvent = {
    add: (owner: TestCustomizer, callback: () => void): void => { listeners.set(owner, callback.bind(owner)); },
    remove: (owner: TestCustomizer): void => { listeners.delete(owner); }
  };
  const nav = jest.fn();

  function pageContext(site: string, title: string, id = 1): {
    site: { absoluteUrl: string }; web: { absoluteUrl: string; title: string }; list: { title: string };
    listItem: { id: number }; legacyPageContext: { webTitle: string; siteAbsoluteUrl: string };
  } {
    return {
      site: { absoluteUrl: site }, web: { absoluteUrl: site, title }, list: { title: 'Pages' },
      listItem: { id }, legacyPageContext: { webTitle: title, siteAbsoluteUrl: site }
    };
  }

  async function install(context: ReturnType<typeof pageContext>): Promise<TestCustomizer> {
    const instance = new TestCustomizer();
    Object.assign(instance, {
      properties: { appInsightsConnectionStringHash: btoa('synthetic') },
      context: { pageContext: context, application: { navigatedEvent } }
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

  it('does not revive a timer after a browser-observed detour before SharePoint reports the return', async () => {
    const historyChanges = new EventTarget();
    Object.defineProperty(window, 'navigation', { configurable: true, value: historyChanges });
    const owner = await install(pageContext(a, 'One'));
    fire(owner);
    show(b + home);
    historyChanges.dispatchEvent(new Event('currententrychange'));
    show(a + home);
    historyChanges.dispatchEvent(new Event('currententrychange'));
    jest.advanceTimersByTime(2000);
    expect(nav).not.toHaveBeenCalled();
    fire(owner);
    jest.advanceTimersByTime(2000);
    expect(nav).toHaveBeenCalledTimes(1);
  });
});
