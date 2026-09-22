import type { ReactElement } from 'react';
import { render, type RenderOptions, type RenderResult } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { I18nProvider, type Language } from '../i18n';

export interface PortalRenderOptions extends RenderOptions {
  /**
   * Language to render in. Defaults to English so a test asserting on visible text does not have
   * to care that the portal is translated - only the tests that are *about* translation do.
   */
  language?: Language;
}

/**
 * Render a component inside the same providers the real app uses (see src/main.tsx).
 *
 * Fluent v9 components read theme tokens and portal context from the provider, and Popover/Tooltip
 * render into a portal that is parented off it. Rendering them bare "works" but logs noisy context
 * warnings and can misplace portalled content, so every UI test goes through here.
 *
 * `I18nProvider` is pinned to a language rather than left to detect one: detection reads
 * `navigator.languages`, which is whatever the machine running the tests happens to be configured
 * with, and a suite that passes in London and fails in Madrid is worse than no suite at all.
 */
export function renderWithProvider(ui: ReactElement, options?: PortalRenderOptions): RenderResult {
  const { language = 'en', ...renderOptions } = options ?? {};
  return render(ui, {
    wrapper: ({ children }) => (
      <FluentProvider theme={webLightTheme}>
        <I18nProvider initialLanguage={language}>{children}</I18nProvider>
      </FluentProvider>
    ),
    ...renderOptions,
  });
}
