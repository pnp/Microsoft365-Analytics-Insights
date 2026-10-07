import { getApiReturnJson } from "../../Api";
import { CommentsListData, PageLikesListData, ListItemPropsResponse, PageComment, LikesUserEntity } from "../../Definitions";
import { debug, debugObj } from "../../Logger";
import { PageProps } from "../Models/PageProps";
import { PagePropertyManager } from "../PagePropertyManager";
import { BasePageStateManager } from "../PageState";
import { WebPageDataService } from "./WebPageDataService";

// SPO implementation
export class SpoPagePropertyManager extends PagePropertyManager {

    constructor(pageStateManager: BasePageStateManager, pageDataService: WebPageDataService) {
        super(pageStateManager, pageDataService);
    }

    // REST URL for a list item in the page's own web
    listItemApiUrl(webUrl: string, listTitle: string, pageItemId: number): string {
        return webUrl.replace(/\/+$/, '') +
            "/_api/web/lists/getbytitle('" + encodeURIComponent(listTitle) + "')/items(" + pageItemId + ")";
    }

    loadLikes(webUrl: string, listTitle: string, pageItemId: number, url: string): Promise<LikesUserEntity[]> {
        debug(`Loading likes count for page ID ${pageItemId}`);

        const apiUrlPageLikesUrl = this.listItemApiUrl(webUrl, listTitle, pageItemId) + "/likedByInformation?$expand=likedby";

        return getApiReturnJson<ListItemPropsResponse<PageLikesListData>>(apiUrlPageLikesUrl)
            .then((likesResponse: ListItemPropsResponse<PageLikesListData>) => {

                // Build clean likes list (without all the meta tags etc)
                const likesParsed: LikesUserEntity[] = [];
                likesResponse.d.likedBy.results.forEach(l => {
                    likesParsed.push({ creationDate: l.creationDate, email: l.email, id: l.id });
                });
                debug("Likes response: " + likesResponse.d.likeCount);
                debugObj("Likes parsed:", likesParsed);
                return likesParsed;
            });
    }


    loadComments(webUrl: string, listTitle: string, pageItemId: number, url: string): Promise<PageComment[]> {
        debug(`Loading comments for page ID ${pageItemId}`);
        const apiUrlPageComments = this.listItemApiUrl(webUrl, listTitle, pageItemId) + "/comments?$expand=replies";

        return getApiReturnJson<ListItemPropsResponse<CommentsListData>>(apiUrlPageComments)
            .then((commentsResponse: ListItemPropsResponse<CommentsListData>) => {
                debug("Comments response: " + commentsResponse.d.results.length);
                debugObj("Comments data:", commentsResponse);

                // Build flat comments list
                const comments: PageComment[] = [];
                commentsResponse.d.results.forEach(c => {
                    comments.push({ id: c.id, comment: c.text, email: c.author.email, isReply: false, creationDate: c.createdDate });
                    
                    c.replies.results.forEach(r => comments.push({ id: r.id, comment: r.text, email: r.author.email, isReply: true, creationDate: r.createdDate, parentId: r.parentId }));
                });

                return Promise.resolve(comments);
            });
    }


    // Override base. Get page metadata from SP page properties API
    loadPropsRaw(webUrl: string, listTitle: string, pageItemId: number, url: string): Promise<PageProps> {
        debug(`Loading properties for page ID ${pageItemId}`);
        const apiUrlPageProps = this.listItemApiUrl(webUrl, listTitle, pageItemId) + "/properties";

        return getApiReturnJson<ListItemPropsResponse<any>>(apiUrlPageProps)
            .then((r: any) => this.processPageProps(url, r));
    }

    processPageProps(url: string, r: ListItemPropsResponse<any>): PageProps {
        const pp: PageProps = new PageProps(url, r.d);

        return pp;
    }
}
