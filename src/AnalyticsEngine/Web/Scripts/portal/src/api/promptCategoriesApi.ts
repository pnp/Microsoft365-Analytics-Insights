import { apiFetch } from './http';
import { translateActive } from '../i18n/runtime';

export interface PromptCategoryDefinition {
  id: string;
  name: string;
  description: string;
  humanMode: 'directing' | 'supervising' | null;
  nameKey?: string | null;
  descriptionKey?: string | null;
}
export interface PromptCategoryConfiguration {
  enabled: boolean;
  maxPromptsPerCycle: number;
  version: string;
  categories: PromptCategoryDefinition[];
}
export interface PromptCategoryAdmin {
  configuration: PromptCategoryConfiguration;
  storageAvailable: boolean;
  backendConfigured: boolean;
}
export interface PromptCategoryReport {
  versions: string[];
  version: string | null;
  categories: PromptCategoryDefinition[];
  mix: { categoryId: string; prompts: number }[];
  trend: { categoryId: string; weekStart: string; prompts: number }[];
}
export interface PromptCategoryRun {
  startedUtc: string;
  counters: {
    sent: number; httpAttempts: number; classified: number; other: number; notClassified: number;
    capped: number; failed: number; inputTokens: number; outputTokens: number; reason: string;
    taxonomyVersion?: string | null;
  };
}

/** Stable failure codes the server sends beside a non-2xx status; the portal writes the sentence for each. */
export const PROMPT_CATEGORY_ERROR_CODES = [
  'storageUnavailable', 'storageTimeout', 'storageNotConfigured', 'invalidTaxonomy',
  'storedConfigurationInvalid', 'databaseNotUpgraded', 'reportUnavailable',
] as const;
export type PromptCategoryErrorCode = typeof PROMPT_CATEGORY_ERROR_CODES[number] | 'clientTimeout';

export class PromptCategoryApiError extends Error {
  readonly code: PromptCategoryErrorCode | null;
  constructor(code: PromptCategoryErrorCode | null) {
    super(translateActive('promptCategories.error'));
    this.name = 'PromptCategoryApiError';
    this.code = code;
  }
}

/** The server answers in seconds when storage is down; this is only the backstop for a dead connection. */
const CLIENT_TIMEOUT_MS = 25000;

async function errorCodeOf(response: Response): Promise<PromptCategoryErrorCode | null> {
  const body = await response.json().catch(() => null) as { code?: unknown } | null;
  return (PROMPT_CATEGORY_ERROR_CODES as readonly unknown[]).includes(body?.code)
    ? body!.code as PromptCategoryErrorCode : null;
}

async function call<T>(path = '', configuration?: PromptCategoryConfiguration, reset = false): Promise<T> {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), CLIENT_TIMEOUT_MS);
  try {
    const response = await apiFetch(`${window.location.origin}/api/PromptCategories${path}`, {
      method: configuration || reset ? 'POST' : 'GET',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      signal: controller.signal,
      ...(configuration ? { body: JSON.stringify(configuration) } : {}),
    });
    if (!response.ok) throw new PromptCategoryApiError(await errorCodeOf(response));
    return await (response.json() as Promise<T>);
  } catch (error) {
    if (controller.signal.aborted) throw new PromptCategoryApiError('clientTimeout');
    throw error;
  } finally {
    window.clearTimeout(timer);
  }
}
export const fetchPromptCategoryAdmin = () => call<PromptCategoryAdmin>();
export const savePromptCategories = (configuration: PromptCategoryConfiguration) => call<PromptCategoryConfiguration>('', configuration);
export const resetPromptCategories = () => call<PromptCategoryConfiguration>('/reset', undefined, true);
export const fetchPromptCategoryRuns = () => call<PromptCategoryRun[]>('/runs');
export const fetchPromptCategoryReport = (months: number, version?: string) =>
  call<PromptCategoryReport>(`/report?months=${months}${version ? `&version=${encodeURIComponent(version)}` : ''}`);
