// What the SPFx extension shares with this script on modern pages, as window._o365AnalyticsInfo: the site collections tracked in
// this page. Each instance of the extension registers its site collection as it starts, and unregisters it as SharePoint disposes
// of the instance - which SharePoint does when it navigates to a site collection the extension isn't enabled on.
export interface SitesTrackedByExtension {
    siteUrls: string[];
    siteRegistrationChanged?: (site: string) => void;
    visits?: { currentPageContext?: () => ModernPageContext | undefined };
}

export interface ModernPageContext {
    url: string;
    siteUrl: string;
    webUrl: string;
    webTitle: string;
    listTitle?: string;
    listItemId?: number;
}

// Whether the page at this URL is on a tracked site collection. SharePoint navigates between site collections without reloading
// the page, so this script keeps running after it navigates to one the installer didn't set up for tracking - where it mustn't
// collect anything. Classic pages have no SPFx, so no list: the site's own custom action loads this script there, and every
// navigation loads a new page.
export function isOnTrackedSite(url: string, sites: SitesTrackedByExtension | undefined, reportedSite?: string): boolean {
    if (!sites) return !reportedSite || isUrlInSite(url, reportedSite);
    const owner = registeredSiteFor(url, sites);
    if (!owner || !reportedSite || owner === reportedSite) return !!owner;
    const site = urlParts(owner), reported = urlParts(reportedSite);
    return !!site && !!reported && site.origin === reported.origin && site.path === reported.path;
}

export function registeredSiteFor(url: string, sites: SitesTrackedByExtension): string | undefined {
    const page = urlParts(url);
    if (!page || !Array.isArray(sites.siteUrls)) return undefined;
    let owner: string | undefined;
    let longest = -1;
    for (const siteUrl of sites.siteUrls) {
        const site = urlParts(siteUrl);
        if (site && site.path.length > longest && containsPage(site, page)) {
            owner = siteUrl;
            longest = site.path.length;
        }
    }
    return owner;
}

// Whether a URL is a site collection's, or below it. Not case-sensitive, as SharePoint isn't: its own links can have the host in
// capitals, and a user can type the path in any case.
export function isUrlInSite(url: string, siteUrl: string): boolean {
    const page = urlParts(url), site = urlParts(siteUrl);
    return !!page && !!site && containsPage(site, page);
}

type UrlParts = { origin: string, path: string };

function containsPage(site: UrlParts, page: UrlParts): boolean {
    // Keep in step with SPFx NavigationVisits: managed-path collections do not belong to the root collection.
    return page.origin === site.origin && (site.path
        ? page.path === site.path || page.path.indexOf(site.path + '/') === 0
        : !/^\/(?:sites|teams)\/[^/]/.test(page.path));
}

function urlParts(url: string): UrlParts | null {
    try {
        const u = new URL(url);
        return { origin: u.origin, path: decodeURIComponent(u.pathname).replace(/\/+/g, '/').replace(/\/$/, '').toLowerCase() };
    } catch {
        return null;
    }
}
