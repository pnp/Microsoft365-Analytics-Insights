import { getSPRequestDuration, isValidGuid, uuidv4 } from '../src/DataFunctions';

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
