import { beforeEach, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import { fetchPromptCategoryRuns } from '../api/promptCategoriesApi';
import PromptCategoryRuns from './PromptCategoryRuns';

vi.mock('../api/promptCategoriesApi', () => ({ fetchPromptCategoryRuns: vi.fn() }));
beforeEach(() => vi.clearAllMocks());

it('localises counters and unknown operational reasons without exposing the raw reason', async () => {
  await loadCatalog('es');
  vi.mocked(fetchPromptCategoryRuns).mockResolvedValue([{
    startedUtc: '2000-01-03T00:00:00Z',
    counters: { sent: 1234, httpAttempts: 125, classified: 1200, other: 100, notClassified: 34,
      capped: 0, failed: 34, inputTokens: 10000, outputTokens: 1000,
      reason: 'untrusted synthetic model output', taxonomyVersion: 'synthetic-version' },
  }]);
  renderWithProvider(<PromptCategoryRuns active />, { language: 'es' });
  expect(await screen.findByText(/Enviadas: 1234/)).toBeInTheDocument();
  expect(screen.getByText('Tokens de entrada / salida: 10.000 / 1000')).toBeInTheDocument();
  expect(screen.getByText('Estado: Fallo del servicio del modelo')).toBeInTheDocument();
  expect(screen.getByText('Intentos HTTP: 125')).toBeInTheDocument();
  expect(screen.queryByText(/untrusted synthetic model output/)).not.toBeInTheDocument();
}, 30000);

it('does not request administrative diagnostics when inactive', () => {
  renderWithProvider(<PromptCategoryRuns active={false} />);
  expect(fetchPromptCategoryRuns).not.toHaveBeenCalled();
});
