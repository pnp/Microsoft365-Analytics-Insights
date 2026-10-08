import { beforeEach, expect, it, vi } from 'vitest';
import { apiFetch } from './http';
import { fetchPromptCategoryAdmin, resetPromptCategories, savePromptCategories } from './promptCategoriesApi';
import { loadCatalog, setActiveLanguage } from '../i18n';
vi.mock('./http', () => ({ apiFetch: vi.fn() }));
beforeEach(() => { vi.clearAllMocks(); setActiveLanguage('en'); });

it('uses the same-origin helper and POSTs opt-in and reset', async () => {
  vi.mocked(apiFetch).mockImplementation(async () => new Response('{}', { status: 200 }));
  const config = { enabled: true, maxPromptsPerCycle: 100, version: '', categories: [] };
  await savePromptCategories(config);
  expect(apiFetch).toHaveBeenCalledWith(expect.stringContaining('/api/PromptCategories'), expect.objectContaining({
    method: 'POST', body: JSON.stringify(config),
  }));
  await resetPromptCategories();
  expect(apiFetch).toHaveBeenLastCalledWith(expect.stringContaining('/reset'), expect.objectContaining({ method: 'POST' }));
});

it('never exposes server or model text in a Spanish error', async () => {
  await loadCatalog('es');
  setActiveLanguage('es');
  vi.mocked(apiFetch).mockResolvedValue(new Response('untrusted model or prompt content', { status: 503 }));
  await expect(fetchPromptCategoryAdmin()).rejects.toThrow(/No se pudo cargar/);
});
