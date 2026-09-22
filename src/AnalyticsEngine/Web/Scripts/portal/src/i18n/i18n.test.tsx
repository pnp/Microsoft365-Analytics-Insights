import { describe, it, expect, beforeAll, beforeEach, afterEach, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProvider } from '../test/renderWithProvider';
import {
  LANGUAGE_STORAGE_KEY,
  LanguageSwitcher,
  activeLocale,
  browserLanguages,
  catalogFailed,
  clearCatalogFailure,
  detectLanguage,
  formatNumber,
  interpolate,
  interpolateNodes,
  isCatalogLoaded,
  loadCatalog,
  markCatalogFailed as markCatalogFailedForTest,
  matchLanguage,
  plural,
  setActiveLanguage,
  useT,
} from './index';

/**
 * What a visitor actually experiences: the portal opens in their language without being asked, and
 * they can change it from anywhere if it guessed wrong.
 */

beforeAll(async () => {
  // Spanish is fetched as its own chunk in the browser (see catalog/index.ts), and `main.tsx`
  // awaits it before the first render. These tests render directly, so they await it here.
  await loadCatalog('es');
});

describe('Choosing a language', () => {
  it('opens in the browser language when it is one we ship', () => {
    expect(detectLanguage({ storage: undefined, navigator: { languages: ['es-ES', 'en'], language: 'es-ES' } })).toBe('es');
  });

  it('falls back to English for a language we do not ship', () => {
    expect(detectLanguage({ storage: undefined, navigator: { languages: ['fr-FR', 'de'], language: 'fr-FR' } })).toBe('en');
  });

  /**
   * Matching on the primary subtag rather than the full tag. A reader in Mexico or Argentina
   * asking for `es-MX`/`es-419` wants Spanish; giving them English because the region is not Spain
   * would be a worse answer than European Spanish.
   */
  it('gives Spanish to any Spanish region, not only Spain', () => {
    expect(matchLanguage(['es-MX'])).toBe('es');
    expect(matchLanguage(['es-419'])).toBe('es');
    expect(matchLanguage(['ES'])).toBe('es');
  });

  it('prefers a choice the visitor made earlier over the browser preference', () => {
    const storage = { getItem: () => 'en' };
    expect(detectLanguage({ storage, navigator: { languages: ['es-ES'], language: 'es-ES' } })).toBe('en');
  });

  it('survives a browser with site data blocked, where localStorage throws', () => {
    const storage = {
      getItem: () => {
        throw new Error('The operation is insecure.');
      },
    };
    expect(detectLanguage({ storage, navigator: { languages: ['es-ES'], language: 'es-ES' } })).toBe('es');
  });

  it('reads navigator.language when navigator.languages is unavailable', () => {
    expect(browserLanguages({ languages: [] as unknown as readonly string[], language: 'es-ES' })).toEqual(['es-ES']);
  });
});

describe('Substituting values into a translation', () => {
  it('replaces named placeholders', () => {
    expect(interpolate('Showing {count} of {total}', { count: 5, total: 90 })).toBe('Showing 5 of 90');
  });

  /**
   * A missing value leaves `{count}` visible rather than blanking it. A gap in a sentence reads as
   * a rendering glitch and gets lived with; a literal `{count}` on screen gets reported.
   */
  it('leaves an unsupplied placeholder visible', () => {
    expect(interpolate('Showing {count} users', {})).toBe('Showing {count} users');
  });

  it('substitutes elements so a sentence can carry a link without being cut up', () => {
    const parts = interpolateNodes('See {link} for more', { link: 'THE-LINK' });
    expect(parts).toEqual(['See ', 'THE-LINK', ' for more']);
  });

  /**
   * A sentence with a link in it is the normal case for `tNode`, and React warns about every
   * unkeyed element inside an array. Without keys the console fills with warnings on any page
   * that uses it - which trains people to ignore the console, and hides the next real warning.
   */
  it('renders a sentence containing elements without React key warnings', () => {
    const warn = vi.spyOn(console, 'error').mockImplementation(() => {});
    try {
      renderWithProvider(
        <p data-testid="rich">
          {interpolateNodes('Open {page} then {other} to continue', {
            page: <a href="#/admin/health">Service health</a>,
            other: <strong>Profiling</strong>,
          })}
        </p>,
      );
      expect(screen.getByTestId('rich')).toHaveTextContent(
        'Open Service health then Profiling to continue',
      );
      expect(warn).not.toHaveBeenCalled();
    } finally {
      warn.mockRestore();
    }
  });

  /**
   * The substitution regex is global, so a shared instance would carry `lastIndex` between calls
   * and skip the first placeholder of every other sentence.
   */
  it('substitutes the same template correctly twice in a row', () => {
    expect(interpolateNodes('{a} and {b}', { a: '1', b: '2' })).toEqual(['1', ' and ', '2']);
    expect(interpolateNodes('{a} and {b}', { a: '3', b: '4' })).toEqual(['3', ' and ', '4']);
  });

  it('picks the singular form only for exactly one', () => {
    const one = 'common.unit.user.one';
    const other = 'common.unit.user.other';
    expect(plural(1, one, other)).toBe(one);
    expect(plural(0, one, other)).toBe(other);
    expect(plural(2, one, other)).toBe(other);
  });
});

