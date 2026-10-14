import { apiFetch } from './http';
import type { TranslationKey } from '../i18n';
import type { CopilotAdoptionScoreValues, CopilotAdoptionSettingsModel } from '../types/copilotAdoptionSettings';

const BASE = 'api/CopilotAdoptionSettings';

/**
 * Every code the settings API can answer with (`CopilotAdoptionScoreSettingsErrorCodes`), and the two this
 * module falls back to when a response carries none. `serverAuthoredText.test.ts` keeps the two lists equal.
 */
export const COPILOT_ADOPTION_SETTINGS_ERROR_KEYS: ReadonlyMap<string, TranslationKey> = new Map<string, TranslationKey>([
  ['weightOutOfRange', 'admin.copilotAdoptionSettings.error.weightOutOfRange'],
  ['weightsMustTotal100', 'admin.copilotAdoptionSettings.error.weightsMustTotal100'],
  ['thresholdOutOfRange', 'admin.copilotAdoptionSettings.error.thresholdOutOfRange'],
  ['thresholdsNotAscending', 'admin.copilotAdoptionSettings.error.thresholdsNotAscending'],
  ['invalidRequest', 'admin.copilotAdoptionSettings.error.invalidRequest'],
  ['settingsChanged', 'admin.copilotAdoptionSettings.error.settingsChanged'],
  ['storageNotConfigured', 'admin.copilotAdoptionSettings.error.storageNotConfigured'],
  ['stateUnavailable', 'admin.copilotAdoptionSettings.error.stateUnavailable'],
  ['loadFailed', 'admin.copilotAdoptionSettings.error.loadFailed'],
  ['saveFailed', 'admin.copilotAdoptionSettings.error.saveFailed'],
]);

/** A refusal from the settings API: `code` is stable, `validationErrors` lists every rule broken. */
export class CopilotAdoptionSettingsError extends Error {
  readonly code: string;
  readonly validationErrors: string[];

  constructor(code: string, validationErrors: string[]) {
    super(code);
    this.name = 'CopilotAdoptionSettingsError';
    this.code = code;
    this.validationErrors = validationErrors;
  }
}

async function failure(response: Response, fallback: string): Promise<CopilotAdoptionSettingsError> {
  const body = await response.json().catch(() => null) as { code?: unknown; validationErrors?: unknown } | null;
  const code = typeof body?.code === 'string' ? body.code : fallback;
  const validationErrors = Array.isArray(body?.validationErrors)
    ? body.validationErrors.filter((e): e is string => typeof e === 'string')
    : [];
  return new CopilotAdoptionSettingsError(code, validationErrors);
}

export async function fetchCopilotAdoptionSettings(): Promise<CopilotAdoptionSettingsModel> {
  const response = await apiFetch(BASE);
  if (!response.ok) throw await failure(response, 'loadFailed');
  return response.json() as Promise<CopilotAdoptionSettingsModel>;
}

export async function saveCopilotAdoptionSettings(expectedVersion: number, settings: CopilotAdoptionScoreValues): Promise<CopilotAdoptionSettingsModel> {
  const response = await apiFetch(BASE, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ expectedVersion, settings }),
  });
  if (!response.ok) throw await failure(response, 'saveFailed');
  return response.json() as Promise<CopilotAdoptionSettingsModel>;
}

export async function resetCopilotAdoptionSettings(expectedVersion: number): Promise<CopilotAdoptionSettingsModel> {
  const response = await apiFetch(`${BASE}/reset`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ expectedVersion }),
  });
  if (!response.ok) throw await failure(response, 'saveFailed');
  return response.json() as Promise<CopilotAdoptionSettingsModel>;
}