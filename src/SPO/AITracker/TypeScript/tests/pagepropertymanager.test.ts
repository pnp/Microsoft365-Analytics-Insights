import { TestPagePropertyManager, TestPageDataService } from './MockLoaders';
import { InMemoryPageStateManager } from '../src/PageProps/PageState';
import { IPageDataService, LikesUserEntity, PageComment } from '../src/Definitions';
import { PagePropertyManager } from '../src/PageProps/PagePropertyManager';
import { PageProps } from '../src/PageProps/Models/PageProps';
import { BasePageStateManager } from '../src/PageProps/PageState';

/**
 * A variant of TestPagePropertyManager that allows controlling
 * per-call results (resolve/reject) for props, likes, and comments.
 */
class ControllablePagePropertyManager extends PagePropertyManager {
    propsResult: () => Promise<PageProps>;
    likesResult: () => Promise<LikesUserEntity[]>;
    commentsResult: () => Promise<PageComment[]>;

    constructor(
        stateManager: BasePageStateManager,
        dataService: IPageDataService,
        propsResult: () => Promise<PageProps>,
        likesResult?: () => Promise<LikesUserEntity[]>,
        commentsResult?: () => Promise<PageComment[]>,
    ) {
        super(stateManager, dataService);
        this.propsResult = propsResult;
        this.likesResult = likesResult ?? (() => Promise.resolve([]));
        this.commentsResult = commentsResult ?? (() => Promise.resolve([]));
    }

    loadPropsRaw(listTitle: string, pageItemId: number, url: string): Promise<PageProps> {
        return this.propsResult();
    }
    loadLikes(listTitle: string, pageItemId: number, url: string): Promise<LikesUserEntity[]> {
        return this.likesResult();
    }
    loadComments(listTitle: string, pageItemId: number, url: string): Promise<PageComment[]> {
        return this.commentsResult();
    }
}

describe('PagePropertyManager.handleNewPage', () => {
    let stateManager: InMemoryPageStateManager;
    let dataService: TestPageDataService;

    beforeEach(() => {
        stateManager = new InMemoryPageStateManager();
        dataService = new TestPageDataService();
    });

    test('returns false when listTitle is empty', async () => {
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        const result = await manager.handleNewPage(1, 'http://url', '');
        expect(result).toBe(false);
    });

    test('returns false when listTitle is undefined', async () => {
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        const result = await manager.handleNewPage(1, 'http://url', undefined);
        expect(result).toBe(false);
    });

    test('returns false when pageItemId is less than 1', async () => {
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        const result = await manager.handleNewPage(0, 'http://url', 'Site Pages');
        expect(result).toBe(false);
    });

    test('returns false when pageItemId is -1', async () => {
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        const result = await manager.handleNewPage(-1, 'http://url', 'Site Pages');
        expect(result).toBe(false);
    });

    test('returns true when all data loads successfully', async () => {
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        const result = await manager.handleNewPage(1, 'http://url', 'Site Pages');
        expect(result).toBe(true);
    });

    test('records page as seen after successful load', async () => {
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        await manager.handleNewPage(1, 'http://url', 'Site Pages');
        expect(stateManager.pageSeen('Site Pages', 1)).not.toBeNull();
    });

    test('returns false when page was recently seen (within interval)', async () => {
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        // First load succeeds and registers page
        await manager.handleNewPage(1, 'http://url', 'Site Pages');
        // Second call should skip because page was recently seen
        const result = await manager.handleNewPage(1, 'http://url', 'Site Pages');
        expect(result).toBe(false);
    });

    test('returns false when props loading fails', async () => {
        const manager = new ControllablePagePropertyManager(
            stateManager, dataService,
            () => Promise.reject('API error')
        );
        const result = await manager.handleNewPage(1, 'http://url', 'Site Pages');
        expect(result).toBe(false);
    });

    test('does not register page seen when props loading fails', async () => {
        const manager = new ControllablePagePropertyManager(
            stateManager, dataService,
            () => Promise.reject('API error')
        );
        await manager.handleNewPage(1, 'http://url', 'Site Pages');
        expect(stateManager.pageSeen('Site Pages', 1)).toBeNull();
    });

    test('still returns true when likes fail but props succeed', async () => {
        const manager = new ControllablePagePropertyManager(
            stateManager, dataService,
            () => Promise.resolve(new PageProps('http://url', { Title: 'Test' })),
            () => Promise.reject('likes error'),
            () => Promise.resolve([])
        );
        const result = await manager.handleNewPage(1, 'http://url', 'Site Pages');
        expect(result).toBe(true);
    });

    test('still returns true when comments fail but props succeed', async () => {
        const manager = new ControllablePagePropertyManager(
            stateManager, dataService,
            () => Promise.resolve(new PageProps('http://url', { Title: 'Test' })),
            () => Promise.resolve([]),
            () => Promise.reject('comments error')
        );
        const result = await manager.handleNewPage(1, 'http://url', 'Site Pages');
        expect(result).toBe(true);
    });

    test('invokes newPagePropsLoaded callback when provided', async () => {
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        let receivedProps: PageProps | null = null;
        await manager.handleNewPage(1, 'http://url', 'Site Pages', (props: PageProps) => {
            receivedProps = props;
        });
        expect(receivedProps).not.toBeNull();
        expect(receivedProps!.url).toBe('https://whatever');
    });

    test('records page props via data service', async () => {
        const recordSpy = jest.spyOn(dataService, 'recordPageProps');
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        await manager.handleNewPage(1, 'http://url', 'Site Pages');
        expect(recordSpy).toHaveBeenCalled();
    });

    test('adds likes count to props when likes load succeeds', async () => {
        const likes: LikesUserEntity[] = [
            { id: '1', email: 'u1@test.com', creationDate: new Date() },
            { id: '2', email: 'u2@test.com', creationDate: new Date() },
        ];
        let capturedProps: PageProps | null = null;
        const manager = new ControllablePagePropertyManager(
            stateManager, dataService,
            () => Promise.resolve(new PageProps('http://url', { Title: 'Test' })),
            () => Promise.resolve(likes),
            () => Promise.resolve([])
        );
        await manager.handleNewPage(1, 'http://url', 'Site Pages', (props: PageProps) => {
            capturedProps = props;
        });
        expect(capturedProps!.pageLikes.length).toBe(2);
        expect(capturedProps!.props.PageLikesCount).toBe(2);
    });

    test('adds comments count to props when comments load succeeds', async () => {
        const comments: PageComment[] = [
            { id: '1', email: 'u1@test.com', comment: 'Hi', isReply: false, creationDate: new Date() },
        ];
        let capturedProps: PageProps | null = null;
        const manager = new ControllablePagePropertyManager(
            stateManager, dataService,
            () => Promise.resolve(new PageProps('http://url', { Title: 'Test' })),
            () => Promise.resolve([]),
            () => Promise.resolve(comments)
        );
        await manager.handleNewPage(1, 'http://url', 'Site Pages', (props: PageProps) => {
            capturedProps = props;
        });
        expect(capturedProps!.pageComments.length).toBe(1);
        expect(capturedProps!.props.CommentsCount).toBe(1);
    });

    test('setPageUpdateIntervalMinutes changes interval', () => {
        const manager = new TestPagePropertyManager('val', stateManager, dataService);
        manager.setPageUpdateIntervalMinutes(120);
        expect(manager.pageUpdateIntervalMinutes).toBe(120);
    });
});
