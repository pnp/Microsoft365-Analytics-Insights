import { IPageDataService, LikesUserEntity, PageComment } from "../Definitions";
import { debug } from "../Logger";
import { PageProps } from "./Models/PageProps";
import { BasePageStateManager } from "./PageState";

// Handles whether to upload page information to App Insights.
// Should only do it once per session to avoid generating excessive updates
export abstract class PagePropertyManager {
    stateManager: BasePageStateManager;
    dataService: IPageDataService;
    constructor(pageStateManager: BasePageStateManager, pageDataService: IPageDataService) {
        this.stateManager = pageStateManager;
        this.dataService = pageDataService;
    }

    // Get list of page props in raw form, i.e. don't parse taxonomy fields
    abstract loadPropsRaw(listTitle: string, pageItemId: number, url: string): Promise<PageProps>;

    abstract loadLikes(listTitle: string, pageItemId: number, url: string): Promise<LikesUserEntity[]>;

    abstract loadComments(listTitle: string, pageItemId: number, url: string): Promise<PageComment[]>;

    // Decide whether to register page properties or not.
    // Return if props were loaded or not
    handleNewPage(pageItemId: number, url: string, listTitle?: string, newPagePropsLoaded?: Function): Promise<boolean> {

        if (!listTitle || pageItemId < 1) {
            return Promise.resolve(false);
        }

        if (!this.stateManager.pageSeen(listTitle, pageItemId)) {
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
                    const loadedPageProps = loadedPagePropsResult.value;

                    if (newPagePropsLoaded)
                        newPagePropsLoaded(loadedPageProps);

                    // Parse taxonomy fields
                    const taxFieldCount = loadedPageProps.setTaxonomyFieldsFromRawLoadedProps();
                    debug(`Read ${loadedPageProps.propsCount()} properties and ${taxFieldCount} taxonomy fields for page id ${pageItemId} on list ${listTitle}. Will not update metadata for page again.`);

                    // Add likes to page properties
                    const likesLoadPromiseResult = loadResults[1];
                    if (likesLoadPromiseResult.status === "fulfilled") {

                        // Add totals & details
                        loadedPageProps.props.PageLikes = likesLoadPromiseResult.value;
                        loadedPageProps.props.PageLikesCount = likesLoadPromiseResult.value.length;
                    }

                    // Add comments
                    const commentsLoadPromiseResult = loadResults[2];
                    if (commentsLoadPromiseResult.status === "fulfilled") {
                        
                        // Add totals & details
                        loadedPageProps.props.Comments = commentsLoadPromiseResult.value;
                        loadedPageProps.props.CommentsCount = commentsLoadPromiseResult.value.length;
                    }

                    // Log loaded props with api provider
                    this.dataService.recordPageProps(loadedPageProps);

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
