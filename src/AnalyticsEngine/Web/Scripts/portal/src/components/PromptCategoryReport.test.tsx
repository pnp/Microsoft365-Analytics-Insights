import { beforeEach, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import { fetchPromptCategoryReport } from '../api/promptCategoriesApi';
import PromptCategoryReport from './PromptCategoryReport';

vi.mock('../api/promptCategoriesApi', () => ({ fetchPromptCategoryReport: vi.fn() }));
beforeEach(() => vi.clearAllMocks());

it('shows only the selected immutable taxonomy and labels the sample and unverified accuracy', async () => {
  vi.mocked(fetchPromptCategoryReport).mockResolvedValue({
    versions: ['version-a', 'version-b'], version: 'version-a',
    categories: [{ id: 'custom', name: 'Contoso Καλημέρα', description: 'Synthetic goal', humanMode: null }],
    mix: [{ categoryId: 'custom', prompts: 1234 }, { categoryId: 'not-classified', prompts: 20 }],
    trend: [{ categoryId: 'custom', weekStart: '2026-01-05T00:00:00Z', prompts: 1234 }],
  });

  renderWithProvider(<PromptCategoryReport months={3} />);
  expect(await screen.findByText('Contoso Καλημέρα: 1,234 Prompts')).toBeInTheDocument();
  expect(screen.getByText('Not classified: 20 Prompts')).toBeInTheDocument();
  expect(screen.getByText(/Real-model accuracy has not been verified/)).toBeInTheDocument();
  expect(screen.getByText(/at least 10 distinct people/)).toBeInTheDocument();
  expect(screen.getByLabelText('Taxonomy version')).toHaveValue('version-a');
  expect(fetchPromptCategoryReport).toHaveBeenCalledWith(3, undefined);
});

it('keeps an unavailable selection explicit so the reader can choose the sole eligible version', async () => {
  vi.mocked(fetchPromptCategoryReport).mockImplementation(async (_months, version) => ({
    versions: ['version-b'], version: version ?? 'version-a', categories: [], mix: [], trend: [],
  }));
  renderWithProvider(<PromptCategoryReport months={1} />);
  const select = await screen.findByLabelText('Taxonomy version');
  expect(select).toHaveValue('version-a');
  expect(screen.getByRole('option', { name: /version-a.*No eligible groups/ })).toBeInTheDocument();
  await userEvent.setup().selectOptions(select, 'version-b');
  await waitFor(() => expect(fetchPromptCategoryReport).toHaveBeenCalledWith(1, 'version-b'));
});

it('never translates administrator names that happen to match server placeholders', async () => {
  await loadCatalog('es');
  vi.mocked(fetchPromptCategoryReport).mockResolvedValue({
    versions: ['version-a'], version: 'version-a',
    categories: [
      { id: 'custom-a', name: '(unknown)', description: 'Synthetic goal A', humanMode: null },
      { id: 'custom-b', name: '(none)', description: 'Synthetic goal B', humanMode: null },
    ],
    mix: [{ categoryId: 'custom-a', prompts: 12 }, { categoryId: 'custom-b', prompts: 15 }],
    trend: [
      { categoryId: 'custom-a', weekStart: '2026-01-05T00:00:00Z', prompts: 12 },
      { categoryId: 'custom-b', weekStart: '2026-01-05T00:00:00Z', prompts: 15 },
    ],
  });
  renderWithProvider(<PromptCategoryReport months={3} />, { language: 'es' });
  await screen.findByRole('option', { name: 'version-a' });
  expect(screen.getAllByText('(unknown)')).toHaveLength(2);
  expect(screen.getAllByText('(none)')).toHaveLength(2);
  expect(screen.queryByText('(desconocido)')).not.toBeInTheDocument();
  expect(screen.queryByText('(ninguno)')).not.toBeInTheDocument();
}, 30000);
