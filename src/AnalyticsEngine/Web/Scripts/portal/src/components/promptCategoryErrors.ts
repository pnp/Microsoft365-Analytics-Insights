import { PortalPermissionError, SessionExpiredError } from '../api/http';
import type { PromptCategoryErrorCode } from '../api/promptCategoriesApi';
import type { TranslationKey } from '../i18n';

/** Duck-typed so a component test can mock the api module without also mocking its error class. */
function codeOf(error: unknown): PromptCategoryErrorCode | null {
  const candidate = error as { name?: unknown; code?: unknown } | null;
  return candidate?.name === 'PromptCategoryApiError' && typeof candidate.code === 'string'
    ? candidate.code as PromptCategoryErrorCode : null;
}

const CODE_KEYS: Record<PromptCategoryErrorCode, TranslationKey> = {
  storageUnavailable: 'promptCategories.error.storageUnavailable',
  storageTimeout: 'promptCategories.error.storageTimeout',
  storageNotConfigured: 'promptCategories.error.storageNotConfigured',
  invalidTaxonomy: 'promptCategories.error.invalidTaxonomy',
  storedConfigurationInvalid: 'promptCategories.error.storedConfigurationInvalid',
  databaseNotUpgraded: 'promptCategories.error.databaseNotUpgraded',
  reportUnavailable: 'promptCategories.error.reportUnavailable',
  clientTimeout: 'promptCategories.error.clientTimeout',
};

/** The portal's wording for a failed call. Server text is never shown; only the stable code picks the sentence. */
export function promptCategoryErrorKey(error: unknown): TranslationKey {
  const code = codeOf(error);
  return code ? CODE_KEYS[code] : 'promptCategories.error';
}

/** A permission or session error already carries its own translated sentence. */
export function promptCategoryErrorText(error: unknown, t: (key: TranslationKey) => string): string {
  if (error instanceof PortalPermissionError || error instanceof SessionExpiredError) return error.message;
  return t(promptCategoryErrorKey(error));
}

export function isStoredConfigurationInvalid(error: unknown): boolean {
  return codeOf(error) === 'storedConfigurationInvalid';
}
