import { IPageDataService, LikesUserEntity, PageComment } from "../src/Definitions";
import { log } from "../src/Logger";
import { PageProps } from "../src/PageProps/Models/PageProps";
import { PagePropertyManager } from "../src/PageProps/PagePropertyManager";
import { BasePageStateManager } from "../src/PageProps/PageState";

export class TestPagePropertyManager extends PagePropertyManager {
    loadLikes(listTitle: string, pageItemId: number, url: string): Promise<LikesUserEntity[]> {
        const c : LikesUserEntity = { email: "testuser@whatevs.local", creationDate: new Date(), id: "1" }

        return Promise.resolve([c]);
    }
    loadComments(listTitle: string, pageItemId: number, url: string): Promise<PageComment[]> {
        const c : PageComment = { email: "testuser@whatevs.local", comment: "Test comment", isReply: false, id: "1", creationDate: new Date() }
        return Promise.resolve([c])
    }
    _testValForProps: string;

    // Fake loading props
    loadPropsRaw(listTitle: string, pageItemId: number): Promise<PageProps> {

        return Promise.resolve(new PageProps("https://whatever", {
            randoProp1: this._testValForProps,
            randoProp2: 2,
            testTag: "1;#Fake Term 1|f5b7ced7-2039-47f9-a22d-32c66d2eec65"
        }));
    }

    constructor(testValForProps: string, pageStateManager: BasePageStateManager, pageDataService: IPageDataService) {
        super(pageStateManager, pageDataService);
        this._testValForProps = testValForProps;
    }
}

export class TestPageDataService implements IPageDataService {

    recordPageProps(props: PageProps): void {

        const pageProps = JSON.stringify(props);
        log(`TestPageDataService: Pretending to register page properties '${pageProps}'`);
    }
}
