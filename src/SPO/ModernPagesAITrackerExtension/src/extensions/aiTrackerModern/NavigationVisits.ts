import type { PageContext } from '@microsoft/sp-page-context';

export interface INavigationVisit {
  readonly url: string;
  readonly site: string;
  reported: boolean;
  pageContext?: IModernPageContext;
}

export interface IModernPageContext {
  url: string;
  siteUrl: string;
  binding: 'request' | 'event';
  webUrl?: string;
  webTitle?: string;
  listTitle?: string;
  listItemId?: number;
}

// Shared by this document's configured customizers, not by tabs or full page loads.
export class NavigationVisits {
  private visit?: INavigationVisit;
  private documentUrl?: string;

  public constructor(private readonly page: Window, private readonly sites: string[]) {
    this.documentUrl = page.performance?.getEntriesByType?.('navigation')[0]?.name?.split('#')[0];
    this.observeLocation();
    // A detour and return (including history.back()) can both precede the next SPFx event.
    // Only invalidate here. SharePoint's navigatedEvent supplies the context for a report.
    const navigation = (page as Window & { navigation?: EventTarget }).navigation;
    if (navigation?.addEventListener) navigation.addEventListener('currententrychange', () => this.observeLocation());
    else page.addEventListener('popstate', () => this.observeLocation());
  }

  public observeLocation(): string {
    const url = this.page.location.href.split('#')[0];
    // Once the document visit ends, returning to its URL cannot restore its title or load time.
    if (url !== this.documentUrl) this.documentUrl = undefined;
    if (this.visit && this.visit.url !== url) this.visit = undefined;
    return url;
  }

  public isOriginalDocumentVisit(): boolean {
    return this.observeLocation() === this.documentUrl;
  }

  public forEvent(registeredSite: string, contextSite: string): INavigationVisit | undefined {
    const url = this.observeLocation();
    if (!sameSite(registeredSite, contextSite) || this.visibleSite() !== registeredSite) return undefined;
    if (!this.visit || this.visit.site !== registeredSite) {
      this.visit = { url, site: registeredSite, reported: false };
    }
    return this.visit;
  }

  public isCurrent(visit: INavigationVisit, contextSite: string): boolean {
    this.observeLocation();
    return this.visit === visit && sameSite(visit.site, contextSite) && this.visibleSite() === visit.site;
  }

  // The standalone tracker can load between a navigation and its delayed report.
  public currentPageContext(): IModernPageContext | undefined {
    this.observeLocation();
    return this.visit && this.visibleSite() === this.visit.site ? this.visit.pageContext : undefined;
  }

  private visibleSite(): string | undefined {
    const page = address(this.page.location.href);
    if (!page) return undefined;
    let owner: string | undefined;
    let longest = -1;
    // A root-site instance can still be registered alongside a more specific site collection.
    for (const registered of this.sites) {
      const site = address(registered);
      if (site && site.path.length > longest && containsPage(site, page)) {
        owner = registered;
        longest = site.path.length;
      }
    }
    return owner;
  }
}

type Address = { origin: string; path: string };

function address(url: string): Address | undefined {
  try {
    const u = new URL(url);
    return { origin: u.origin, path: decodeURIComponent(u.pathname).replace(/\/+/g, '/').replace(/\/$/, '').toLowerCase() };
  }
  catch { return undefined; }
}

function containsPage(site: Address, page: Address): boolean {
  // Keep in step with the separately deployed standalone tracker (TrackedSites).
  return page.origin === site.origin && (site.path
    ? page.path === site.path || page.path.indexOf(site.path + '/') === 0
    : !/^\/(?:sites|teams)\/[^/]/.test(page.path));
}

export function sameSite(first: string, second: string): boolean {
  if (first === second) return true;
  const a = address(first), b = address(second);
  return !!a && !!b && a.origin === b.origin && a.path === b.path;
}

export function contextForVisit(visit: INavigationVisit, context: PageContext, freshEvent: boolean): IModernPageContext | undefined {
  const webUrl = context.web.absoluteUrl;
  const page = address(visit.url), site = address(visit.site), web = address(webUrl);
  if (!site || !web || !page || !sameSite(visit.site, context.site.absoluteUrl) ||
      !containsPage(site, web) || !containsPage(web, page)) return undefined;
  try {
    const visible = new URL(visit.url);
    const item = context.listItem?.id;
    let itemMatches = true, namesItem = false, namesList = false, onlyItemKeys = true;
    visible.searchParams.forEach((value, key) => {
      const name = key.toLowerCase();
      if (name === 'id' && /^\d+$/.test(value)) {
        namesItem = true;
        if (Number(value) !== item) itemMatches = false;
      } else if ((name === 'listid' || name === 'list') && /^\{?[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}\}?$/i.test(value)) {
        namesList = true;
        if (value.replace(/[{}]/g, '').toLowerCase() !== context.list?.id?.toString().toLowerCase()) itemMatches = false;
      } else onlyItemKeys = false;
    });
    if (!itemMatches) return undefined;
    const listPath = context.list?.serverRelativeUrl;
    if (listPath) {
      const list = address(new URL(listPath, visible.origin).href);
      if (!list || !containsPage(web, list) || (!namesList && !containsPage(list, page))) return undefined;
    }
    const requestPath = context.site.serverRequestPath;
    // A new top-level navigation can establish a page view without identifying a list item.
    // Subscribing can synchronously replay an old event; that is no stronger than onInit.
    if (!requestPath) return freshEvent ? { url: visit.url, siteUrl: visit.site, binding: 'event' } : undefined;
    const request = new URL(requestPath, visible.origin);
    if (request.origin !== visible.origin || decodeURI(request.pathname).toLowerCase() !== decodeURI(visible.pathname).toLowerCase() ||
        (request.search !== visible.search && (!!request.search || (!freshEvent && !(namesItem && onlyItemKeys))))) return undefined;
    return { url: visit.url, siteUrl: visit.site, binding: 'request', webUrl, webTitle: context.web.title,
      listTitle: context.list?.title, listItemId: item };
  } catch { return undefined; }
}
