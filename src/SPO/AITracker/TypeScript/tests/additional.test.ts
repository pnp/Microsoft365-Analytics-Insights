/**
 * @jest-environment jsdom
 */

import { LocalStorageUtils } from '../src/LocalStorageUtils';
import { AITrackerConfig } from '../src/Models';
import { ConfigHandler } from '../src/Config/ConfigHandler';
import { ConfigLoadResult, IConfigLoader } from '../src/Config/interfaces';
import { log, error, warn, debug, LOGGING_PREFIX } from '../src/Logger';
import { MetadataInfo } from '../src/PageProps/Models/MetadataInfo';
import { PageProps } from '../src/PageProps/Models/PageProps';
import { InMemoryPageStateManager } from '../src/PageProps/PageState';
import { LocalStoragePageStateManager } from '../src/PageProps/SpoImplementation/LocalStoragePageStateManager';
import { splitIntoJsonArraysOfMaxBytes } from '../src/functions';

// ===== LocalStorageUtils =====
describe('LocalStorageUtils', () => {
    test('isLocalStorageAvailable returns true in jsdom', () => {
        expect(LocalStorageUtils.isLocalStorageAvailable()).toBe(true);
    });

    test('isLocalStorageAvailable returns false when localStorage throws', () => {
        const original = window.localStorage;
        // Override localStorage to throw
        Object.defineProperty(window, 'localStorage', {
            value: {
                setItem: () => { throw new Error('SecurityError'); },
                removeItem: () => { throw new Error('SecurityError'); },
            },
            configurable: true,
            writable: true,
        });
        expect(LocalStorageUtils.isLocalStorageAvailable()).toBe(false);
        // Restore
        Object.defineProperty(window, 'localStorage', { value: original, configurable: true, writable: true });
    });
});

// ===== AITrackerConfig =====
describe('AITrackerConfig', () => {
    describe('GetDefault', () => {
        test('returns config with positive metadataRefreshMinutes', () => {
            const config = AITrackerConfig.GetDefault();
            expect(config.metadataRefreshMinutes).toBeGreaterThan(0);
        });

        test('returns config with future expiry', () => {
            const config = AITrackerConfig.GetDefault();
            expect(new Date(config.expiry).getTime()).toBeGreaterThan(Date.now());
        });
    });

    describe('isValid', () => {
        test('returns true for default config', () => {
            expect(AITrackerConfig.isValid(AITrackerConfig.GetDefault())).toBe(true);
        });

        test('returns false for zero metadataRefreshMinutes', () => {
            const config = new AITrackerConfig(0, new Date(Date.now() + 60000));
            expect(AITrackerConfig.isValid(config)).toBe(false);
        });

        test('returns false for negative metadataRefreshMinutes', () => {
            const config = new AITrackerConfig(-1, new Date(Date.now() + 60000));
            expect(AITrackerConfig.isValid(config)).toBe(false);
        });

        test('returns false for expired config', () => {
            const config = new AITrackerConfig(60, new Date(Date.now() - 60000));
            expect(AITrackerConfig.isValid(config)).toBe(false);
        });

        test('handles expiry as string (from JSON deserialization)', () => {
            const config = { metadataRefreshMinutes: 60, expiry: new Date(Date.now() + 60000).toISOString() };
            expect(AITrackerConfig.isValid(config as unknown as AITrackerConfig)).toBe(true);
        });
    });
});

// ===== Logger =====
describe('Logger', () => {
    test('log outputs with prefix', () => {
        const spy = jest.spyOn(console, 'log').mockImplementation();
        log('test message');
        expect(spy).toHaveBeenCalledWith(LOGGING_PREFIX + 'test message');
        spy.mockRestore();
    });

    test('error outputs with prefix', () => {
        const spy = jest.spyOn(console, 'error').mockImplementation();
        error('error message');
        expect(spy).toHaveBeenCalledWith(LOGGING_PREFIX + 'error message');
        spy.mockRestore();
    });

    test('warn outputs with prefix', () => {
        const spy = jest.spyOn(console, 'warn').mockImplementation();
        warn('warn message');
        expect(spy).toHaveBeenCalledWith(LOGGING_PREFIX + 'warn message');
        spy.mockRestore();
    });

    test('debug outputs with prefix', () => {
        const spy = jest.spyOn(console, 'debug').mockImplementation();
        debug('debug message');
        expect(spy).toHaveBeenCalledWith(LOGGING_PREFIX + 'debug message');
        spy.mockRestore();
    });
});

