

export abstract class BasePageStateManager {
    abstract pageSeen(webUrl: string, listTitle: string, pageItemId: number): Date | null;

    // forgetSeenBefore drops pages last seen before then, so the record doesn't grow forever
    abstract registerPageSeen(webUrl: string, listTitle: string, pageItemId: number, forgetSeenBefore?: Date): Date;
    abstract clear(): void;

    // Lists belong to a web, and every web has a "Site Pages" list numbering its pages from 1, so the web is part of the ID
    getPageId(webUrl: string, listTitle: string, pageItemId: number): string {
        return `Web '${webUrl.replace(/\/+$/, '').toLowerCase()}': list '${listTitle}': item ID: '${pageItemId}'`;
    }
}

export class InMemoryPageStateManager extends BasePageStateManager {
    clear(): void {
        this.pages = [];
    }

    registerPageSeen(webUrl: string, listTitle: string, pageItemId: number, forgetSeenBefore?: Date): Date {

        if (forgetSeenBefore) {
            this.pages = this.pages.filter(p => p.seenOn >= forgetSeenBefore);
        }

        const date = new Date();
        const pageId = this.getPageId(webUrl, listTitle, pageItemId);
        const r = this.pages.find(p => p.pageId === pageId);
        if (r != null) {
            r.seenOn = date;
        } else
            this.pages.push({ pageId: pageId, seenOn: date });

        return date;
    }
    pages: pageSeenOn[] = []

    pageSeen(webUrl: string, listTitle: string, pageItemId: number): Date | null {
        const pageId = this.getPageId(webUrl, listTitle, pageItemId);
        const r = this.pages.find(p => p.pageId === pageId);
        return r != null ? r.seenOn : null;
    }
}

export interface pageSeenOn {
    pageId: string,
    seenOn: Date
}