/**
 * Numbers are not decoration. `1,234` is one thousand two hundred and thirty-four in English and
 * one point two three four in Spanish, so a figure formatted in the wrong locale is wrong by a
 * factor of a thousand - on a page used to justify licence spend.
 */
describe('Formatting figures for the chosen language', () => {
  afterEach(() => setActiveLanguage('en'));

  it('groups thousands the English way in English', () => {
    setActiveLanguage('en');
    expect(formatNumber(1234567)).toBe('1,234,567');
  });

  it('groups thousands the Spanish way in Spanish', () => {
    setActiveLanguage('es');
    expect(formatNumber(1234567)).toBe('1.234.567');
  });

  it('uses a comma for the decimal separator in Spanish', () => {
    setActiveLanguage('es');
    expect(formatNumber(12.5, { minimumFractionDigits: 1 })).toBe('12,5');
  });

  /**
   * Percentages are built by hand rather than with `Intl.NumberFormat`'s `style: 'percent'`, so
   * they are invisible to the locale-formatting check - a `.toFixed(1)` contains no locale call.
   * They are also the most-shown figure on several pages, and `12.3%` reads as twelve point three
   * in English and as a thousands-separated number in Spanish.
   */
  it('formats a percentage with the Spanish decimal comma', async () => {
    const { formatPct: kpiPct } = await import('../components/shared/KpiGrid');
    const { formatPct: licencePct } = await import('../components/licenceActivity/format');

    setActiveLanguage('en');
    expect(kpiPct(12.34)).toBe('12.3%');
    expect(licencePct(12.34)).toBe('12.3%');
    expect(kpiPct(12)).toBe('12%');

    setActiveLanguage('es');
    expect(kpiPct(12.34)).toBe('12,3%');
    expect(licencePct(12.34)).toBe('12,3%');
    // A whole number still has no decimal separator to get wrong.
    expect(kpiPct(12)).toBe('12%');
  });

  it('formats a sentiment score with the Spanish decimal comma', async () => {
    const { formatSentiment } = await import('../components/shared/SentimentLight');

    setActiveLanguage('en');
    expect(formatSentiment(0.75)).toContain('0.75');

    setActiveLanguage('es');
    expect(formatSentiment(0.75)).toContain('0,75');
  });
});

function Sample() {
  const t = useT();
  return <span data-testid="sample">{t('app.signOut')}</span>;
}

