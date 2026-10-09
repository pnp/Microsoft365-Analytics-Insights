import { beforeEach, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
  const row = (await screen.findByText('Fallo del servicio del modelo')).closest('tr')!;
  const cells = within(row).getAllByRole('cell').map(c => c.textContent);
  expect(cells.slice(2)).toEqual(['1234', '1200', '100', '34', '0', '34', '10.000 / 1000', '125']);
  expect(screen.getByRole('columnheader', { name: 'Intentos HTTP' })).toBeInTheDocument();
  expect(screen.queryByText(/untrusted synthetic model output/)).not.toBeInTheDocument();
}, 30000);

it('shows a distinct, translated error with a retry that reloads', async () => {
  vi.mocked(fetchPromptCategoryRuns).mockRejectedValueOnce(Object.assign(new Error('raw server text'), { name: 'PromptCategoryApiError', code: 'storageTimeout' }));
  vi.mocked(fetchPromptCategoryRuns).mockResolvedValueOnce([]);
  renderWithProvider(<PromptCategoryRuns active />);
  expect(await screen.findByText(/did not answer in time/)).toBeInTheDocument();
  expect(screen.queryByText(/raw server text/)).not.toBeInTheDocument();
  await userEvent.setup().click(screen.getAllByRole('button', { name: 'Retry' })[0]);
  expect(await screen.findByText('No import cycles have recorded counters yet.')).toBeInTheDocument();
});

it('does not fetch counters when durable storage is unavailable', () => {
  renderWithProvider(<PromptCategoryRuns active storageAvailable={false} />);
  expect(fetchPromptCategoryRuns).not.toHaveBeenCalled();
  expect(screen.getByText('Import counters need durable Azure Table storage.')).toBeInTheDocument();
});

it('shows a loading state, then an empty message when no cycle has recorded counters', async () => {
  let resolve!: (value: never[]) => void;
  vi.mocked(fetchPromptCategoryRuns).mockReturnValue(new Promise(r => { resolve = r; }));
  renderWithProvider(<PromptCategoryRuns active />);
  expect(screen.getByText('Loading import counters…')).toBeInTheDocument();
  expect(screen.queryByText('No import cycles have recorded counters yet.')).not.toBeInTheDocument();
  resolve([]);
  expect(await screen.findByText('No import cycles have recorded counters yet.')).toBeInTheDocument();
  expect(screen.queryByText('Loading import counters…')).not.toBeInTheDocument();
});

it('does not request administrative diagnostics when inactive', () => {
  renderWithProvider(<PromptCategoryRuns active={false} />);
  expect(fetchPromptCategoryRuns).not.toHaveBeenCalled();
});
