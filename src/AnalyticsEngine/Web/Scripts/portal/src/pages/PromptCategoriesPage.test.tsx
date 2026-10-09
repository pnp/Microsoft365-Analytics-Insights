import { beforeEach, describe, expect, it, vi } from 'vitest';
import { act, screen, waitFor, within } from '@testing-library/react';
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
  it('explains both modes and supervising examples on hover without assigning a default mode', async () => {
    const user = userEvent.setup();
    renderWithProvider(<PromptCategoriesPage />);
    const info = await screen.findByRole('img', { name: 'About directing and supervising, category 1' });
    await user.hover(info);
    const tooltip = await screen.findByRole('tooltip');
    expect(within(tooltip).getByText(/Directing: you specify the task/)).toBeInTheDocument();
    expect(within(tooltip).getByText(/Investigate these failures, implement a fix/)).toBeInTheDocument();
    expect(within(tooltip).getByText(/Research the options, compare them/)).toBeInTheDocument();
    expect(within(tooltip).getByText(/the AI does not detect this behaviour/)).toBeInTheDocument();
    expect(screen.getByLabelText('Optional human mode metadata, category 1')).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Save taxonomy and opt-in' })).toBeDisabled();
    expect(savePromptCategories).not.toHaveBeenCalled();
  });

  it('makes the translated mode explanation available on keyboard focus', async () => {
    await loadCatalog('es');
    renderWithProvider(<PromptCategoriesPage />, { language: 'es' });
    const info = await screen.findByRole('img', { name: 'Acerca de la dirección y la supervisión, categoría 1' });
    expect(info).toHaveAttribute('tabindex', '0');
    act(() => info.focus());
    const tooltip = await screen.findByRole('tooltip');
    expect(info).toHaveFocus();
    expect(within(tooltip).getByText(/Dirección: usted define la tarea/)).toBeInTheDocument();
    expect(within(tooltip).getByText(/Investiga estos fallos/)).toBeInTheDocument();
    expect(within(tooltip).getByText(/la IA no detecta este comportamiento/)).toBeInTheDocument();
  }, 30000);

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

  it('blocks saving while a field is invalid, explains why, and a confirmed reset calls the server', async () => {
    const user = userEvent.setup();
    renderWithProvider(<PromptCategoriesPage />);
    const id = await screen.findByLabelText('Stable category id, category 1');
    await user.clear(id);
    await user.type(id, 'Bad Id');
    expect(screen.getByText(/lowercase letters, digits or hyphens/)).toBeInTheDocument();
    expect(screen.getByText('Fix the highlighted fields before saving.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save taxonomy and opt-in' })).toBeDisabled();
    await user.clear(id);
    await user.type(id, 'other');
    expect(screen.getAllByText('Another category already uses this id.').length).toBeGreaterThan(0);
    await user.click(screen.getByRole('button', { name: 'Discard changes' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save taxonomy and opt-in' })).toBeDisabled());
    await user.click(screen.getByRole('button', { name: 'Reset defaults and disable' }));
    expect(resetPromptCategories).not.toHaveBeenCalled();
    await user.click(await screen.findByText('Reset and disable', { selector: 'button, button *' }));
    await waitFor(() => expect(resetPromptCategories).toHaveBeenCalledTimes(1));
  });

  it('shows a distinct storage error with retry instead of a generic message', async () => {
    const user = userEvent.setup();
    vi.mocked(fetchPromptCategoryAdmin).mockRejectedValueOnce(Object.assign(new Error('x'), { name: 'PromptCategoryApiError', code: 'storageTimeout' }));
    renderWithProvider(<PromptCategoriesPage />);
    expect(await screen.findByText(/did not answer in time/)).toBeInTheDocument();
    expect(screen.queryByText(/Check the category ids, names/)).not.toBeInTheDocument();
    await user.click(screen.getAllByRole('button', { name: 'Retry' })[0]);
    expect(await screen.findByLabelText('Enable Foundry prompt categorisation')).toBeInTheDocument();
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
