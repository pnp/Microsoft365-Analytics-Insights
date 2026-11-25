import TimeMe from 'timeme.js'
import { createTrackerTelemetry, TrackerTelemetry } from './TrackerTelemetry';

import { AppInsightsWrapper } from './AppInsightsWrapper';
import { debug, error, log, warn } from './Logger';
import { isSamePage, linkLabel, uuidv4 } from './DataFunctions';
import { CleanCookies, GetSessionCookieVal, RemoveRetiredCookies, SetSessionCookieVal } from './Cookies';
import { PageViewTracker } from './PageViewTracker';
import { SpoPagePropertyManager } from './PageProps/SpoImplementation/SpoPagePropertyManager';
import { BasePageStateManager, InMemoryPageStateManager } from './PageProps/PageState';
import { WebPageDataService } from './PageProps/SpoImplementation/WebPageDataService';
import { AI_TRACKER_VER } from './AiTrackerConstants';
import { ClickData, spPageContextInfo } from './Definitions';
import { LocalStoragePageStateManager } from './PageProps/SpoImplementation/LocalStoragePageStateManager';
import { DuplicateClickHandler } from './DuplicateClickHandler';
import { AITrackerConfig } from './Models';
import { LocalStorageUtils } from './LocalStorageUtils';
import { ConfigHandler } from './Config/ConfigHandler';
import { ApiConfigLoader } from './Config/ApiConfigLoader';
import { isUrlInSite, type SitesTrackedByExtension } from './TrackedSites';

export { };

// How often to check, on modern pages, that the browser is still on a tracked site
const TRACKED_SITE_CHECK_MS = 1000;

var ai: AppInsightsWrapper | null = null;
var pageTracker: PageViewTracker | null = null;
const clickHandler: DuplicateClickHandler = new DuplicateClickHandler();

var scriptConfig: AITrackerConfig = AITrackerConfig.GetDefault();
debug("Default config set until we get one from either local cache or App Service API");

declare global {
    interface Window {
        _spPageContextInfo: spPageContextInfo,
        appInsightsConnectionStringHash: string | undefined,
        insightsWebRootUrlHash: string | undefined,
        modernPageNav: Function,
        spoInsightsAITrackerVersion: string | undefined,    // Set by the copy of this script that is tracking the page
        _o365AnalyticsInfo: SitesTrackedByExtension | undefined     // Set by the SPFx extension, so on modern pages only
    }
}

// https://github.com/SharePoint/sp-dev-docs/issues/2809
window._spPageContextInfo = window._spPageContextInfo ||
{
    siteAbsoluteUrl: null,
    webAbsoluteUrl: null,
    userLoginName: null,
    webTitle: null
};

// Page functions ------->

function initPageControls() {
    TimeMe.initialize();

    // On modern pages capture before the link's own handler can change document.URL or replace the link.
    const modern = !!window._o365AnalyticsInfo;
    document.addEventListener('mousedown', interceptClickEvent, modern);    // Mousedown for links that use stopPropagation etc
    document.addEventListener('click', interceptClickEvent, modern);        // Click for links that only raise click (megamenu links)

    // Save time on page for the next page load to send. Not window.onbeforeunload: SharePoint assigns that too, so
    // whichever was set last silently replaced the other - losing either this or SharePoint's own handler.
    window.addEventListener('pagehide', e => pageTracker?.savePageExitToCookie(e.persisted));
    window.addEventListener('pageshow', e => { if (e.persisted) pageTracker?.resumeFromCache(); });
    // On a back traversal Edge makes the document visible before notifying pageshow listeners.
    // Resume before an earlier site listener can synchronously report a navigation or click.
    document.addEventListener('visibilitychange', () => { if (!document.hidden) pageTracker?.resumeFromCache(); });

    // SharePoint can navigate to a site collection that isn't tracked without reloading the page, and back. Note when it does, so
    // the time spent there is no tracked page's time on page. Classic pages have no such navigations: every one loads a new page.
    if (modern) {
        window.setInterval(() => pageTracker?.observeCurrentPage(), TRACKED_SITE_CHECK_MS);
        const navigated = () => pageTracker?.observeCurrentPage('navigation');
        if (window.navigation?.addEventListener) window.navigation.addEventListener('currententrychange', navigated);
        else window.addEventListener('popstate', navigated);
        window._o365AnalyticsInfo!.siteRegistrationChanged = site => {
            if (isUrlInSite(document.URL, site)) pageTracker?.observeCurrentPage('registration');
        };
    }
}

