// What the SPFx extension shares with this script on modern pages, as window._o365AnalyticsInfo: the site collections tracked in
// this page. Each instance of the extension registers its site collection as it starts, and unregisters it as SharePoint disposes
// of the instance - which SharePoint does when it navigates to a site collection the extension isn't enabled on.
export interface SitesTrackedByExtension {
    siteUrls: string[];
}

// Whether the page at this URL is on a tracked site collection. SharePoint navigates between site collections without reloading
// the page, so this script keeps running after it navigates to one the installer didn't set up for tracking - where it mustn't
// collect anything. Classic pages have no SPFx, so no list: the site's own custom action loads this script there, and every
// navigation loads a new page.
export function isOnTrackedSite(url: string, sites: SitesTrackedByExtension | undefined): boolean {
    if (!sites || !Array.isArray(sites.siteUrls)) {
        return true;
    }
    return sites.siteUrls.some(siteUrl => isUrlInSite(url, siteUrl));
}

// Whether a URL is a site collection's, or below it. Not case-sensitive, as SharePoint isn't: its own links can have the host in
// capitals, and a user can type the path in any case.
export function isUrlInSite(url: string, siteUrl: string): boolean {
    const page = urlParts(url), site = urlParts(siteUrl);
    if (!page || !site || page.origin !== site.origin) {
        return false;
    }
    const sitePath = site.path.replace(/\/+$/, '');
    return page.path === sitePath || page.path.indexOf(sitePath + '/') === 0;
}

function urlParts(url: string): { origin: string, path: string } | null {
    try {
        const u = new URL(url);
        let path = u.pathname;
        try {
            path = decodeURIComponent(path);        // A site's URL can come with its path encoded, or not
        } catch {
            // Not valid percent-encoding: compare it as it is
        }
        return { origin: u.origin.toLowerCase(), path: path.toLowerCase() };
    } catch {
        return null;
    }
}