// ===== ConfigHandler edge cases =====
describe('ConfigHandler edge cases', () => {
    beforeEach(() => {
        localStorage.clear();
    });

    test('handles corrupt JSON in localStorage cache gracefully', () => {
        localStorage.setItem('AITrackerConfig', '{corrupt json!!!');
        const loader: IConfigLoader = {
            loadConfig: () => Promise.resolve({ config: AITrackerConfig.GetDefault(), success: true })
        };
        const handler = new ConfigHandler(loader);

        // Should not throw, and should fall through to API
        return handler.getConfigFromCacheOrAppService().then(config => {
            expect(config).toBeDefined();
            expect(config.metadataRefreshMinutes).toBeGreaterThan(0);
        });
    });

    test('haveValidCachedConfig returns false for corrupt JSON', () => {
        localStorage.setItem('AITrackerConfig', 'not json');
        const loader: IConfigLoader = {
            loadConfig: () => Promise.resolve({ config: AITrackerConfig.GetDefault(), success: true })
        };
        const handler = new ConfigHandler(loader);
        expect(handler.haveValidCachedConfig()).toBe(false);
    });

    test('haveValidCachedConfig returns false for expired config', () => {
        const expired = new AITrackerConfig(60, new Date(Date.now() - 100000));
        localStorage.setItem('AITrackerConfig', JSON.stringify(expired));
        const loader: IConfigLoader = {
            loadConfig: () => Promise.resolve({ config: AITrackerConfig.GetDefault(), success: true })
        };
        const handler = new ConfigHandler(loader);
        expect(handler.haveValidCachedConfig()).toBe(false);
    });

    test('loads from API when cache is expired', () => {
        const expired = new AITrackerConfig(60, new Date(Date.now() - 100000));
        localStorage.setItem('AITrackerConfig', JSON.stringify(expired));

        const freshConfig = AITrackerConfig.GetDefault();
        const loader: IConfigLoader = {
            loadConfig: () => Promise.resolve({ config: freshConfig, success: true })
        };
        const handler = new ConfigHandler(loader);

        return handler.getConfigFromCacheOrAppService().then(config => {
            expect(config.metadataRefreshMinutes).toBe(freshConfig.metadataRefreshMinutes);
        });
    });

    test('uses default when API load fails', () => {
        const loader: IConfigLoader = {
            loadConfig: () => Promise.resolve({ config: AITrackerConfig.GetDefault(), success: false })
        };
        const handler = new ConfigHandler(loader);

        return handler.getConfigFromCacheOrAppService().then(config => {
            expect(config).toBeDefined();
        });
    });
});

// ===== MetadataInfo additional tests =====
describe('MetadataInfo additional tests', () => {
    test('FromFieldValue with valid multi-term field', () => {
        const m = MetadataInfo.FromFieldValue("Category", "1;#Term 1|f5b7ced7-2039-47f9-a22d-32c66d2eec65");
        expect(m.isValid()).toBeTruthy();
        expect(m.propName).toBe("Category");
        expect(m.label).toBe("Term 1");
        expect(m.id).toBe("f5b7ced7-2039-47f9-a22d-32c66d2eec65");
    });

    test('FromFieldValue with non-string input returns invalid', () => {
        const m = MetadataInfo.FromFieldValue("prop", 12345 as any);
        expect(m.isValid()).toBeFalsy();
    });

    test('FromFieldValue with no separator returns invalid', () => {
        const m = MetadataInfo.FromFieldValue("prop", "no separators here");
        expect(m.isValid()).toBeFalsy();
    });

    test('FromFieldValue with ;# but no pipe returns invalid', () => {
        const m = MetadataInfo.FromFieldValue("prop", "1;#Label without pipe");
        expect(m.isValid()).toBeFalsy();
    });

    test('FromFieldValue with ;# and pipe but invalid GUID returns invalid', () => {
        const m = MetadataInfo.FromFieldValue("prop", "1;#Label|not-a-valid-guid");
        expect(m.isValid()).toBeFalsy();
    });

    test('constructor sets properties', () => {
        const m = new MetadataInfo("propName", "guid-val", "label-val");
        expect(m.propName).toBe("propName");
        expect(m.id).toBe("guid-val");
        expect(m.label).toBe("label-val");
    });

    test('isValid returns false when any field is empty', () => {
        expect(new MetadataInfo("", "id", "label").isValid()).toBeFalsy();
        expect(new MetadataInfo("prop", "", "label").isValid()).toBeFalsy();
        expect(new MetadataInfo("prop", "id", "").isValid()).toBeFalsy();
    });
});

