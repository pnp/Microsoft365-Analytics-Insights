import { DuplicateClickHandler } from '../src/DuplicateClickHandler';
import { ClickData } from '../src/Definitions';

describe('DuplicateClickHandler', () => {

    let handler: DuplicateClickHandler;
    let callCount: number;
    const callback = () => callCount++;

    beforeEach(() => {
        handler = new DuplicateClickHandler();
        callCount = 0;
    });

    test('first click always fires callback', () => {
        const click: ClickData = { linkText: 'Link', altText: 'Alt', classNames: 'cls', href: 'http://test.com' };
        handler.registerClick(click, callback);
        expect(callCount).toBe(1);
    });

    test('identical click immediately after is suppressed as duplicate', () => {
        const click: ClickData = { linkText: 'Link', altText: 'Alt', classNames: 'cls', href: 'http://test.com' };
        handler.registerClick(click, callback);
        handler.registerClick(click, callback);
        expect(callCount).toBe(1);
    });

    test('different linkText is not a duplicate', () => {
        const click1: ClickData = { linkText: 'Link1', altText: 'Alt', classNames: 'cls', href: 'http://test.com' };
        const click2: ClickData = { linkText: 'Link2', altText: 'Alt', classNames: 'cls', href: 'http://test.com' };
        handler.registerClick(click1, callback);
        handler.registerClick(click2, callback);
        expect(callCount).toBe(2);
    });

    test('different href is not a duplicate', () => {
        const click1: ClickData = { linkText: 'Link', altText: 'Alt', classNames: 'cls', href: 'http://a.com' };
        const click2: ClickData = { linkText: 'Link', altText: 'Alt', classNames: 'cls', href: 'http://b.com' };
        handler.registerClick(click1, callback);
        handler.registerClick(click2, callback);
        expect(callCount).toBe(2);
    });

    test('different altText is not a duplicate', () => {
        const click1: ClickData = { linkText: 'Link', altText: 'Alt1', classNames: 'cls', href: 'http://test.com' };
        const click2: ClickData = { linkText: 'Link', altText: 'Alt2', classNames: 'cls', href: 'http://test.com' };
        handler.registerClick(click1, callback);
        handler.registerClick(click2, callback);
        expect(callCount).toBe(2);
    });

    test('different classNames is not a duplicate', () => {
        const click1: ClickData = { linkText: 'Link', altText: 'Alt', classNames: 'cls-a', href: 'http://test.com' };
        const click2: ClickData = { linkText: 'Link', altText: 'Alt', classNames: 'cls-b', href: 'http://test.com' };
        handler.registerClick(click1, callback);
        handler.registerClick(click2, callback);
        expect(callCount).toBe(2);
    });

    test('null classNames on both clicks is treated as duplicate', () => {
        const click: ClickData = { linkText: 'Link', altText: 'Alt', classNames: null, href: 'http://test.com' };
        handler.registerClick(click, callback);
        handler.registerClick(click, callback);
        expect(callCount).toBe(1);
    });

    test('same click after a delay fires callback', async () => {
        const click: ClickData = { linkText: 'Link', altText: 'Alt', classNames: 'cls', href: 'http://test.com' };
        handler.registerClick(click, callback);
        await new Promise(r => setTimeout(r, 1100));
        handler.registerClick(click, callback);
        expect(callCount).toBe(2);
    });

    test('three rapid identical clicks only fires once', () => {
        const click: ClickData = { linkText: 'Link', altText: 'Alt', classNames: 'cls', href: 'http://test.com' };
        handler.registerClick(click, callback);
        handler.registerClick(click, callback);
        handler.registerClick(click, callback);
        expect(callCount).toBe(1);
    });

    test('alternating different clicks all fire', () => {
        const clickA: ClickData = { linkText: 'A', altText: '', classNames: '', href: 'http://a.com' };
        const clickB: ClickData = { linkText: 'B', altText: '', classNames: '', href: 'http://b.com' };
        handler.registerClick(clickA, callback);
        handler.registerClick(clickB, callback);
        handler.registerClick(clickA, callback);
        handler.registerClick(clickB, callback);
        expect(callCount).toBe(4);
    });
});
