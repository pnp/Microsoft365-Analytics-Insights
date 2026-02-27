import { callApiReturnString, getApiReturnJson, postApiReturnJson } from '../src/Api';

// Mock global fetch
const mockFetch = jest.fn();
global.fetch = mockFetch;

describe('Api', () => {

    beforeEach(() => {
        mockFetch.mockReset();
    });

    describe('callApiReturnString', () => {
        test('returns body text on successful GET', async () => {
            mockFetch.mockResolvedValueOnce({
                ok: true,
                text: () => Promise.resolve('response body'),
            });

            const result = await callApiReturnString('https://api.test.com/data', 'GET');
            expect(result).toBe('response body');
            expect(mockFetch).toHaveBeenCalledWith('https://api.test.com/data', expect.objectContaining({
                method: 'GET',
                headers: expect.objectContaining({
                    'Accept': 'application/json;odata=verbose',
                    'Content-Type': 'application/json',
                }),
            }));
        });

        test('returns body text on successful POST', async () => {
            mockFetch.mockResolvedValueOnce({
                ok: true,
                text: () => Promise.resolve('{"result": true}'),
            });

            const result = await callApiReturnString('https://api.test.com/action', 'POST');
            expect(result).toBe('{"result": true}');
        });

        test('rejects with body text on HTTP error with body', async () => {
            mockFetch.mockResolvedValueOnce({
                ok: false,
                status: 404,
                text: () => Promise.resolve('Not found details'),
            });

            await expect(callApiReturnString('https://api.test.com/missing', 'GET'))
                .rejects.toBe('Not found details');
        });

        test('rejects on HTTP error with empty body', async () => {
            mockFetch.mockResolvedValueOnce({
                ok: false,
                status: 500,
                text: () => Promise.resolve(''),
            });

            await expect(callApiReturnString('https://api.test.com/error', 'GET'))
                .rejects.toBe('');
        });

        test('rejects on network error (fetch throws)', async () => {
            mockFetch.mockRejectedValueOnce(new TypeError('Failed to fetch'));

            await expect(callApiReturnString('https://api.test.com/offline', 'GET'))
                .rejects.toContain('Network error');
        });

        test('rejects with descriptive message on network error', async () => {
            mockFetch.mockRejectedValueOnce(new TypeError('net::ERR_CONNECTION_REFUSED'));

            await expect(callApiReturnString('https://api.test.com/refused', 'POST'))
                .rejects.toMatch(/Network error POSTing from API/);
        });
    });

    describe('getApiReturnJson', () => {
        test('parses JSON response for GET', async () => {
            mockFetch.mockResolvedValueOnce({
                ok: true,
                text: () => Promise.resolve('{"name": "test", "value": 42}'),
            });

            const result = await getApiReturnJson<{ name: string; value: number }>('https://api.test.com/data');
            expect(result.name).toBe('test');
            expect(result.value).toBe(42);
        });

        test('rejects when response is not valid JSON', async () => {
            mockFetch.mockResolvedValueOnce({
                ok: true,
                text: () => Promise.resolve('not json'),
            });

            await expect(getApiReturnJson('https://api.test.com/bad')).rejects.toThrow();
        });
    });

    describe('postApiReturnJson', () => {
        test('parses JSON response for POST', async () => {
            mockFetch.mockResolvedValueOnce({
                ok: true,
                text: () => Promise.resolve('{"success": true}'),
            });

            const result = await postApiReturnJson<{ success: boolean }>('https://api.test.com/action');
            expect(result.success).toBe(true);
        });

        test('propagates HTTP error', async () => {
            mockFetch.mockResolvedValueOnce({
                ok: false,
                status: 403,
                text: () => Promise.resolve('Forbidden'),
            });

            await expect(postApiReturnJson('https://api.test.com/forbidden')).rejects.toBe('Forbidden');
        });
    });
});