describe('Switching language in the portal', () => {
  beforeEach(() => {
    window.localStorage.clear();
    vi.restoreAllMocks();
  });

  it('renders the catalog for the language it was given', () => {
    renderWithProvider(<Sample />, { language: 'es' });
    expect(screen.getByTestId('sample')).toHaveTextContent('Cerrar sesi\u00f3n');
  });

  it('relabels the page when the reader picks another language, without a reload', async () => {
    const user = userEvent.setup();
    renderWithProvider(
      <>
        <LanguageSwitcher />
        <Sample />
      </>,
      { language: 'en' },
    );

    expect(screen.getByTestId('sample')).toHaveTextContent('Sign out');

    await user.click(screen.getByRole('button', { name: /Language: English/ }));
    await user.click(await screen.findByRole('menuitemradio', { name: 'Espa\u00f1ol' }));

    await waitFor(() => expect(screen.getByTestId('sample')).toHaveTextContent('Cerrar sesi\u00f3n'));
  });

  /**
   * Named in its own language on purpose. The list has to be legible to someone who cannot read
   * the language the page is currently in - which is the whole reason they are opening it.
   */
  it('names each language in that language', async () => {
    const user = userEvent.setup();
    renderWithProvider(<LanguageSwitcher />, { language: 'es' });

    await user.click(screen.getByRole('button', { name: /Idioma: Espa\u00f1ol/ }));

    expect(await screen.findByRole('menuitemradio', { name: 'English' })).toBeVisible();
    expect(screen.getByRole('menuitemradio', { name: 'Espa\u00f1ol' })).toBeVisible();
  });

  it('remembers the choice for the next visit', async () => {
    const user = userEvent.setup();
    renderWithProvider(<LanguageSwitcher />, { language: 'en' });

    await user.click(screen.getByRole('button', { name: /Language: English/ }));
    await user.click(await screen.findByRole('menuitemradio', { name: 'Espa\u00f1ol' }));

    await waitFor(() => expect(window.localStorage.getItem(LANGUAGE_STORAGE_KEY)).toBe('es'));
  });

  /**
   * `<html lang>` is what a screen reader uses to pick a voice. Left at `en` on a Spanish page it
   * reads Spanish with English phonetics, which is close to unusable.
   */
  it('tells the browser and assistive technology which language the page is in', async () => {
    renderWithProvider(<Sample />, { language: 'es' });
    await waitFor(() => expect(document.documentElement.lang).toBe('es-ES'));
  });
});

/**
 * Only English is bundled; every other language is fetched as its own chunk, so that adding a
 * language costs existing readers nothing. The alternative - bundling every language - would make
 * each new one a permanent download for everybody, and would quietly turn "should we support
 * another language?" into a question about page weight.
 */
describe('Fetching a language', () => {
  it('has English available without fetching anything', () => {
    expect(isCatalogLoaded('en')).toBe(true);
  });

  it('serves the same catalog on a second request rather than fetching again', async () => {
    const first = await loadCatalog('es');
    const second = await loadCatalog('es');
    expect(second).toBe(first);
  });

  it('renders English until a language arrives, rather than nothing at all', () => {
    // A portal that shows a blank page while a translation file downloads is worse than one that
    // shows English for a moment. main.tsx avoids even that on first load by awaiting the catalog,
    // so this is the language-switch and slow-network path.
    renderWithProvider(<Sample />, { language: 'de' as never });
    expect(screen.getByTestId('sample')).toHaveTextContent('Sign out');
  });

  /**
   * A chunk fetch can fail for reasons that pass: an offline moment, or a stale `index.html` still
   * asking for a file the redeploy replaced. When it does, the whole language has to fall back -
   * not just the text. English prose under `<html lang="es-ES">` with Spanish thousands separators
   * is worse than honest English, because a screen reader then reads English with Spanish
   * phonetics and every number is grouped the wrong way round.
   *
   * And it must be recoverable. Marking the language failed and then never asking again would lock
   * the reader out of it for the rest of the session over one transient error - so choosing it
   * from the menu has to count as "try again".
   */
  it('falls the whole language back to English when its catalog cannot be fetched', () => {
    expect(catalogFailed('es')).toBe(false);
    markCatalogFailedForTest('es');
    try {
      renderWithProvider(<Sample />, { language: 'es' });
      expect(screen.getByTestId('sample')).toHaveTextContent('Sign out');
      expect(activeLocale()).toBe('en-GB');
    } finally {
      clearCatalogFailure('es');
      setActiveLanguage('en');
    }
  });

  it('lets the reader try the language again by picking it from the menu', async () => {
    const user = userEvent.setup();
    markCatalogFailedForTest('es');
    try {
      renderWithProvider(
        <>
          <LanguageSwitcher />
          <Sample />
        </>,
        { language: 'es' },
      );

      // Fell back, so the picker shows English and the text is English.
      expect(screen.getByTestId('sample')).toHaveTextContent('Sign out');

      await user.click(screen.getByRole('button', { name: /Language: English/ }));
      await user.click(await screen.findByRole('menuitemradio', { name: 'Espa\u00f1ol' }));

      await waitFor(() =>
        expect(screen.getByTestId('sample')).toHaveTextContent('Cerrar sesi\u00f3n'),
      );
      expect(catalogFailed('es')).toBe(false);
    } finally {
      clearCatalogFailure('es');
      setActiveLanguage('en');
    }
  });
});
