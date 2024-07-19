import { BasePageStateManager } from "../PageState";
import { debug, error } from "../../Logger";
import { PagesList } from "../../Definitions";

export class LocalStoragePageStateManager extends BasePageStateManager {
    clear(): void {
        debug("Clearing local storage");
        localStorage.removeItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY);
    }

    static PAGES_SEEN_STORAGE_KEY = "AITrackerPagesMetadataUploaded";

    registerPageSeen(listTitle: string, pageItemId: number): Date {
        const pagesConfig = this.loadCurrentOrDefault();

        const date = new Date();
        const r = pagesConfig.pagesUploadedFor.find(p => p.pageId === this.getPageId(listTitle, pageItemId));
        if (r != null) {
            r.seenOn = date;
        } else
            pagesConfig.pagesUploadedFor.push({ pageId: this.getPageId(listTitle, pageItemId), seenOn: new Date() });

        try {
            localStorage.setItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY, JSON.stringify(pagesConfig));
        } catch (e) {
            error("Couldn't set local storage with page info - see JS console");
            error(e);
        }

        return date;
    }

    pageSeen(listTitle: string, pageItemId: number): Date | null {
        const pagesConfig = this.loadCurrentOrDefault();

        const r = pagesConfig.pagesUploadedFor.find(p => p.pageId === this.getPageId(listTitle, pageItemId));

        return r != null ? new Date(r.seenOn) : null;
    }

    loadCurrentOrDefault(): PagesList {
        const storagePagesVal = localStorage.getItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY);

        let newPageConfig: PagesList = { pagesUploadedFor: [] };
        if (storagePagesVal) {
            const pageConfig = JSON.parse(storagePagesVal);
            if (pageConfig) {
                return pageConfig;
            }
        }
        return newPageConfig;
    }
}
