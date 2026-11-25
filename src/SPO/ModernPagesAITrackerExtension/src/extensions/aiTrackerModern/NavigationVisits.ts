export interface INavigationVisit {
  readonly url: string;
  readonly site: string;
  reported: boolean;
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

  private visibleSite(): string | undefined {
    const page = this.page.location;
    const path = sitePath(page.pathname);
    let owner: string | undefined;
    let longest = -1;
    // A root-site instance can still be registered alongside a more specific site collection.
    for (const registered of this.sites) {
      const site = new URL(registered);
      const root = sitePath(site.pathname);
      // /sites and /teams are SharePoint Online managed paths, not children of the root collection.
      // Its listener can receive the event before the collection under that path registers.
      if (!root && /^\/(?:sites|teams)\/[^/]/.test(path)) continue;
      if (page.origin === site.origin && root.length > longest && (path === root || path.indexOf(root + '/') === 0)) {
        owner = registered;
        longest = root.length;
      }
    }
    return owner;
  }
}

function sitePath(path: string): string {
  try { path = decodeURIComponent(path); } catch { /* Compare malformed escapes as supplied. */ }
  return path.replace(/\/+$/, '').toLowerCase();
}

function sameSite(first: string, second: string): boolean {
  if (first === second) return true;
  const a = new URL(first), b = new URL(second);
  return a.origin === b.origin && sitePath(a.pathname) === sitePath(b.pathname);
}
