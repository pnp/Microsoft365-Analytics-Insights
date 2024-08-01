

export abstract class BasePageStateManager {
    abstract pageSeen(listTitle: string, pageItemId: number): Date | null;
    abstract registerPageSeen(listTitle: string, pageItemId: number): Date;
    abstract clear(): void;

    getPageId(listTitle: string, pageItemId: number): string {
        return `List '${listTitle}': item ID: '${pageItemId}'`;
    }
}

export class InMemoryPageStateManager extends BasePageStateManager {
    clear(): void {
        this.pages = [];
    }

    registerPageSeen(listTitle: string, pageItemId: number): Date {

        const date = new Date();
        const r = this.pages.find(p => p.pageId === this.getPageId(listTitle, pageItemId));
        if (r != null) {
            r.seenOn = date;
        } else
            this.pages.push({ pageId: this.getPageId(listTitle, pageItemId), seenOn: date });

        return date;
    }
    pages: pageSeenOn[] = []

    pageSeen(listTitle: string, pageItemId: number): Date | null {
        const r = this.pages.find(p => p.pageId === this.getPageId(listTitle, pageItemId));
        return r != null ? r.seenOn : null;
    }
}

export interface pageSeenOn {
    pageId: string,
    seenOn: Date
}