// Handle page click events
function interceptClickEvent(e: MouseEvent) {
    // Only the buttons that follow a link: the main button, and the middle one, which opens it in a new tab. Not the right
    // button, which only opens the context menu, nor back/forward buttons.
    if (e.button !== 0 && e.button !== 1) {
        return;
    }
    if (!pageTracker?.observeCurrentPage()) {
        return;
    }

    const target = (e.target || e.srcElement) as Element;
    if (target) {

        // Link directly clicked on?
        if (target.tagName === 'A') {
            processLinkNodeAndRegisterIfNotDuplicate(target as HTMLAnchorElement);
        }
        else if (target.parentNode && target.parentNode instanceof Element) {

            // SPAN or something else that has a link parent?
            const closestLink = target.parentElement?.closest("A");
            if (closestLink) {
                processLinkNodeAndRegisterIfNotDuplicate(closestLink as HTMLAnchorElement);
            }
        }
    }
}
function processLinkNodeAndRegisterIfNotDuplicate(target: HTMLAnchorElement) {
    const classNames = target.getAttribute('class');
    const clickData: ClickData = { linkText: linkLabel(target), altText: target.title, classNames: classNames, href: target.href };

    // Only register clicks that aren't duplicate (because they came via click & mousedown)
    clickHandler.registerClick(clickData, () => ai?.trackClick(clickData));
}

// Starts App Insights and the page tracker. False if App Insights couldn't start, such as with an invalid connection string.
// Executes before doc-load if loaded on classic pages.
function startAppInsights(): boolean {

    RemoveRetiredCookies();

    // New session? Will create new session cookie if it is
    if (isNewSPOSession()) {

        // Clean-up cookies from any previous session (except last page stat, if there is one). Before App Insights starts:
        // these include its own cookies, which it reads as it starts
        CleanCookies();

        log("version " + AI_TRACKER_VER + ": New browsing session detected for '" + window._spPageContextInfo.userLoginName +
            "' - starting SPOInsights session '" + GetSessionCookieVal() + "' with App Insights connection string '" + atob(window.appInsightsConnectionStringHash!) + "'.");
    }
    else {
        debug("Resuming session '" + GetSessionCookieVal() + "' for '" + window._spPageContextInfo.userLoginName + "'.");
    }

    let appInsights: TrackerTelemetry;
    try {
        appInsights = createTrackerTelemetry(atob(window.appInsightsConnectionStringHash!), window._spPageContextInfo.userLoginName);
    } catch (e) {
        error("Couldn't start Application Insights with the connection string this site is configured with, so this page isn't tracked. Re-run the installer for this site.");
        error(e);
        return false;
    }

    ai = new AppInsightsWrapper(appInsights, GetSessionCookieVal());

    // Use local storage for remembering pages properties sent for
    let pageStateManager: BasePageStateManager;
    if (LocalStorageUtils.isLocalStorageAvailable()) {
        pageStateManager = new LocalStoragePageStateManager();
        debug("Using LocalStoragePageStateManager for page metadata upload logic");
    }
    else {
        pageStateManager = new InMemoryPageStateManager();
        warn("Using InMemoryPageStateManager for page metadata upload logic - local storage not supported on this browser");
    }

    // Create new page-tracker. The web URL comes with each page tracked: SharePoint can navigate between sites without reloading
    pageTracker = new PageViewTracker(ai, window._spPageContextInfo,
        new SpoPagePropertyManager(pageStateManager, new WebPageDataService(ai)));

    return true;
}

// Track the page this script was loaded on, once it has loaded
function trackPageWhenLoaded(): void {
    if (document.readyState !== "complete") {
        debug("Waiting for document load to track current URL and last page stats");
        window.addEventListener('load', () => {

            // Async so the load event can finish, and load timings are > 0
            setTimeout(trackLoadedPage, 0);
        });
    }
    else {
        // The page finished loading before this script ran, so no "load" event is coming. On modern pages that happens when the
        // SPFx extension started late, or when SharePoint navigated here from a site that wasn't tracked, without reloading the page.
        debug("Document already loaded - tracking current URL and last page stats now");
        setTimeout(trackLoadedPage, 0);
    }
}

