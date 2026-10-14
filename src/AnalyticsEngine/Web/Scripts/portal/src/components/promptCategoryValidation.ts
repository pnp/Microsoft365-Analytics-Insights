import type { PromptCategoryConfiguration } from '../api/promptCategoriesApi';
import type { TranslationKey } from '../i18n';

export const MIN_CATEGORIES = 2;
export const MAX_CATEGORIES = 20;
export const MAX_PROMPTS_CAP = 10000;
export const MAX_NAME_LENGTH = 100;
export const MAX_DESCRIPTION_LENGTH = 500;

// Mirrors PromptCategoryConfiguration.ValidateAndVersion on the server, so a rejected save is the exception.
const ID_PATTERN = /^[a-z][a-z0-9-]{0,39}$/;

export interface CategoryProblems {
  id?: TranslationKey;
  name?: TranslationKey;
  description?: TranslationKey;
}
export interface TaxonomyProblems {
  cap?: TranslationKey;
  count?: TranslationKey;
  categories: CategoryProblems[];
  total: number;
}

export function validateTaxonomy(config: PromptCategoryConfiguration): TaxonomyProblems {
  const problems: TaxonomyProblems = { categories: [], total: 0 };
  const cap = config.maxPromptsPerCycle;
  if (!Number.isInteger(cap) || cap < 1 || cap > MAX_PROMPTS_CAP) problems.cap = 'promptCategories.validation.cap';
  if (config.categories.length < MIN_CATEGORIES || config.categories.length > MAX_CATEGORIES)
    problems.count = 'promptCategories.validation.count';

  const seen = new Map<string, number>();
  config.categories.forEach(c => seen.set(c.id, (seen.get(c.id) ?? 0) + 1));
  config.categories.forEach(c => {
    const row: CategoryProblems = {};
    if (!ID_PATTERN.test(c.id)) row.id = 'promptCategories.validation.idFormat';
    else if (c.id === 'not-classified') row.id = 'promptCategories.validation.idReserved';
    else if ((seen.get(c.id) ?? 0) > 1) row.id = 'promptCategories.validation.idDuplicate';
    if (!c.name.trim() || c.name.length > MAX_NAME_LENGTH) row.name = 'promptCategories.validation.name';
    if (!c.description.trim() || c.description.length > MAX_DESCRIPTION_LENGTH)
      row.description = 'promptCategories.validation.description';
    problems.categories.push(row);
  });
  if (!config.categories.some(c => c.id === 'other')) problems.count = 'promptCategories.validation.other';

  problems.total = (problems.cap ? 1 : 0) + (problems.count ? 1 : 0) +
    problems.categories.reduce((n, r) => n + (r.id ? 1 : 0) + (r.name ? 1 : 0) + (r.description ? 1 : 0), 0);
  return problems;
}

/** What makes two configurations different to an administrator: the server recomputes the version and keys. */
export function configurationSnapshot(config: PromptCategoryConfiguration): string {
  return JSON.stringify([
    config.enabled, config.maxPromptsPerCycle,
    config.categories.map(c => [c.id, c.name, c.description, c.humanMode ?? null]),
  ]);
}
