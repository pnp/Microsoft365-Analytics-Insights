import moment from "moment";
import { IPageDataService, LikesUserEntity, PageComment } from "../Definitions";
import { debug, log } from "../Logger";
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

    // Get list of page props in raw form, i.e. don't parse taxonomy fields
    abstract loadPropsRaw(listTitle: string, pageItemId: number, url: string): Promise<PageProps>;

    abstract loadLikes(listTitle: string, pageItemId: number, url: string): Promise<LikesUserEntity[]>;

    abstract loadComments(listTitle: string, pageItemId: number, url: string): Promise<PageComment[]>;

    setPageUpdateIntervalMinutes(interval: number) {
        log(`Setting page update interval to ${interval} minutes`);
        this.pageUpdateIntervalMinutes = interval;
    }

    // Decide whether to register page properties or not.
    // Return if props were loaded or not
    handleNewPage(pageItemId: number, url: string, listTitle?: string, newPagePropsLoaded?: Function): Promise<boolean> {

        if (!listTitle || pageItemId < 1) {
            return Promise.resolve(false);
        }

        const pageResult = this.stateManager.pageSeen(listTitle, pageItemId);
        const expiry = moment().add(this.pageUpdateIntervalMinutes, 'minutes');
        const expiryDate = expiry.toDate();

        if (!pageResult || pageResult > expiryDate) {
            debug("Not read & submitted page properties recently...");

            // Load all page props, comments, and likes
            const pagePropsLoadPromise = this.loadPropsRaw(listTitle, pageItemId, url);
            const likesLoadPromise = this.loadLikes(listTitle, pageItemId, url);
            const commentsLoadPromise = this.loadComments(listTitle, pageItemId, url);

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
                    }

                    // Add comments
                    const commentsLoadPromiseResult = loadResults[2];
                    if (commentsLoadPromiseResult.status === "fulfilled") {
                        
                        // Add totals & details
                        loadedPagePropsAll.pageComments = commentsLoadPromiseResult.value;
                        loadedPagePropsAll.props.CommentsCount = commentsLoadPromiseResult.value.length;
                    }

                    // Log loaded props with api provider. Split into multiple parts if needed
                    const splitPageProps = loadedPagePropsAll.splitIntoMutliple(MAX_CUSTOM_PROP_SIZE_BYTES);
                    debug(`Splitting page properties into ${splitPageProps.length} parts`);
                    splitPageProps.forEach((pageProps, idx) => {
                        this.dataService.recordPageProps(pageProps);
                    });

                    // Don't keep registering page props
                    this.stateManager.registerPageSeen(listTitle, pageItemId);
                }

                return Promise.resolve(true);
            });

        }
        else {
            debug("Ignoring page properties collection - done so previously");
            return Promise.resolve(false);
        }
    }
}
