import { BasePageStateManager } from "../PageState";
import { debug, error } from "../../Logger";
import { PagesList } from "../../Definitions";

export class LocalStoragePageStateManager extends BasePageStateManager {
    clear(): void {
        debug("Clearing local storage");
        localStorage.removeItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY);
    }

    static PAGES_SEEN_STORAGE_KEY = "AITrackerPagesMetadataUploaded";

    registerPageSeen(webUrl: string, listTitle: string, pageItemId: number, forgetSeenBefore?: Date): Date {
        const pagesConfig = this.loadCurrentOrDefault();

        // Local storage is shared with SharePoint itself, so don't let this grow with every page ever visited
        if (forgetSeenBefore) {
            pagesConfig.pagesUploadedFor = pagesConfig.pagesUploadedFor.filter(p => new Date(p.seenOn) >= forgetSeenBefore);
        }

        const date = new Date();
        const pageId = this.getPageId(webUrl, listTitle, pageItemId);
        const r = pagesConfig.pagesUploadedFor.find(p => p.pageId === pageId);
        if (r != null) {
            r.seenOn = date;
        } else
            pagesConfig.pagesUploadedFor.push({ pageId: pageId, seenOn: date });

        try {
            localStorage.setItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY, JSON.stringify(pagesConfig));
        } catch (e) {
            error("Couldn't set local storage with page info - see JS console");
            error(e);
        }

        return date;
    }

    pageSeen(webUrl: string, listTitle: string, pageItemId: number): Date | null {
        const pagesConfig = this.loadCurrentOrDefault();

        const pageId = this.getPageId(webUrl, listTitle, pageItemId);
        const r = pagesConfig.pagesUploadedFor.find(p => p.pageId === pageId);

        return r != null ? new Date(r.seenOn) : null;
    }

    loadCurrentOrDefault(): PagesList {
        const storagePagesVal = localStorage.getItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY);

        let newPageConfig: PagesList = { pagesUploadedFor: [] };
        if (storagePagesVal) {
            try {
                const pageConfig = JSON.parse(storagePagesVal);
                if (pageConfig && Array.isArray(pageConfig.pagesUploadedFor)) {
                    return pageConfig;
                }
            } catch (e) {
                error("Failed to parse pages-seen data from local storage");
                error(e);
            }
        }
        return newPageConfig;
    }
}
