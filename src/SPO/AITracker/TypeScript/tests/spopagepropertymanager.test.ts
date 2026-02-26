import { SpoPagePropertyManager } from '../src/PageProps/SpoImplementation/SpoPagePropertyManager';
import { InMemoryPageStateManager } from '../src/PageProps/PageState';
import { WebPageDataService } from '../src/PageProps/SpoImplementation/WebPageDataService';
import { AppInsightsWrapper } from '../src/AppInsightsWrapper';
import { ApplicationInsights } from '@microsoft/applicationinsights-web';
import { PageProps } from '../src/PageProps/Models/PageProps';

// Mock Api module
jest.mock('../src/Api', () => ({
    getApiReturnJson: jest.fn(),
}));

import { getApiReturnJson } from '../src/Api';
const mockGetApiReturnJson = getApiReturnJson as jest.MockedFunction<typeof getApiReturnJson>;

function createMockAI(): ApplicationInsights {
    return {
        trackPageView: jest.fn(),
        trackEvent: jest.fn(),
        setAuthenticatedUserContext: jest.fn(),
        loadAppInsights: jest.fn(),
    } as unknown as ApplicationInsights;
}

describe('SpoPagePropertyManager', () => {
    let stateManager: InMemoryPageStateManager;
    let wrapper: AppInsightsWrapper;
    let dataService: WebPageDataService;
    let manager: SpoPagePropertyManager;

    const webAbsoluteUrl = 'https://contoso.sharepoint.com/sites/test';

    beforeEach(() => {
        mockGetApiReturnJson.mockReset();
        stateManager = new InMemoryPageStateManager();
        wrapper = new AppInsightsWrapper(createMockAI(), 'session-1');
        dataService = new WebPageDataService(wrapper);
        manager = new SpoPagePropertyManager(stateManager, dataService, webAbsoluteUrl);
    });

    describe('loadPropsRaw', () => {
        test('calls API with correct URL and returns PageProps', async () => {
            mockGetApiReturnJson.mockResolvedValueOnce({
                d: { Title: 'Test Page', Author: 'admin' }
            });

            const result = await manager.loadPropsRaw('Site Pages', 1, 'https://test.com');

            expect(mockGetApiReturnJson).toHaveBeenCalledWith(
                expect.stringContaining("/_api/web/lists/getbytitle('Site%20Pages')/items(1)/properties")
            );
            expect(result).toBeInstanceOf(PageProps);
            expect(result.props.Title).toBe('Test Page');
        });

        test('encodes list title with special characters in URL', async () => {
            mockGetApiReturnJson.mockResolvedValueOnce({ d: { Title: 'Test' } });

            await manager.loadPropsRaw("Pages Library's & More", 5, 'https://test.com');

            const calledUrl = mockGetApiReturnJson.mock.calls[0][0];
            expect(calledUrl).toContain(encodeURIComponent("Pages Library's & More"));
            expect(calledUrl).not.toContain("Pages Library's & More");
        });

        test('rejects when API fails', async () => {
            mockGetApiReturnJson.mockRejectedValueOnce('Not found');

            await expect(manager.loadPropsRaw('Site Pages', 99, 'https://test.com'))
                .rejects.toBe('Not found');
        });
    });

    describe('loadLikes', () => {
        test('calls API with correct URL and returns parsed likes', async () => {
            mockGetApiReturnJson.mockResolvedValueOnce({
                d: {
                    likeCount: 2,
                    isLikedByUser: false,
                    likedBy: {
                        results: [
                            { id: '1', email: 'user1@contoso.com', creationDate: '2023-01-01T00:00:00Z' },
                            { id: '2', email: 'user2@contoso.com', creationDate: '2023-01-02T00:00:00Z' },
                        ]
                    }
                }
            });

            const likes = await manager.loadLikes('Site Pages', 1, 'https://test.com');

            expect(mockGetApiReturnJson).toHaveBeenCalledWith(
                expect.stringContaining("/_api/web/lists/getbytitle('Site%20Pages')/items(1)/likedByInformation")
            );
            expect(likes.length).toBe(2);
            expect(likes[0].email).toBe('user1@contoso.com');
            expect(likes[1].email).toBe('user2@contoso.com');
        });

        test('returns empty array when no likes', async () => {
            mockGetApiReturnJson.mockResolvedValueOnce({
                d: { likeCount: 0, isLikedByUser: false, likedBy: { results: [] } }
            });

            const likes = await manager.loadLikes('Site Pages', 1, 'https://test.com');
            expect(likes).toEqual([]);
        });

        test('encodes list title in likes URL', async () => {
            mockGetApiReturnJson.mockResolvedValueOnce({
                d: { likeCount: 0, isLikedByUser: false, likedBy: { results: [] } }
            });

            await manager.loadLikes("Test's List", 1, 'https://test.com');
            const calledUrl = mockGetApiReturnJson.mock.calls[0][0];
            expect(calledUrl).toContain(encodeURIComponent("Test's List"));
        });
    });

    describe('loadComments', () => {
        test('calls API with correct URL and returns parsed comments', async () => {
            mockGetApiReturnJson.mockResolvedValueOnce({
                d: {
                    results: [
                        {
                            id: 'c1',
                            text: 'Great page!',
                            author: { email: 'user1@contoso.com', id: 1 },
                            createdDate: '2023-06-01T00:00:00Z',
                            replies: { results: [] }
                        }
                    ]
                }
            });

            const comments = await manager.loadComments('Site Pages', 1, 'https://test.com');

            expect(mockGetApiReturnJson).toHaveBeenCalledWith(
                expect.stringContaining("/_api/web/lists/getbytitle('Site%20Pages')/items(1)/comments")
            );
            expect(comments.length).toBe(1);
            expect(comments[0].comment).toBe('Great page!');
            expect(comments[0].isReply).toBe(false);
        });

        test('flattens comment replies into result', async () => {
            mockGetApiReturnJson.mockResolvedValueOnce({
                d: {
                    results: [
                        {
                            id: 'c1',
                            text: 'Parent comment',
                            author: { email: 'user1@contoso.com', id: 1 },
                            createdDate: '2023-06-01T00:00:00Z',
                            replies: {
                                results: [
                                    {
                                        id: 'r1',
                                        text: 'Reply to parent',
                                        author: { email: 'user2@contoso.com', id: 2 },
                                        createdDate: '2023-06-02T00:00:00Z',
                                        parentId: 1
                                    }
                                ]
                            }
                        }
                    ]
                }
            });

            const comments = await manager.loadComments('Site Pages', 1, 'https://test.com');

            expect(comments.length).toBe(2);
            expect(comments[0].isReply).toBe(false);
            expect(comments[0].comment).toBe('Parent comment');
            expect(comments[1].isReply).toBe(true);
            expect(comments[1].comment).toBe('Reply to parent');
            expect(comments[1].parentId).toBe(1);
        });

        test('returns empty array when no comments', async () => {
            mockGetApiReturnJson.mockResolvedValueOnce({
                d: { results: [] }
            });

            const comments = await manager.loadComments('Site Pages', 1, 'https://test.com');
            expect(comments).toEqual([]);
        });

        test('encodes list title in comments URL', async () => {
            mockGetApiReturnJson.mockResolvedValueOnce({
                d: { results: [] }
            });

            await manager.loadComments("Test's List", 1, 'https://test.com');
            const calledUrl = mockGetApiReturnJson.mock.calls[0][0];
            expect(calledUrl).toContain(encodeURIComponent("Test's List"));
        });
    });

    describe('processPageProps', () => {
        test('creates PageProps from API response', () => {
            const response = { d: { Title: 'Hello', Modified: '2023-01-01' } };
            const result = manager.processPageProps('https://test.com', response);

            expect(result).toBeInstanceOf(PageProps);
            expect(result.url).toBe('https://test.com');
            expect(result.props.Title).toBe('Hello');
        });

        test('filters invalid property types from response', () => {
            const response = { d: { Title: 'Hello', nested: { deep: true }, valid: 42 } };
            const result = manager.processPageProps('https://test.com', response);

            expect(result.propsCount()).toBe(2); // Title + valid, not nested
        });
    });
});
