import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Suspense } from 'react';
import { screen } from '@testing-library/react';
import { renderWithProvider } from './test/renderWithProvider';
import RouteErrorBoundary from './components/RouteErrorBoundary';
import { lazyWithReload } from './lazyWithReload';

const reload = vi.fn();

/**
 * A window whose storage throws on access, as it does where site data is blocked: reading
 * `window.sessionStorage` itself raises `SecurityError`, before any method is called.
 */
function blockStorage(): void {
  const location = { ...window.location, reload };
  const denied = () => {
    throw new DOMException('The operation is insecure.', 'SecurityError');
  };
  vi.stubGlobal('window', new Proxy(window, {
    get: (target, key) => {
      if (key === 'location') return location;
      if (key === 'sessionStorage') return denied();
      return Reflect.get(target, key, target);
    },
  }));
  vi.stubGlobal('sessionStorage', new Proxy(sessionStorage, { get: denied }));
}

function renderRoute(Page: ReturnType<typeof lazyWithReload>) {
  return renderWithProvider(
    <RouteErrorBoundary resetKey="/insights/web-activity">
      <Suspense fallback={<span>Loading</span>}>
        <Page />
      </Suspense>
    </RouteErrorBoundary>,
  );
}

beforeEach(() => {
  reload.mockReset();
  vi.spyOn(console, 'error').mockImplementation(() => {});
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  sessionStorage.clear();
});

describe('lazyWithReload where storage is blocked', () => {
  it('still shows a page whose chunk loaded', async () => {
    blockStorage();
    const Page = lazyWithReload(() => Promise.resolve({ default: () => <p>Contoso web activity</p> }));

    renderRoute(Page);

    expect(await screen.findByText('Contoso web activity')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('reports a missing chunk instead of reloading with nothing to stop a loop', async () => {
    blockStorage();
    const Page = lazyWithReload(() =>
      Promise.reject(new Error('Failed to fetch dynamically imported module: https://contoso.example/assets/WebActivityPage-00000000.js')),
    );

    renderRoute(Page);

    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to fetch dynamically imported module');
    expect(reload).not.toHaveBeenCalled();
  });
});
