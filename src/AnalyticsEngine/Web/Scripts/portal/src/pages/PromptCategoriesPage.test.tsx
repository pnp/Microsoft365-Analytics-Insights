import { beforeEach, describe, expect, it, vi } from 'vitest';
import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import { fetchPromptCategoryAdmin, fetchPromptCategoryRuns, resetPromptCategories, savePromptCategories } from '../api/promptCategoriesApi';
import PromptCategoriesPage from './PromptCategoriesPage';

vi.mock('../api/promptCategoriesApi', () => ({
  fetchPromptCategoryAdmin: vi.fn(), fetchPromptCategoryRuns: vi.fn(),
  resetPromptCategories: vi.fn(), savePromptCategories: vi.fn(),
}));

const configuration = () => ({
  enabled: false, maxPromptsPerCycle: 1000, version: 'synthetic-version',
  categories: [
    { id: 'analysis', name: 'Analysis', description: 'Analyse information, reason or compare options.', humanMode: null,
      nameKey: 'promptCategories.defaults.analysis.name', descriptionKey: 'promptCategories.defaults.analysis.description' },
    { id: 'other', name: 'Other', description: 'A goal that does not match another category.', humanMode: null },
  ],
});
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(fetchPromptCategoryAdmin).mockResolvedValue({ configuration: configuration(), storageAvailable: true, backendConfigured: true });
  vi.mocked(fetchPromptCategoryRuns).mockResolvedValue([]);
  vi.mocked(savePromptCategories).mockImplementation(async config => config);
  vi.mocked(resetPromptCategories).mockResolvedValue(configuration());
});

describe('prompt categorisation editor', () => {
  it('starts disabled, explains the new data flow, and saves an explicit opt-in', async () => {
    const user = userEvent.setup();
    renderWithProvider(<PromptCategoriesPage />);
    const enabled = await screen.findByLabelText('Enable Foundry prompt categorisation');
    expect(enabled).not.toBeChecked();
    expect(screen.getByText(/Responses are never sent/)).toBeInTheDocument();
    await user.click(enabled);
    expect(screen.getByText(/Changes are not active until you save/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Save taxonomy and opt-in' }));
    await waitFor(() => expect(savePromptCategories).toHaveBeenCalledWith(expect.objectContaining({ enabled: true })));
    expect(await screen.findByText('Configuration saved.')).toBeInTheDocument();
  });

  it('does not let a memory fallback enable or save and resets to disabled defaults', async () => {
    vi.mocked(fetchPromptCategoryAdmin).mockResolvedValue({ configuration: configuration(), storageAvailable: false, backendConfigured: false });
    renderWithProvider(<PromptCategoriesPage />);
    expect(await screen.findByLabelText('Enable Foundry prompt categorisation')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save taxonomy and opt-in' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Reset defaults and disable' })).toBeDisabled();
  });

  it('allows an existing opt-in to be disabled when backend settings are removed', async () => {
    const config = configuration();
    config.enabled = true;
    vi.mocked(fetchPromptCategoryAdmin).mockResolvedValue({ configuration: config, storageAvailable: true, backendConfigured: false });
    renderWithProvider(<PromptCategoriesPage />);
    const enabled = await screen.findByLabelText('Enable Foundry prompt categorisation');
    expect(enabled).toBeEnabled();
    expect(screen.getByDisplayValue('other')).toBeDisabled();
    expect(screen.getByDisplayValue('Other')).toBeDisabled();
    await userEvent.setup().click(enabled);
    expect(enabled).not.toBeChecked();
    expect(enabled).toBeDisabled();
    await userEvent.setup().click(screen.getByRole('button', { name: 'Save taxonomy and opt-in' }));
    await waitFor(() => expect(savePromptCategories).toHaveBeenCalledWith(expect.objectContaining({ enabled: false })));
  });

  it('translates preset names in Spanish and preserves custom customer category names', async () => {
    await loadCatalog('es');
    const config = configuration();
    config.categories.push({ id: 'contoso', name: 'Contoso Καλημέρα', description: 'Synthetic customer wording', humanMode: null });
    vi.mocked(fetchPromptCategoryAdmin).mockResolvedValue({ configuration: config, storageAvailable: true, backendConfigured: true });
    renderWithProvider(<PromptCategoriesPage />, { language: 'es' });
    expect(await screen.findByDisplayValue('Análisis')).toBeInTheDocument();
    expect(screen.getByDisplayValue('Contoso Καλημέρα')).toBeInTheDocument();
    expect(screen.getByText(/Nunca se envían respuestas/)).toBeInTheDocument();
  }, 30000);
});
