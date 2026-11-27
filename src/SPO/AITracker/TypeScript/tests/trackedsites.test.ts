import { isOnTrackedSite, isUrlInSite } from '../src/TrackedSites';

describe('isUrlInSite', () => {
    const site = 'https://contoso.sharepoint.com/sites/test';

    test('a page of the site, or of a subsite', () => {
        expect(isUrlInSite('https://contoso.sharepoint.com/sites/test', site)).toBe(true);
        expect(isUrlInSite('https://contoso.sharepoint.com/sites/test/', site)).toBe(true);
        expect(isUrlInSite('https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx?web=1#section', site)).toBe(true);
        expect(isUrlInSite('https://contoso.sharepoint.com/sites/test/team/_layouts/15/settings.aspx', site)).toBe(true);
    });

    test('ignores case, as SharePoint does, and how the path is encoded', () => {
        expect(isUrlInSite('https://CONTOSO.sharepoint.com/Sites/TEST/SitePages/Home.aspx', site)).toBe(true);
        expect(isUrlInSite('https://contoso.sharepoint.com/sites/Caf%C3%A9/SitePages/Home.aspx', 'https://contoso.sharepoint.com/sites/café')).toBe(true);
    });

    test('not another site whose address starts the same, another host, or something that isn\'t a URL', () => {
        expect(isUrlInSite('https://contoso.sharepoint.com/sites/test2/SitePages/Home.aspx', site)).toBe(false);
        expect(isUrlInSite('https://contoso.sharepoint.com/sites/other', site)).toBe(false);
        expect(isUrlInSite('https://contoso-my.sharepoint.com/sites/test', site)).toBe(false);
        expect(isUrlInSite('not a url', site)).toBe(false);
        expect(isUrlInSite('https://contoso.sharepoint.com/sites/test', undefined as unknown as string)).toBe(false);
    });

    test('the root collection contains its pages and subwebs, not collections on managed paths', () => {
        const root = 'https://contoso.example';
        for (const path of ['/', '/SitePages/Home.aspx', '/news/SitePages/Home.aspx', '/sites-archive/page']) {
            expect(isUrlInSite(root + path, root + '/')).toBe(true);
        }
        for (const path of ['/sites/one', '/TEAMS/Two/page', '/s%69tes/One/page', '/teams%2FTwo/page']) {
            expect(isUrlInSite(root + path, root)).toBe(false);
            expect(isOnTrackedSite(root + path, { siteUrls: [root] })).toBe(false);
        }
    });

    test('only the most specific registered owner can report its page', () => {
        const root = 'https://contoso.example', site = root + '/sites/Café', web = site + '/News';
        const page = root + '/SITES/Caf%C3%A9/news/Home.aspx';
        const sites = { siteUrls: [root, site, web] };
        expect(isOnTrackedSite(page, sites)).toBe(true);
        expect(isOnTrackedSite(page, sites, root)).toBe(false);
        expect(isOnTrackedSite(page, sites, site)).toBe(false);
        expect(isOnTrackedSite(page, sites, web.toUpperCase() + '/')).toBe(true);
        expect(isOnTrackedSite(root + '/sites/Caf%C3%A9-other/Home.aspx', sites)).toBe(false);
    });
});

describe('isOnTrackedSite', () => {
    const page = 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx';

    test('on classic pages, which have no SPFx extension to register sites, every page is', () => {
        expect(isOnTrackedSite(page, undefined)).toBe(true);
    });

    test('on modern pages, only pages of the sites the extension has registered', () => {
        expect(isOnTrackedSite(page, { siteUrls: ['https://contoso.sharepoint.com/sites/other', 'https://contoso.sharepoint.com/sites/test'] })).toBe(true);
        expect(isOnTrackedSite(page, { siteUrls: ['https://contoso.sharepoint.com/sites/other'] })).toBe(false);
        expect(isOnTrackedSite(page, { siteUrls: [] })).toBe(false);
    });
});
