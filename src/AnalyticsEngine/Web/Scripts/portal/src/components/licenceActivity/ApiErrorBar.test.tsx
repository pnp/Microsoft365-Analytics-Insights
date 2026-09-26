import { describe, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';

import { LicenceActivityApiError } from '../../api/licenceActivityApi';
import { loadCatalog } from '../../i18n';
import { renderWithProvider } from '../../test/renderWithProvider';
import ApiErrorBar from './ApiErrorBar';

describe('ApiErrorBar', () => {
  it('uses a translated retry label by default in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <ApiErrorBar error={new Error('Error de prueba')} fallback="Error de prueba" onRetry={vi.fn()} />,
      { language: 'es' },
    );

    expect(await screen.findByRole('button', { name: 'Intentarlo de nuevo' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Try again' })).not.toBeInTheDocument();
  });

  it('uses a translated refresh label for expired snapshots in Spanish', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <ApiErrorBar
        error={new LicenceActivityApiError('expired', 410, 'Actualice el informe.')}
        fallback="Error de prueba"
        onRetry={vi.fn()}
      />,
      { language: 'es' },
    );

    expect(await screen.findByRole('button', { name: 'Actualizar' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Refresh' })).not.toBeInTheDocument();
  });
});
