import { spApiJson } from "../../Api";
import { CommentsListData, PageLikesListData, ListItemPropsResponse, PageComment, LikesUserEntity } from "../../Definitions";
import { LOGGING_PREFIX, debug } from "../../Logger";
import { PageProps } from "../Models/PageProps";
import { PagePropertyManager } from "../PagePropertyManager";
import { BasePageStateManager } from "../PageState";
import { WebPageDataService } from "./WebPageDataService";

// SPO implementation
export class SpoPagePropertyManager extends PagePropertyManager {
    _webAbsoluteUrl: string;

    constructor(pageStateManager: BasePageStateManager, pageDataService: WebPageDataService, webAbsoluteUrl: string) {
        super(pageStateManager, pageDataService);
        this._webAbsoluteUrl = webAbsoluteUrl;
    }

    loadLikes(listTitle: string, pageItemId: number, url: string): Promise<LikesUserEntity[]> {
        debug(`Loading likes count for page ID ${pageItemId}`);

        const apiUrlPageLikesUrl = this._webAbsoluteUrl +
            "/_api/web/lists/getbytitle('" + listTitle + "')/items(" + pageItemId + ")/likedByInformation?$expand=likedby";

        return spApiJson<ListItemPropsResponse<PageLikesListData>>(apiUrlPageLikesUrl)
            .then((likesResponse: ListItemPropsResponse<PageLikesListData>) => {

                // Build clean likes list (without all the meta tags etc)
                const likesParsed: LikesUserEntity[] = [];
                likesResponse.d.likedBy.results.forEach(l => {
                    likesParsed.push({ creationDate: l.creationDate, email: l.email, id: l.id });
                });
                console.debug(LOGGING_PREFIX + "likes response: " + likesResponse.d.likeCount);
                console.debug(likesParsed);
                return likesParsed;
            });
    }


    loadComments(listTitle: string, pageItemId: number, url: string): Promise<PageComment[]> {
        debug(`Loading comments for page ID ${pageItemId}`);
        const apiUrlPageComments = this._webAbsoluteUrl +
            "/_api/web/lists/getbytitle('" + listTitle + "')/items(" + pageItemId + ")/comments?$expand=replies";

        return spApiJson<ListItemPropsResponse<CommentsListData>>(apiUrlPageComments)
            .then((commentsResponse: ListItemPropsResponse<CommentsListData>) => {
                console.debug(LOGGING_PREFIX + "comments response: " + commentsResponse.d.results.length);
                console.debug(commentsResponse);

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
    loadPropsRaw(listTitle: string, pageItemId: number, url: string): Promise<PageProps> {
        debug(`Loading properties for page ID ${pageItemId}`);
        const apiUrlPageProps = this._webAbsoluteUrl +
            "/_api/web/lists/getbytitle('" + listTitle + "')/items(" + pageItemId + ")/properties";

        return spApiJson<ListItemPropsResponse<any>>(apiUrlPageProps)
            .then((r: any) => this.processPageProps(url, r));
    }

    processPageProps(url: string, r: ListItemPropsResponse<any>): PageProps {
        const pp: PageProps = new PageProps(url, r.d);

        return pp;
    }
}
