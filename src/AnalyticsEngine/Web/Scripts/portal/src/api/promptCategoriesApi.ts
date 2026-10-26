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
  /**
   * Identifies the saved document as a whole - opt-in, cap and taxonomy - unlike `version`, which identifies only the
   * taxonomy. Sent back with a save or reset, so neither can overwrite settings someone else saved in the meantime.
   */
  revision?: string | null;
}
export interface PromptCategoryAdmin {
  configuration: PromptCategoryConfiguration;
  storageAvailable: boolean;
  backendConfigured: boolean;
}
export interface PromptCategoryTestResult {
  category: PromptCategoryDefinition;
  version: string;
  inputTokens: number;
  outputTokens: number;
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
  'storedConfigurationInvalid', 'configurationChanged', 'databaseNotUpgraded', 'reportUnavailable',
  'invalidTestPrompt', 'backendNotConfigured', 'taxonomyChanged', 'testBusy', 'testThrottled',
  'testAccessDenied', 'testTimeout', 'testRefused', 'testInvalidResponse', 'testServiceFailure',
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

type RequestBody = PromptCategoryConfiguration | { prompt: string; version: string } | { expectedRevision: string | null };

async function call<T>(path = '', body?: RequestBody, timeoutMs = CLIENT_TIMEOUT_MS): Promise<T> {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await apiFetch(`${window.location.origin}/api/PromptCategories${path}`, {
      method: body ? 'POST' : 'GET',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
      signal: controller.signal,
      ...(body ? { body: JSON.stringify(body) } : {}),
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
/** Saves against `configuration.revision`; refused with `configurationChanged` if the settings changed since. */
export const savePromptCategories = (configuration: PromptCategoryConfiguration) => call<PromptCategoryConfiguration>('', configuration);
/**
 * Resets to the disabled defaults, if the settings are still at `expectedRevision`. Null only replaces a saved
 * configuration the server can't read.
 */
export const resetPromptCategories = (expectedRevision: string | null) =>
  call<PromptCategoryConfiguration>('/reset', { expectedRevision });
export const fetchPromptCategoryRuns = () => call<PromptCategoryRun[]>('/runs');
export const testPromptCategory = (prompt: string, version: string) =>
  call<PromptCategoryTestResult>('/test', { prompt, version }, 40000);
export const fetchPromptCategoryReport = (months: number, version?: string) =>
  call<PromptCategoryReport>(`/report?months=${months}${version ? `&version=${encodeURIComponent(version)}` : ''}`);
