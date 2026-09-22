import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { HashRouter } from 'react-router-dom';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import App from './App';
import { I18nProvider, detectLanguage, loadCatalog } from './i18n';
import { restoreRouteAfterReauth } from './api/http';
import './index.css';

const container = document.getElementById('root');
if (!container) {
  throw new Error('Root container #root not found');
}

// If an expired session sent the user through sign-in, put them back on the page they were on.
// Must run before HashRouter reads the URL.
restoreRouteAfterReauth();

// The reader's language: an explicit earlier choice, then the browser's preference, then English.
const language = detectLanguage();

/**
 * Wait for the translations before painting.
 *
 * Only English is bundled; anything else is a separate chunk (see `i18n/catalog/index.ts`), so a
 * Spanish reader would otherwise get a frame of English before it arrives. The provider copes with
 * that on its own - it has to, for the language *switch* - but on first load there is nothing
 * useful to show in the meantime, and a page that renders in one language and then changes under
 * the reader looks broken.
 *
 * `loadCatalog` resolves to English rather than rejecting if the chunk cannot be fetched, so a
 * failed download degrades to an English portal instead of a blank page.
 */
void loadCatalog(language).then(() => {
  createRoot(container).render(
    <StrictMode>
      <FluentProvider theme={webLightTheme} style={{ minHeight: '100vh' }}>
        <I18nProvider initialLanguage={language}>
          <HashRouter>
            <App />
          </HashRouter>
        </I18nProvider>
      </FluentProvider>
    </StrictMode>,
  );
});
