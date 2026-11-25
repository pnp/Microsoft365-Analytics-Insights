export interface INavigationVisit {
  readonly url: string;
  readonly site: string;
  reported: boolean;
  pageContext?: IModernPageContext;
}

export interface IModernPageContext {
  url: string;
  siteUrl: string;
  webUrl: string;
  webTitle: string;
  listTitle?: string;
  listItemId?: number;
}

// Shared by this document's configured customizers, not by tabs or full page loads.
export class NavigationVisits {
  private visit?: INavigationVisit;

  public constructor(private readonly page: Window, private readonly sites: string[]) {
    // A detour and return (including history.back()) can both precede the next SPFx event.
    // Only invalidate here. SharePoint's navigatedEvent supplies the context for a report.
    (page as Window & { navigation?: EventTarget }).navigation?.addEventListener?.('currententrychange', () => this.observeLocation());
  }

  public observeLocation(): string {
    const url = this.page.location.href.split('#')[0];
    if (this.visit && this.visit.url !== url) this.visit = undefined;
    return url;
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

export function contextMatchesVisit(visit: INavigationVisit, webUrl: string, requestPath: string, initial: boolean): boolean {
  const page = address(visit.url), site = address(visit.site), web = address(webUrl);
  if (!site || !web || !page || !containsPage(site, web) || !containsPage(web, page)) return false;
  // A navigatedEvent supplies the destination context; onInit by itself identifies only the collection.
  if (!requestPath) return !initial;
  try {
    const visible = new URL(visit.url), request = new URL(requestPath, visible.origin);
    return request.origin === visible.origin && decodeURI(request.pathname).toLowerCase() === decodeURI(visible.pathname).toLowerCase() &&
      (request.search === visible.search || (!initial && !request.search));
  } catch { return false; }
}
