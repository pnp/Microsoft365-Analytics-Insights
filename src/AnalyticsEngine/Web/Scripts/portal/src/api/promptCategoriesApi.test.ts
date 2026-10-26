import { beforeEach, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import { fetchPromptCategoryAdmin, resetPromptCategories, savePromptCategories, testPromptCategory } from './promptCategoriesApi';
import { loadCatalog, setActiveLanguage } from '../i18n';
vi.mock('./http', () => ({ apiFetch: vi.fn() }));
beforeEach(() => { vi.clearAllMocks(); setActiveLanguage('en'); });

it('uses the same-origin helper and POSTs opt-in and reset, each with the revision it was made against', async () => {
  vi.mocked(apiFetch).mockImplementation(async () => new Response('{}', { status: 200 }));
  const config = { enabled: true, maxPromptsPerCycle: 100, version: '', categories: [], revision: 'synthetic-revision' };
  await savePromptCategories(config);
  expect(apiFetch).toHaveBeenCalledWith(expect.stringContaining('/api/PromptCategories'), expect.objectContaining({
    method: 'POST', body: JSON.stringify(config),
  }));
  await resetPromptCategories('synthetic-revision');
  expect(apiFetch).toHaveBeenLastCalledWith(expect.stringContaining('/reset'), expect.objectContaining({
    method: 'POST', body: JSON.stringify({ expectedRevision: 'synthetic-revision' }),
  }));
});

it('reports a save refused because the settings changed since they were loaded by its stable code', async () => {
  vi.mocked(apiFetch).mockResolvedValue(new Response(JSON.stringify({ code: 'configurationChanged', configuration: null }), { status: 409 }));
  await expect(savePromptCategories({ enabled: true, maxPromptsPerCycle: 100, version: '', categories: [], revision: 'stale' }))
    .rejects.toMatchObject({ name: 'PromptCategoryApiError', code: 'configurationChanged' });
});

it('never exposes server or model text in a Spanish error', async () => {
  await loadCatalog('es');
  setActiveLanguage('es');
  vi.mocked(apiFetch).mockResolvedValue(new Response('untrusted model or prompt content', { status: 503 }));
  await expect(fetchPromptCategoryAdmin()).rejects.toThrow(/No se pudo cargar/);
});

it('POSTs a single test prompt and saved version, never in the URL', async () => {
  vi.mocked(apiFetch).mockResolvedValue(new Response('{}', { status: 200 }));
  await testPromptCategory('Synthetic Καλημέρα', 'synthetic-version');
  expect(apiFetch).toHaveBeenCalledWith(expect.stringMatching(/\/api\/PromptCategories\/test$/), expect.objectContaining({
    method: 'POST', body: JSON.stringify({ prompt: 'Synthetic Καλημέρα', version: 'synthetic-version' }),
  }));
});
