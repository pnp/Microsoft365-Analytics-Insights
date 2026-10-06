import { getSPRequestDuration, isSamePage, isValidGuid, uuidv4 } from '../src/DataFunctions';

describe('isSamePage', () => {
    test('the same address is the same page', () => {
        expect(isSamePage('https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx', 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx')).toBe(true);
    });

    test('ignores the #fragment: an in-page link stays on the page', () => {
        expect(isSamePage('https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx?a=1', 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx?a=1#section')).toBe(true);
    });

    test('a different query is a different page: on a search page it is another search', () => {
        expect(isSamePage('https://contoso.sharepoint.com/_layouts/15/search.aspx/siteall?q=first', 'https://contoso.sharepoint.com/_layouts/15/search.aspx/siteall?q=second')).toBe(false);
    });

    test('a different path is a different page, including a non-Latin one', () => {
        expect(isSamePage('https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx', 'https://contoso.sharepoint.com/sites/test/SitePages/News.aspx')).toBe(false);
        expect(isSamePage('https://contoso.sharepoint.com/sites/test/SitePages/Καλημέρα.aspx', 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx')).toBe(false);
    });

    test('another site is a different page', () => {
        expect(isSamePage('https://contoso.sharepoint.com/', 'https://contoso.sharepoint.com/sites/test')).toBe(false);
    });
});

describe('DataFunctions', () => {

    describe('uuidv4', () => {
        test('generates a non-empty string', () => {
            const result = uuidv4();
            expect(result).toBeTruthy();
            expect(typeof result).toBe('string');
        });

        test('generates unique values', () => {
            const id1 = uuidv4();
            const id2 = uuidv4();
            expect(id1).not.toBe(id2);
        });

        test('generated GUID is valid', () => {
            const id = uuidv4();
            expect(isValidGuid(id)).toBeTruthy();
        });
    });

    describe('isValidGuid', () => {
        test('returns true for valid GUID', () => {
            expect(isValidGuid('f5b7ced7-2039-47f9-a22d-32c66d2eec65')).toBeTruthy();
        });

        test('returns false for empty string', () => {
            expect(isValidGuid('')).toBeFalsy();
        });

        test('returns false for null', () => {
            expect(isValidGuid(null)).toBeFalsy();
        });

        test('returns false for random string', () => {
            expect(isValidGuid('not-a-guid-at-all')).toBeFalsy();
        });

        test('returns false for partial GUID', () => {
            expect(isValidGuid('f5b7ced7-2039')).toBeFalsy();
        });

        test('returns true for uppercase GUID', () => {
            expect(isValidGuid('F5B7CED7-2039-47F9-A22D-32C66D2EEC65')).toBeTruthy();
        });
    });

    describe('getSPRequestDuration', () => {
        test('returns null when no perf block found', () => {
            expect(getSPRequestDuration('no perf data here')).toBeNull();
        });

        test('returns null for empty string', () => {
            expect(getSPRequestDuration('')).toBeNull();
        });

        test('extracts spRequestDuration from valid perf block', () => {
            const htmlWithPerf = `some html before "perf":{"spRequestDuration":123.45}, some html after`;
            const result = getSPRequestDuration(htmlWithPerf);
            expect(result).toBe(123.45);
        });

        test('extracts spRequestDuration from integer value', () => {
            const htmlWithPerf = `content "perf":{"spRequestDuration":500}, more content`;
            expect(getSPRequestDuration(htmlWithPerf)).toBe(500);
        });

        test('handles \\r in perf JSON', () => {
            const htmlWithPerf = `content "perf":{"spRequestDuration":\\r200}, more content`;
            expect(getSPRequestDuration(htmlWithPerf)).toBe(200);
        });

        test('returns null for malformed JSON in perf block', () => {
            const htmlWithPerf = `content "perf":{"spRequestDuration":not_a_number}, more content`;
            expect(getSPRequestDuration(htmlWithPerf)).toBeNull();
        });

        test('returns null when perf block start found but no end', () => {
            const htmlWithPerf = `content "perf":{"spRequestDuration":123`;
            expect(getSPRequestDuration(htmlWithPerf)).toBeNull();
        });

        test('handles perf block with multiple properties', () => {
            const htmlWithPerf = `content "perf":{"spRequestDuration":250,"iisLatency":10}, more`;
            const result = getSPRequestDuration(htmlWithPerf);
            expect(result).toBe(250);
        });
    });
});
