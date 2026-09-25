import { describe, expect, it } from 'vitest';
import { screen } from '@testing-library/react';

import { loadCatalog } from '../i18n';
import { renderWithProvider } from '../test/renderWithProvider';
import { ImportFailureBar } from './AgentCostsPage';

describe('ImportFailureBar', () => {
  it('labels a failing import in the reader language and still says why it is failing', async () => {
    await loadCatalog('es');
    renderWithProvider(
      <ImportFailureBar kind="copilotStudio" error="403 Forbidden: the runtime account lacks the Power Platform reader role." />,
      { language: 'es' },
    );

    expect(await screen.findByText('La importación de créditos de Copilot Studio está fallando.')).toBeInTheDocument();
    // Once some figures exist this bar is the page's only diagnosis of the failure, so the importer's error stays.
    expect(screen.getByText(/403 Forbidden: the runtime account lacks the Power Platform reader role\./)).toBeInTheDocument();
  });

  it('does the same for the Azure cost import', async () => {
    await loadCatalog('es');
    renderWithProvider(<ImportFailureBar kind="azure" error="Cost Management returned 429 Too Many Requests." />, { language: 'es' });

    expect(await screen.findByText(/Cost Management returned 429 Too Many Requests\./)).toBeInTheDocument();
    expect(screen.queryByText(/import is failing/)).not.toBeInTheDocument();
  });
});
