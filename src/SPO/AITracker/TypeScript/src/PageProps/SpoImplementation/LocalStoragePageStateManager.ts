const moment = require('moment');
import { BasePageStateManager } from "../PageState";
import { error } from "../../Logger";
import { PagesList } from "../../Definitions";

export class LocalStoragePageStateManager extends BasePageStateManager {
    static PAGES_SEEN_STORAGE_KEY = "AITrackerPagesMetadataUploaded";
    static TOMORROW = moment().add(1, 'days');

    registerPageSeen(listTitle: string, pageItemId: number): void {
        const pagesConfig = this.loadCurrentOrDefault();
        pagesConfig.pagesUploadedFor.push(this.getPageId(listTitle, pageItemId))

        try {
            localStorage.setItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY, JSON.stringify(pagesConfig));
        } catch (e) {
            error("Couldn't set local storage with page info - see JS console");
            error(e);
        }
    }

    pageSeen(listTitle: string, pageItemId: number): boolean {
        const pagesConfig = this.loadCurrentOrDefault();
        return pagesConfig.pagesUploadedFor.indexOf(this.getPageId(listTitle, pageItemId)) > -1
    }

    loadCurrentOrDefault(): PagesList {
        const storagePagesVal = localStorage.getItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY);

        let newPageConfig: PagesList = {expiry: LocalStoragePageStateManager.TOMORROW.toDate(), pagesUploadedFor: []};
        let pageConfig: PagesList = newPageConfig;
        let validConfig = false;
        if (storagePagesVal) {
            pageConfig = JSON.parse(storagePagesVal);
            if (pageConfig) {
                const expiry = new Date(pageConfig.expiry);
                if (expiry > new Date()) {
                    // Config is valid and still within expiry
                    validConfig = true;
                }
            }
            if (!validConfig) pageConfig = newPageConfig;
        }
        return pageConfig;
    }
}
