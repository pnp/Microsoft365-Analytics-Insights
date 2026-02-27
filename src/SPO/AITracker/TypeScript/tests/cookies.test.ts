/**
 * @jest-environment jsdom
 */

import {
    GetSessionCookieVal, SetSessionCookieVal,
    GetLastTrackedPageVal, SetLastTrackedPageVal,
    GetLastPageStatsVal, SetLastPageStatsVal, ClearLastPageStatsVal,
    CleanCookies
} from '../src/Cookies';
import { PageStats } from '../src/Definitions';
import Cookies from 'js-cookie';

describe('Cookies', () => {

    beforeEach(() => {
        // Clear all cookies before each test
        Object.keys(Cookies.get()).forEach(name => Cookies.remove(name));
    });

    describe('Session cookie', () => {
        test('GetSessionCookieVal returns empty string when no cookie set', () => {
            expect(GetSessionCookieVal()).toBe('');
        });

        test('SetSessionCookieVal and GetSessionCookieVal round-trip', () => {
            SetSessionCookieVal('test-session-123');
            expect(GetSessionCookieVal()).toBe('test-session-123');
        });

        test('SetSessionCookieVal overwrites previous value', () => {
            SetSessionCookieVal('session-1');
            SetSessionCookieVal('session-2');
            expect(GetSessionCookieVal()).toBe('session-2');
        });
    });

    describe('Last tracked page cookie', () => {
        test('GetLastTrackedPageVal returns empty string when no cookie set', () => {
            expect(GetLastTrackedPageVal()).toBe('');
        });

        test('SetLastTrackedPageVal and GetLastTrackedPageVal round-trip', () => {
            const url = 'https://contoso.sharepoint.com/sites/test/SitePages/Home.aspx';
            SetLastTrackedPageVal(url);
            expect(GetLastTrackedPageVal()).toBe(url);
        });
    });

    describe('Last page stats cookie', () => {
        test('GetLastPageStatsVal returns null when no cookie set', () => {
            expect(GetLastPageStatsVal()).toBeNull();
        });

        test('SetLastPageStatsVal and GetLastPageStatsVal round-trip', () => {
            const stats: PageStats = {
                pageRequestId: 'req-123',
                secondsOnPage: 42.5,
                url: 'https://contoso.sharepoint.com/sites/test'
            };
            SetLastPageStatsVal(stats);
            const retrieved = GetLastPageStatsVal();
            expect(retrieved).not.toBeNull();
            expect(retrieved!.pageRequestId).toBe('req-123');
            expect(retrieved!.secondsOnPage).toBe(42.5);
            expect(retrieved!.url).toBe('https://contoso.sharepoint.com/sites/test');
        });

        test('GetLastPageStatsVal returns null for corrupt JSON', () => {
            Cookies.set('SPOInsightsLastPageStats', '{invalid json!!!');
            const result = GetLastPageStatsVal();
            expect(result).toBeNull();
        });

        test('SetLastPageStatsVal with null secondsOnPage', () => {
            const stats: PageStats = {
                pageRequestId: 'req-456',
                secondsOnPage: null,
                url: 'https://test.com'
            };
            SetLastPageStatsVal(stats);
            const retrieved = GetLastPageStatsVal();
            expect(retrieved).not.toBeNull();
            expect(retrieved!.secondsOnPage).toBeNull();
        });

        test('ClearLastPageStatsVal removes cookie', () => {
            const stats: PageStats = {
                pageRequestId: 'req-789',
                secondsOnPage: 10,
                url: 'https://test.com'
            };
            SetLastPageStatsVal(stats);
            expect(GetLastPageStatsVal()).not.toBeNull();
            ClearLastPageStatsVal();
            expect(GetLastPageStatsVal()).toBeNull();
        });
    });

    describe('CleanCookies', () => {
        test('removes AI cookies', () => {
            Cookies.set('ai_authUser', 'user1');
            Cookies.set('ai_session', 'sess1');
            Cookies.set('ai_user', 'usr1');
            Cookies.set('SPOInsightsSessionID', 'keep-me');

            CleanCookies();

            expect(Cookies.get('ai_authUser')).toBeUndefined();
            expect(Cookies.get('ai_session')).toBeUndefined();
            expect(Cookies.get('ai_user')).toBeUndefined();
            // Should not remove SPOInsights cookies
            expect(Cookies.get('SPOInsightsSessionID')).toBe('keep-me');
        });
    });
});
