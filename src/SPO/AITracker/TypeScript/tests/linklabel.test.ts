/**
 * @jest-environment jsdom
 */

import { linkLabel } from '../src/DataFunctions';

function link(html: string): HTMLAnchorElement {
    const host = document.createElement('div');
    host.innerHTML = html;
    return host.querySelector('a') as HTMLAnchorElement;
}

describe('linkLabel', () => {
    test('a link with text is labelled by its text, exactly as before', () => {
        expect(linkLabel(link('<a href="#" aria-label="Ignored">LEARN MORE </a>'))).toBe('LEARN MORE ');
    });

    test('keeps non-Latin link text as it is', () => {
        expect(linkLabel(link('<a href="#">Καλημέρα κόσμε</a>'))).toBe('Καλημέρα κόσμε');
    });

    test('an icon link without text is labelled by its aria-label', () => {
        expect(linkLabel(link('<a href="https://contoso.sharepoint.com" aria-label="Contoso"><svg></svg></a>'))).toBe('Contoso');
    });

    test('whitespace is not text: falls back to the aria-label', () => {
        expect(linkLabel(link('<a href="#" aria-label="Home">  \n </a>'))).toBe('Home');
    });

    test('then by its title', () => {
        expect(linkLabel(link('<a href="#" title="Site settings"><i class="icon"></i></a>'))).toBe('Site settings');
    });

    test('then by the alt text of its image', () => {
        expect(linkLabel(link('<a href="#"><img src="logo.png" alt="Contoso logo"></a>'))).toBe('Contoso logo');
    });

    test('a link with nothing to go by has no label', () => {
        expect(linkLabel(link('<a href="#"><img src="logo.png"></a>'))).toBe('');
    });
});
