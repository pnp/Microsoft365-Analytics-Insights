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

    test('the root site collection is the site of every page on its host', () => {
        expect(isUrlInSite('https://contoso.sharepoint.com/SitePages/Home.aspx', 'https://contoso.sharepoint.com')).toBe(true);
        expect(isUrlInSite('https://contoso.sharepoint.com/SitePages/Home.aspx', 'https://contoso.sharepoint.com/')).toBe(true);
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
