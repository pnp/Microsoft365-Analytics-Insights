

export abstract class BasePageStateManager {
    abstract pageSeen(listTitle: string, pageItemId: number): boolean;
    abstract registerPageSeen(listTitle: string, pageItemId: number): void;

    getPageId(listTitle: string, pageItemId: number): string {
        return `List '${listTitle}': item ID: '${pageItemId}'`;
    }
}

export class InMemoryPageStateManager extends BasePageStateManager {
    registerPageSeen(listTitle: string, pageItemId: number): void {
        this.pages.push(this.getPageId(listTitle, pageItemId))
    }
    pages: string[] = []

    pageSeen(listTitle: string, pageItemId: number): boolean {
        return this.pages.indexOf(this.getPageId(listTitle, pageItemId)) > -1
    }
}
