import TimeMe from 'timeme.js'
import { ApplicationInsights } from '@microsoft/applicationinsights-web'

import { AppInsightsWrapper } from './AppInsightsWrapper';
import { debug, error, log, warn } from './Logger';
import { uuidv4 } from './DataFunctions';
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
debug("Default config loaded until we get a response from the App Service API");

declare global {
    interface Window {
        _spPageContextInfo: spPageContextInfo,
        appInsightsConnectionStringHash: string | undefined,
        insightsWebRootUrlHash: string | undefined,
        modernPageNav: Function
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

    // Time Spent on Page UI
    setInterval(function () {
        var timeSpentOnPage = TimeMe.getTimeOnCurrentPageInSeconds();
        var demoLabel = document.querySelector('#timeInSeconds');
        if (demoLabel) demoLabel.innerHTML = "<p style='font-weight: bold'>" + timeSpentOnPage.toFixed(2) + " seconds.</p>";
    }, 25);

    // Listen for link click events at the document level
    if (document.addEventListener) {
        document.addEventListener('mousedown', interceptClickEvent);    // Mousedown for links that use stopPropagation etc
        document.addEventListener('click', interceptClickEvent);    // Mousedown for links that use stopPropagation etc
    }

    window.onbeforeunload = function () {
        pageTracker?.savePageExitToCookie();
    };
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
    const clickData: ClickData = { linkText: target.text, altText: target.title, classNames: classNames, href: target.href };

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
                isCookieUseDisabled: true,
                enableDebug: true,
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

            // Create new page-tracker. It will assume 1st hit is from page-load event, and therefore ignore SPFx event
            pageTracker = new PageViewTracker(ai, window._spPageContextInfo,
                new SpoPagePropertyManager(pageStateManager, new WebPageDataService(ai), window._spPageContextInfo.webAbsoluteUrl));

        }

        // Track page on page load 
        if (document.readyState !== "complete") {
            debug("Waiting for document load to track current URL and last page stats");
            if (window.addEventListener) {
                window.addEventListener('load', () => {

                    // Call trackCurrentPageViewAndLastPageExit async so load event can finish, and load timings > 0
                    setTimeout(function () {
                        if (pageTracker) {
                            // Use legacy page vars to track page
                            pageTracker.trackCurrentPageViewAndLastPageExit(document.URL,
                                window._spPageContextInfo.listTitle, window._spPageContextInfo.pageItemId);


                            // Wire up on page load. Called from SPFx application customizer
                            window.modernPageNav = window.modernPageNav || modernPageNav;
                        }

                    }, 0);
                })
            } else {
                error("Unsupported browser");
            }
        }
    }
}

const modernPageNav = function (webUrl: string, webTitle: string, siteUrl: string, listTitle?: string, listItemId?: number): void {

    // Track page, assuming we have a valid tracker loaded
    if (pageTracker) {

        pageTracker.handleModernPageNav(webUrl, webTitle, siteUrl, document.URL, listTitle, listItemId);

        // Modern extension will update window var
        pageTracker.updatePageContext(window._spPageContextInfo);
    }

    // Check again search params
    checkIfUserSearched();

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

// Check if one of the search params is present
function checkIfUserSearched(): void {

    const urlParams = new URLSearchParams(window.location.search);

    const modernPageSearch = urlParams.get("k");        // Modern-sites add search param to "k"
    const classicPageSearch = urlParams.get("q");       // Classic sites add search param to "q"

    // Search param in URL?
    if (modernPageSearch !== null) {
        ai?.trackSearch(modernPageSearch);
    }
    else if (classicPageSearch !== null) {
        ai?.trackSearch(classicPageSearch);
    }
}

function loadAndSetScriptConfig(): Promise<void> {
    if (window.insightsWebRootUrlHash && window.insightsWebRootUrlHash !== "" && window.appInsightsConnectionStringHash) {

        const apiBaseUrl = atob(window.insightsWebRootUrlHash);
        const m = new ConfigHandler(new ApiConfigLoader(apiBaseUrl, window.appInsightsConnectionStringHash));
        return m.getConfigFromCacheOrAppService().then((r: AITrackerConfig) => {
            scriptConfig = r;
            log("Script config loaded: " + JSON.stringify(scriptConfig));
            
            // Set page update interval from loaded config
            pageTracker?.setPageUpdateIntervalMinutes(r.metadataRefreshMinutes);
        }).catch(() => error("Failed to load config from API. Using default config."));

    }
    else {
        error("No valid API URL or App Insights connection string found in header!");
        return Promise.reject("No valid API URL or App Insights connection string found in header!");
    }
}

// Do the things that can't wait until document loaded or if this JS file loads after page-load (modern pages, through SPFx extension loading the file)
// Can't wait until pageload, as AppInsights needs to start timing page-load before that.
initPageControls();
initAppInsights();
loadAndSetScriptConfig();

debug("AITracker loaded");