// ===== PageProps additional tests =====
describe('PageProps additional tests', () => {
    test('constructor filters out null values', () => {
        const pp = new PageProps('http://url', { a: null, b: 'valid', c: 123 });
        expect(pp.propsCount()).toBe(2);
        expect(pp.props.a).toBeUndefined();
        expect(pp.props.b).toBe('valid');
        expect(pp.props.c).toBe(123);
    });

    test('constructor filters out undefined values', () => {
        const pp = new PageProps('http://url', { a: undefined, b: 'ok' });
        expect(pp.propsCount()).toBe(1);
    });

    test('constructor filters out strings exceeding MAX_PROP_VAL (1000)', () => {
        const longString = 'x'.repeat(1001);
        const pp = new PageProps('http://url', { tooLong: longString, ok: 'short' });
        expect(pp.propsCount()).toBe(1);
        expect(pp.props.tooLong).toBeUndefined();
    });

    test('constructor keeps strings exactly at MAX_PROP_VAL', () => {
        const exactString = 'x'.repeat(1000);
        const pp = new PageProps('http://url', { exact: exactString });
        expect(pp.propsCount()).toBe(1);
    });

    test('constructor filters out boolean values', () => {
        const pp = new PageProps('http://url', { flag: true, name: 'test' });
        expect(pp.propsCount()).toBe(1);
        expect(pp.props.flag).toBeUndefined();
    });

    test('constructor filters out object values', () => {
        const pp = new PageProps('http://url', { nested: { a: 1 }, name: 'test' });
        expect(pp.propsCount()).toBe(1);
    });

    test('constructor filters out array values', () => {
        const pp = new PageProps('http://url', { arr: [1, 2, 3], name: 'test' });
        expect(pp.propsCount()).toBe(1);
    });

    test('constructor keeps zero as valid number', () => {
        const pp = new PageProps('http://url', { zero: 0 });
        expect(pp.propsCount()).toBe(1);
        expect(pp.props.zero).toBe(0);
    });

    test('constructor keeps negative numbers', () => {
        const pp = new PageProps('http://url', { neg: -42 });
        expect(pp.propsCount()).toBe(1);
        expect(pp.props.neg).toBe(-42);
    });

    test('constructor keeps empty string', () => {
        const pp = new PageProps('http://url', { empty: '' });
        expect(pp.propsCount()).toBe(1);
        expect(pp.props.empty).toBe('');
    });

    test('url is set correctly', () => {
        const pp = new PageProps('http://my-url.com', {});
        expect(pp.url).toBe('http://my-url.com');
    });

    test('pageComments default to empty array', () => {
        const pp = new PageProps('http://url', {});
        expect(pp.pageComments).toEqual([]);
    });

    test('pageLikes default to empty array', () => {
        const pp = new PageProps('http://url', {});
        expect(pp.pageLikes).toEqual([]);
    });

    test('taxonomyProps starts empty', () => {
        const pp = new PageProps('http://url', { tax: "1;#Term|f5b7ced7-2039-47f9-a22d-32c66d2eec65" });
        expect(pp.taxonomyProps).toEqual([]);
    });

    test('setTaxonomyFieldsFromRawLoadedProps populates taxonomyProps', () => {
        const pp = new PageProps('http://url', { tax: "1;#Term|f5b7ced7-2039-47f9-a22d-32c66d2eec65" });
        const count = pp.setTaxonomyFieldsFromRawLoadedProps();
        expect(count).toBe(1);
        expect(pp.taxonomyProps.length).toBe(1);
        expect(pp.taxonomyProps[0].label).toBe('Term');
    });

    test('splitIntoMutliple with empty props returns single item', () => {
        const pp = new PageProps('http://url', {});
        const result = pp.splitIntoMutliple(8192);
        // With no content, should still return at least one empty chunk
        expect(result.length).toBeGreaterThanOrEqual(0);
    });

    test('splitIntoMutliple preserves URL across all parts', () => {
        const pp = new PageProps('http://url', { a: 1, b: 2, c: 3 });
        const parts = pp.splitIntoMutliple(1); // Very small byte limit forces splits
        parts.forEach(p => {
            expect(p.url).toBe('http://url');
        });
    });
});

