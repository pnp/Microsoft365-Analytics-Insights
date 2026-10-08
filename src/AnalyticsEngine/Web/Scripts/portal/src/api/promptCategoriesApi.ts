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

async function call<T>(path = '', configuration?: PromptCategoryConfiguration, reset = false): Promise<T> {
  const response = await apiFetch(`${window.location.origin}/api/PromptCategories${path}`, {
    method: configuration || reset ? 'POST' : 'GET',
    headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
    ...(configuration ? { body: JSON.stringify(configuration) } : {}),
  });
  if (!response.ok) throw new Error(translateActive('promptCategories.error'));
  return response.json() as Promise<T>;
}
export const fetchPromptCategoryAdmin = () => call<PromptCategoryAdmin>();
export const savePromptCategories = (configuration: PromptCategoryConfiguration) => call<PromptCategoryConfiguration>('', configuration);
export const resetPromptCategories = () => call<PromptCategoryConfiguration>('/reset', undefined, true);
export const fetchPromptCategoryRuns = () => call<PromptCategoryRun[]>('/runs');
export const fetchPromptCategoryReport = (months: number, version?: string) =>
  call<PromptCategoryReport>(`/report?months=${months}${version ? `&version=${encodeURIComponent(version)}` : ''}`);