// Track the page this script was loaded on. Classic pages only ever get here: they have no SPFx extension calling modernPageNav
function trackLoadedPage(): void {
    if (!pageTracker) return;

    // A document loaded for another URL means SharePoint navigated here without loading a page.
    const pageLoadDuration = documentWasLoadedForCurrentUrl() ? undefined : 0;
    const context = pageLoadDuration === 0 ? undefined : window._spPageContextInfo;
    pageTracker.trackCurrentPageViewAndLastPageExit(document.URL, context?.listTitle, context?.pageItemId, pageLoadDuration);

    // A hard load has been tracked. A first soft view without bound context still needs a destination report.
    window.modernPageNav = modernPageNav;
}

function documentWasLoadedForCurrentUrl(): boolean {
    const navEntries = window.performance && window.performance.getEntriesByType ?
        window.performance.getEntriesByType('navigation') : [];
    if (!navEntries[0]?.name) return !window._o365AnalyticsInfo; // Classic pages only navigate by loading a document.
    return isSamePage(navEntries[0].name, document.URL);
}

// Called by the SPFx extension when SharePoint navigates to another page without reloading
const modernPageNav = function (webUrl: string, webTitle: string, siteUrl: string, listTitle?: string, listItemId?: number): boolean {
    return pageTracker?.handleModernPageNav(webUrl, webTitle, siteUrl, document.URL, listTitle, listItemId) ?? false;
}

// Used to see if this is a new browsing session, with our own cookie
function isNewSPOSession() {
    var sessionId = GetSessionCookieVal();
    if (!sessionId || sessionId === "") {

        // Start new session
        sessionId = uuidv4();
        SetSessionCookieVal(sessionId)
        debug(`Starting new session ${sessionId}`);

        return true;
    } else {
        return false;
    }
}

function loadAndSetScriptConfig(): void {
    if (window.insightsWebRootUrlHash && window.insightsWebRootUrlHash !== "" && window.appInsightsConnectionStringHash) {

        const apiBaseUrl = atob(window.insightsWebRootUrlHash);
        const m = new ConfigHandler(new ApiConfigLoader(apiBaseUrl, window.appInsightsConnectionStringHash));
        m.getConfigFromCacheOrAppService().then((r: AITrackerConfig) => {
            scriptConfig = r;
            log("Script config loaded: " + JSON.stringify(scriptConfig));
            
            // Set page update interval from loaded config
            pageTracker?.setPageUpdateIntervalMinutes(r.metadataRefreshMinutes);
        }).catch(() => error("Failed to load config from API. Using default config."));

    }
    else {
        // Not fatal: tracking carries on with the default config
        error("No valid API URL or App Insights connection string found in header! Using default config.");
    }
}

// Only one copy of this script tracks a page. Each tracked site collection has its own copy, and SharePoint navigates between
// site collections without reloading the page, so the SPFx extension of the next site loads that site's copy into the same
// page. A second copy would hook every click again and never see a page load of its own, so it leaves tracking to the first.
// An earlier version of this script only shows itself once it has tracked a page, by setting window.modernPageNav.
if (window.spoInsightsAITrackerVersion || typeof window.modernPageNav === "function") {
    debug(`AITracker ${window.spoInsightsAITrackerVersion ?? "(earlier version)"} is already tracking this page - this copy (${AI_TRACKER_VER}) won't.`);
}
else if (!window.appInsightsConnectionStringHash) {
    error("Fatal Error: No valid Application Insights connection string key found!");
}
// Claim the page only once App Insights has started. A copy that couldn't start (an invalid connection string) mustn't stop a
// correctly configured copy, loaded when SharePoint navigates to another site, from tracking the page.
else if (startAppInsights()) {
    window.spoInsightsAITrackerVersion = AI_TRACKER_VER;

    // Do the things that can't wait until document loaded or if this JS file loads after page-load (modern pages, through SPFx extension loading the file)
    // Can't wait until pageload, as AppInsights needs to start timing page-load before that.
    initPageControls();
    trackPageWhenLoaded();
    loadAndSetScriptConfig();

    log(`version ${AI_TRACKER_VER} tracking this page`);
}
