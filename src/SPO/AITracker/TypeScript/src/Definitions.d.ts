import { PageProps } from "./PageProps/Models/PageProps"
import { pageSeenOn } from "./PageProps/PageState"

// Custom properties we send to App Insights
interface BaseCustomProps {
    pageRequestId: string,
    sessionId: string,
    timeStamp: string
}
interface PageViewDataProperties extends BaseCustomProps {
    webUrl: string,
    siteUrl: string,
    webTitle: string,
    aiTrackerVersion: string,
    pageTitle: string,
    spRequestDuration?: number,
}
interface SearchEventProperties extends BaseCustomProps {
    userSearch: string
}
interface TimingEventProperties extends BaseCustomProps {
    url: string,
    activeTime: number,
    aiTrackerVersion: string,
}


interface PageStats {
    secondsOnPage: number | null,
    pageRequestId: string,
    url: string
}

interface spPageContextInfo {
    userLoginName: string,
    webAbsoluteUrl: string,
    siteAbsoluteUrl: string,
    webTitle: string,
    pageItemId: number,
    listTitle: string
}


interface ClickEventProps extends BaseCustomProps {
    linkText?: string,
    altText?: string,
    href?: string,
    classNames?: string
}


interface SpoPerfJson {
    spRequestDuration: number
}

interface ListItemPropsResponse<T> {
    d: T;
}
interface ResultsList<T> {
    results: T[];
}
interface PageLikesListData {
    likeCount: number,
    isLikedByUser: boolean,
    likedBy: ResultsList<LikesUserEntity>
}
interface LikesUserEntity
{
    id: string;
    email: string,
    creationDate: Date,     // When like was created
}

interface CommentsListData {
    results: SpRestApiComment[]
}

interface SpRestApiComment {
    id: string;
    text: string;
    author: SPSharingPrincipal;
    createdDate: Date;
    replies: CommentsListData;
    parentId?: number;
}
interface SPSharingPrincipal {
    email: string;
    id: number;
}

// Something to record page properties. Usually to App Insights as a custom event
interface IPageDataService {
    recordPageProps(props: PageProps): void;
}
interface PageComment {
    id: string;
    email: string;
    comment: string;
    isReply: boolean;
    creationDate: Date;
    parentId?: number;
}

interface PagesList {
    pagesUploadedFor: pageSeenOn[],
}

interface ObjectWithExpiry {
    expiry: Date
}

interface ClickData { linkText: string, altText: string, classNames: string | null, href: string }
