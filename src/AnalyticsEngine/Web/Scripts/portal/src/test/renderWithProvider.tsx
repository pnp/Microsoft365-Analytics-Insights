import type { ReactElement } from 'react';
import { render, type RenderOptions, type RenderResult } from '@testing-library/react';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';

/**
 * Render a component inside the same FluentProvider the real app uses (see src/main.tsx).
 *
 * Fluent v9 components read theme tokens and portal context from the provider, and Popover/Tooltip
 * render into a portal that is parented off it. Rendering them bare "works" but logs noisy context
 * warnings and can misplace portalled content, so every UI test goes through here.
 */
export function renderWithProvider(ui: ReactElement, options?: RenderOptions): RenderResult {
  return render(ui, {
    wrapper: ({ children }) => (
      <FluentProvider theme={webLightTheme}>{children}</FluentProvider>
    ),
    ...options,
  });
}
