import { IPageDataService, LikesUserEntity, PageComment } from "../Definitions";
import { debug, log, warn } from "../Logger";
import { PageProps } from "./Models/PageProps";
import { BasePageStateManager } from "./PageState";

const MAX_CUSTOM_PROP_SIZE_BYTES = 8192	;    // "Property value string length" - https://learn.microsoft.com/en-us/azure/azure-monitor/app/api-custom-events-metrics#limits

// Handles whether to upload page information to App Insights.
// Should only do it once per session to avoid generating excessive updates
export abstract class PagePropertyManager {
    
    stateManager: BasePageStateManager;
    dataService: IPageDataService;
    pageUpdateIntervalMinutes: number = 60 * 24; // Default to once a day
    constructor(pageStateManager: BasePageStateManager, pageDataService: IPageDataService) {
        this.stateManager = pageStateManager;
        this.dataService = pageDataService;
    }

    // Get list of page props in raw form, i.e. don't parse taxonomy fields. webUrl is the web the page's list is in
    abstract loadPropsRaw(webUrl: string, listTitle: string, pageItemId: number, url: string): Promise<PageProps>;

    abstract loadLikes(webUrl: string, listTitle: string, pageItemId: number, url: string): Promise<LikesUserEntity[]>;

    abstract loadComments(webUrl: string, listTitle: string, pageItemId: number, url: string): Promise<PageComment[]>;

    setPageUpdateIntervalMinutes(interval: number) {
        log(`Setting page update interval to ${interval} minutes`);
        this.pageUpdateIntervalMinutes = interval;
    }

    // Decide whether to register page properties or not.
    // Return if props were loaded or not
    handleNewPage(webUrl: string, pageItemId: number, url: string, listTitle?: string, newPagePropsLoaded?: Function): Promise<boolean> {

        if (!webUrl || !listTitle || pageItemId < 1) {
            debug(`Skipping page properties - webUrl: '${webUrl || ''}', listTitle: '${listTitle || ''}', pageItemId: ${pageItemId}`);
            return Promise.resolve(false);
        }

        const pageResult = this.stateManager.pageSeen(webUrl, listTitle, pageItemId);
        const expiryDate = new Date(Date.now() - this.pageUpdateIntervalMinutes * 60 * 1000);

        if (!pageResult || pageResult < expiryDate) {
            debug("Not read & submitted page properties recently...");

            // Load all page props, comments, and likes
            const pagePropsLoadPromise = this.loadPropsRaw(webUrl, listTitle, pageItemId, url);
            const likesLoadPromise = this.loadLikes(webUrl, listTitle, pageItemId, url);
            const commentsLoadPromise = this.loadComments(webUrl, listTitle, pageItemId, url);

            // Combine into one result
            return Promise.allSettled([pagePropsLoadPromise, likesLoadPromise, commentsLoadPromise]).then(loadResults => {

                // Make sure we at least have the page props request
                const loadedPagePropsResult = loadResults[0];
                if (loadedPagePropsResult.status === "fulfilled") {
                    const loadedPagePropsAll = loadedPagePropsResult.value;

                    if (newPagePropsLoaded)
                        newPagePropsLoaded(loadedPagePropsAll);

                    // Parse taxonomy fields
                    const taxFieldCount = loadedPagePropsAll.setTaxonomyFieldsFromRawLoadedProps();
                    debug(`Read ${loadedPagePropsAll.propsCount()} properties and ${taxFieldCount} taxonomy fields for page id ${pageItemId} on list ${listTitle}. Will not update metadata for page again.`);

                    // Add likes to page properties
                    const likesLoadPromiseResult = loadResults[1];
                    if (likesLoadPromiseResult.status === "fulfilled") {

                        // Add totals & details
                        loadedPagePropsAll.pageLikes = likesLoadPromiseResult.value;
                        loadedPagePropsAll.props.PageLikesCount = likesLoadPromiseResult.value.length;
                    } else {
                        warn(`Failed to load likes for page id ${pageItemId}: ${(likesLoadPromiseResult as PromiseRejectedResult).reason}`);
                    }

                    // Add comments
                    const commentsLoadPromiseResult = loadResults[2];
                    if (commentsLoadPromiseResult.status === "fulfilled") {
                        
                        // Add totals & details
                        loadedPagePropsAll.pageComments = commentsLoadPromiseResult.value;
                        loadedPagePropsAll.props.CommentsCount = commentsLoadPromiseResult.value.length;
                    } else {
                        warn(`Failed to load comments for page id ${pageItemId}: ${(commentsLoadPromiseResult as PromiseRejectedResult).reason}`);
                    }

                    // Log loaded props with api provider. Split into multiple parts if needed
                    const splitPageProps = loadedPagePropsAll.splitIntoMutliple(MAX_CUSTOM_PROP_SIZE_BYTES);
                    debug(`Splitting page properties into ${splitPageProps.length} parts`);
                    splitPageProps.forEach((pageProps, idx) => {
                        this.dataService.recordPageProps(pageProps);
                    });

                    // Don't keep registering page props. Pages seen before the expiry would be read again anyway, so forget them
                    this.stateManager.registerPageSeen(webUrl, listTitle, pageItemId, expiryDate);
                    return Promise.resolve(true);
                } else {
                    warn(`Failed to load page properties for page id ${pageItemId} on list ${listTitle}: ${(loadedPagePropsResult as PromiseRejectedResult).reason}`);
                    return Promise.resolve(false);
                }
            });

        }
        else {
            debug("Ignoring page properties collection - done so previously");
            return Promise.resolve(false);
        }
    }
}
