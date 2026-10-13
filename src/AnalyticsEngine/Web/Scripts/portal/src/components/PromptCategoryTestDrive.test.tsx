import { beforeEach, expect, it, vi } from 'vitest';
import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithProvider } from '../test/renderWithProvider';
import { loadCatalog } from '../i18n';
import { testPromptCategory } from '../api/promptCategoriesApi';
import PromptCategoryTestDrive from './PromptCategoryTestDrive';

vi.mock('../api/promptCategoriesApi', () => ({ testPromptCategory: vi.fn() }));
beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(testPromptCategory).mockResolvedValue({
    category: { id: 'analysis', name: 'Analysis', description: 'Synthetic description', humanMode: 'directing',
      nameKey: 'promptCategories.defaults.analysis.name' },
    version: 'synthetic-version', inputTokens: 100, outputTokens: 10,
  });
});

it('requires an explicit non-empty sample, shows the result, and clears it when text changes', async () => {
  const user = userEvent.setup();
  renderWithProvider(<PromptCategoryTestDrive version="synthetic-version" disabled={false} />);
  const submit = screen.getByRole('button', { name: 'Classify test prompt' });
  expect(submit).toBeDisabled();
  expect(testPromptCategory).not.toHaveBeenCalled();
  const input = screen.getByLabelText('Test prompt');
  expect(input).toHaveAttribute('maxlength', '5000');
  await user.type(input, 'Compare synthetic options.');
  await user.click(submit);
  expect(await screen.findByText('Selected category: Analysis')).toBeInTheDocument();
  expect(testPromptCategory).toHaveBeenCalledExactlyOnceWith('Compare synthetic options.', 'synthetic-version');
  expect(screen.getByText('Administrator-assigned human mode: Directing')).toBeInTheDocument();
  expect(screen.getByText('Tokens used: 100 input / 10 output')).toBeInTheDocument();
  await user.type(input, ' Updated');
  expect(screen.queryByText('Selected category: Analysis')).not.toBeInTheDocument();
});

it('shows pending feedback and prevents duplicate submissions', async () => {
  vi.mocked(testPromptCategory).mockReturnValue(new Promise(() => {}));
  const user = userEvent.setup();
  renderWithProvider(<PromptCategoryTestDrive version="synthetic-version" disabled={false} />);
  await user.type(screen.getByLabelText('Test prompt'), 'Synthetic prompt');
  await user.click(screen.getByRole('button', { name: 'Classify test prompt' }));
  expect(screen.getByRole('button', { name: 'Classify test prompt' })).toBeDisabled();
  expect(screen.getByLabelText('Test prompt')).toBeDisabled();
  expect(screen.getByText('Classifying test prompt…')).toBeInTheDocument();
  expect(testPromptCategory).toHaveBeenCalledTimes(1);
});

it('reports a translated error without showing untrusted model text and allows retry', async () => {
  vi.mocked(testPromptCategory).mockRejectedValueOnce(Object.assign(new Error('untrusted content'), {
    name: 'PromptCategoryApiError', code: 'testRefused',
  }));
  const user = userEvent.setup();
  renderWithProvider(<PromptCategoryTestDrive version="synthetic-version" disabled={false} />);
  await user.type(screen.getByLabelText('Test prompt'), 'Synthetic prompt');
  await user.click(screen.getByRole('button', { name: 'Classify test prompt' }));
  expect(await screen.findByText(/The model refused or filtered this sample/)).toBeInTheDocument();
  expect(screen.queryByText('untrusted content')).not.toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: 'Classify test prompt' }));
  expect(await screen.findByText('Selected category: Analysis')).toBeInTheDocument();
});

it('translates the test panel and preset result in Spanish', async () => {
  await loadCatalog('es');
  const user = userEvent.setup();
  renderWithProvider(<PromptCategoryTestDrive version="synthetic-version" disabled={false} />, { language: 'es' });
  await user.type(screen.getByLabelText('Indicación de prueba'), 'Synthetic prompt');
  await user.click(screen.getByRole('button', { name: 'Clasificar la indicación de prueba' }));
  expect(await screen.findByText('Categoría seleccionada: Análisis')).toBeInTheDocument();
  expect(screen.getByText('Modo humano asignado por el administrador: Dirección')).toBeInTheDocument();
});