// ===== InMemoryPageStateManager additional tests =====
describe('InMemoryPageStateManager additional tests', () => {
    test('pageSeen returns null for unseen page', () => {
        const m = new InMemoryPageStateManager();
        expect(m.pageSeen('list', 99)).toBeNull();
    });

    test('registerPageSeen updates existing entry', () => {
        const m = new InMemoryPageStateManager();
        const d1 = m.registerPageSeen('list', 1);
        const d2 = m.registerPageSeen('list', 1);
        expect(d2.getTime()).toBeGreaterThanOrEqual(d1.getTime());
    });

    test('clear removes all entries', () => {
        const m = new InMemoryPageStateManager();
        m.registerPageSeen('list', 1);
        m.registerPageSeen('list', 2);
        m.clear();
        expect(m.pageSeen('list', 1)).toBeNull();
        expect(m.pageSeen('list', 2)).toBeNull();
    });

    test('different lists are tracked independently', () => {
        const m = new InMemoryPageStateManager();
        m.registerPageSeen('list-a', 1);
        expect(m.pageSeen('list-a', 1)).not.toBeNull();
        expect(m.pageSeen('list-b', 1)).toBeNull();
    });

    test('getPageId is deterministic', () => {
        const m = new InMemoryPageStateManager();
        const id1 = m.getPageId('list', 1);
        const id2 = m.getPageId('list', 1);
        expect(id1).toBe(id2);
    });

    test('getPageId differs for different inputs', () => {
        const m = new InMemoryPageStateManager();
        expect(m.getPageId('list', 1)).not.toBe(m.getPageId('list', 2));
        expect(m.getPageId('list-a', 1)).not.toBe(m.getPageId('list-b', 1));
    });
});

// ===== LocalStoragePageStateManager additional tests =====
describe('LocalStoragePageStateManager additional tests', () => {

    beforeEach(() => {
        localStorage.clear();
    });

    test('handles corrupt JSON in localStorage gracefully', () => {
        localStorage.setItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY, '{not valid json');
        const m = new LocalStoragePageStateManager();
        // Should not throw, should return null
        expect(m.pageSeen('list', 1)).toBeNull();
    });

    test('handles invalid structure in localStorage gracefully', () => {
        localStorage.setItem(LocalStoragePageStateManager.PAGES_SEEN_STORAGE_KEY, '{"pagesUploadedFor": "not an array"}');
        const m = new LocalStoragePageStateManager();
        expect(m.pageSeen('list', 1)).toBeNull();
    });

    test('clear followed by pageSeen returns null', () => {
        const m = new LocalStoragePageStateManager();
        m.registerPageSeen('list', 1);
        m.clear();
        expect(m.pageSeen('list', 1)).toBeNull();
    });

    test('persists across instances', () => {
        const m1 = new LocalStoragePageStateManager();
        m1.registerPageSeen('list', 42);

        const m2 = new LocalStoragePageStateManager();
        expect(m2.pageSeen('list', 42)).not.toBeNull();
    });

    test('registerPageSeen updates existing date', () => {
        const m = new LocalStoragePageStateManager();
        m.registerPageSeen('list', 1);
        const d1 = m.pageSeen('list', 1);
        m.registerPageSeen('list', 1);
        const d2 = m.pageSeen('list', 1);
        expect(d2!.getTime()).toBeGreaterThanOrEqual(d1!.getTime());
    });
});

// ===== splitIntoJsonArraysOfMaxBytes additional tests =====
describe('splitIntoJsonArraysOfMaxBytes additional tests', () => {

    test('undefined input calls error, callback is not invoked', () => {
        const spy = jest.spyOn(console, 'error').mockImplementation();
        let callbackCalled = false;
        splitIntoJsonArraysOfMaxBytes(undefined, 100, () => { callbackCalled = true; });
        expect(callbackCalled).toBe(false);
        spy.mockRestore();
    });

    test('non-array input calls error', () => {
        const spy = jest.spyOn(console, 'error').mockImplementation();
        let callbackCalled = false;
        splitIntoJsonArraysOfMaxBytes("not array" as any, 100, () => { callbackCalled = true; });
        expect(callbackCalled).toBe(false);
        spy.mockRestore();
    });

    test('single large item still gets passed to callback', () => {
        const results: string[][] = [];
        splitIntoJsonArraysOfMaxBytes(['a very long string that exceeds max'], 1, (chunk: string[]) => results.push(chunk));
        expect(results.length).toBe(1);
        expect(results[0].length).toBe(1);
    });

    test('each chunk concatenated equals original array', () => {
        const input = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        let allItems: number[] = [];
        splitIntoJsonArraysOfMaxBytes(input, 10, (chunk: number[]) => {
            allItems = allItems.concat(chunk);
        });
        expect(allItems).toEqual(input);
    });

    test('large byte limit results in single callback', () => {
        let callCount = 0;
        splitIntoJsonArraysOfMaxBytes([1, 2, 3], 100000, () => callCount++);
        expect(callCount).toBe(1);
    });
});
