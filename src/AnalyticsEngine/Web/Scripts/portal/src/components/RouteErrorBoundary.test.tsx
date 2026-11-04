import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Suspense, type ReactNode } from 'react';
import { fireEvent, screen } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import RouteErrorBoundary from './RouteErrorBoundary';
import { lazyWithReload } from '../lazyWithReload';

function BrokenPage(): ReactNode {
  throw new Error('Synthetic render failure');
}

function Shell({ resetKey, children }: { resetKey: string; children: ReactNode }) {
  return (
    <>
      <nav>Contoso navigation</nav>
      <RouteErrorBoundary resetKey={resetKey}>
        <Suspense fallback={<span>Loading</span>}>{children}</Suspense>
      </RouteErrorBoundary>
    </>
  );
}

beforeEach(() => {
  // React reports every error a boundary catches; the noise says nothing these assertions do not.
  vi.spyOn(console, 'error').mockImplementation(() => {});
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  sessionStorage.clear();
});

describe('RouteErrorBoundary', () => {
  it('replaces only the failed page, says what happened and offers a real reload', () => {
    renderWithProvider(<Shell resetKey="/insights/dlp"><BrokenPage /></Shell>);

    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('This page could not be shown');
    expect(alert).toHaveTextContent('Details: Synthetic render failure');
    expect(screen.getByText('Contoso navigation')).toBeInTheDocument();

    const reload = vi.fn();
    const location = { ...window.location, reload };
    vi.stubGlobal('window', new Proxy(window, {
      get: (target, key) => (key === 'location' ? location : Reflect.get(target, key, target)),
    }));
    fireEvent.click(screen.getByRole('button', { name: 'Reload' }));
    expect(reload).toHaveBeenCalledOnce();
  });

  it('says it in Spanish to a Spanish reader', async () => {
    await loadCatalog('es');
    renderWithProvider(<Shell resetKey="/insights/dlp"><BrokenPage /></Shell>, { language: 'es' });

    expect(screen.getByRole('alert')).toHaveTextContent('No se ha podido mostrar esta página');
    expect(screen.getByRole('alert')).toHaveTextContent('Detalles: Synthetic render failure');
    expect(screen.getByRole('button', { name: 'Volver a cargar' })).toBeInTheDocument();
  });

  it('catches a page chunk that still cannot be loaded after the automatic reload', async () => {
    // The one automatic reload has already happened, so lazyWithReload now surfaces the failure.
    sessionStorage.setItem('portal:chunk-reloaded', '1');
    const MissingChunk = lazyWithReload(() =>
      Promise.reject(new Error('Failed to fetch dynamically imported module: https://contoso.example/assets/HealthPage-00000000.js')),
    );

    renderWithProvider(<Shell resetKey="/admin/health"><MissingChunk /></Shell>);

    expect(await screen.findByRole('alert')).toHaveTextContent('Failed to fetch dynamically imported module');
    expect(screen.getByText('Contoso navigation')).toBeInTheDocument();
  });

  it('shows the next page normally once the reader moves to it', () => {
    const { rerender } = renderWithProvider(<Shell resetKey="/insights/dlp"><BrokenPage /></Shell>);
    expect(screen.getByRole('alert')).toBeInTheDocument();

    rerender(<Shell resetKey="/insights/overview"><p>Contoso overview</p></Shell>);

    expect(screen.getByText('Contoso overview')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('leaves a page that renders untouched', () => {
    renderWithProvider(<Shell resetKey="/insights/overview"><p>Contoso overview</p></Shell>);

    expect(screen.getByText('Contoso overview')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});
