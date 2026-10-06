import TimeMe from 'timeme.js'
import { ApplicationInsights } from '@microsoft/applicationinsights-web'

import { AppInsightsWrapper } from './AppInsightsWrapper';
import { debug, error, log, warn } from './Logger';
import { linkLabel, uuidv4 } from './DataFunctions';
import { CleanCookies, GetSessionCookieVal, SetSessionCookieVal } from './Cookies';
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

export { };

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
        spoInsightsAITrackerVersion: string | undefined     // Set by the copy of this script that is tracking the page
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

    // Listen for link click events at the document level
    document.addEventListener('mousedown', interceptClickEvent);    // Mousedown for links that use stopPropagation etc
    document.addEventListener('click', interceptClickEvent);        // Click for links that only raise click (megamenu links)

    // Save time on page for the next page load to send. Not window.onbeforeunload: SharePoint assigns that too, so
    // whichever was set last silently replaced the other - losing either this or SharePoint's own handler.
    window.addEventListener('pagehide', () => pageTracker?.savePageExitToCookie());
}

// Handle page click events
function interceptClickEvent(e: MouseEvent) {
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

// Initialises AppInsights. Executes before doc-load if loaded on classic pages.
function initAppInsights(): void {

    // New session? Will create new session cookie if it is
    if (isNewSPOSession()) {

        // Clean-up cookies from any previous session (except last page stat, if there is one)
        // Last page stats could be from a previous session that got closed, and will be deleted once uploaded
        CleanCookies();

        if (window.appInsightsConnectionStringHash) {
            log("version " + AI_TRACKER_VER + ": New browsing session detected for '" + window._spPageContextInfo.userLoginName +
                "' - starting SPOInsights session '" + GetSessionCookieVal() + "' with App Insights connection string '" + atob(window.appInsightsConnectionStringHash) + "'.");
        }
    }
    else {
        debug("Resuming session '" + GetSessionCookieVal() + "' for '" + window._spPageContextInfo.userLoginName + "'.");
    }

    // Do we have a valid AI key injected into the header?
    if (!window.appInsightsConnectionStringHash) {
        error("Fatal Error: No valid Application Insights connection string key found!");
    }
    else {
        // Init AppInsights. Reference: https://github.com/Microsoft/ApplicationInsights-JS/blob/master/API-reference.md
        const appInsights = new ApplicationInsights({
            config: {
                connectionString: atob(window.appInsightsConnectionStringHash),
                disableExceptionTracking: true,
                disableAjaxTracking: true,
                disableFetchTracking: true,     // Don't send every fetch SharePoint makes as a dependency: it's cost, and nothing reads it
                isCookieUseDisabled: true,
                isBeaconApiDisabled: true
            }
        });
        appInsights.loadAppInsights();

        // Set auth context
        appInsights.setAuthenticatedUserContext(window._spPageContextInfo.userLoginName);
        if (ai === null)
            ai = new AppInsightsWrapper(appInsights, GetSessionCookieVal());

        // Construct page-prop registering system
        if (pageTracker === null) {
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

        }

        // Track page on page load 
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
}

// Track the page this script was loaded on. Classic pages only ever get here: they have no SPFx extension calling modernPageNav
function trackLoadedPage(): void {
    if (!pageTracker) return;

    // A document loaded for another URL means SharePoint navigated here without loading a page, so the browser's
    // load timings belong to the earlier page. Report a load time of 0, as for any other page navigation.
    const pageLoadDuration = documentWasLoadedForCurrentUrl() ? undefined : 0;
    pageTracker.trackCurrentPageViewAndLastPageExit(document.URL, window._spPageContextInfo.listTitle, window._spPageContextInfo.pageItemId, pageLoadDuration);

    // Only now: the SPFx extension's calls are page navigations, which need the current page to have been tracked first
    window.modernPageNav = modernPageNav;
}

function documentWasLoadedForCurrentUrl(): boolean {
    const navEntries = window.performance && window.performance.getEntriesByType ?
        window.performance.getEntriesByType('navigation') : [];
    if (navEntries.length === 0) return true;

    const withoutHash = (url: string) => url.split('#')[0];
    return withoutHash(navEntries[0].name) === withoutHash(document.URL);
}

// Called by the SPFx extension when SharePoint navigates to another page without reloading
const modernPageNav = function (webUrl: string, webTitle: string, siteUrl: string, listTitle?: string, listItemId?: number): void {

    if (pageTracker) {
        // The extension has already updated window._spPageContextInfo for the new page
        pageTracker.updatePageContext(window._spPageContextInfo);
        pageTracker.handleModernPageNav(webUrl, webTitle, siteUrl, document.URL, listTitle, listItemId);
    }
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
else {
    window.spoInsightsAITrackerVersion = AI_TRACKER_VER;

    // Do the things that can't wait until document loaded or if this JS file loads after page-load (modern pages, through SPFx extension loading the file)
    // Can't wait until pageload, as AppInsights needs to start timing page-load before that.
    initPageControls();
    initAppInsights();
    loadAndSetScriptConfig();

    log(`version ${AI_TRACKER_VER} tracking this page`);
}
