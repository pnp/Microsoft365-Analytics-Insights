import type { TranslationKey } from '../../i18n';
import type { CopilotAdoptionScoreField, CopilotAdoptionScoreValues } from '../../types/copilotAdoptionSettings';

export const WEIGHT_FIELDS: readonly CopilotAdoptionScoreField[] = ['frequencyWeightPercent', 'depthWeightPercent', 'breadthWeightPercent'];
export const THRESHOLD_FIELDS: readonly CopilotAdoptionScoreField[] = ['developingScore', 'establishedScore', 'championScore'];

export const SCORE_FIELD_LABEL_KEYS: Record<CopilotAdoptionScoreField, TranslationKey> = {
  frequencyWeightPercent: 'admin.copilotAdoptionSettings.field.frequencyWeightPercent',
  depthWeightPercent: 'admin.copilotAdoptionSettings.field.depthWeightPercent',
  breadthWeightPercent: 'admin.copilotAdoptionSettings.field.breadthWeightPercent',
  developingScore: 'admin.copilotAdoptionSettings.field.developingScore',
  establishedScore: 'admin.copilotAdoptionSettings.field.establishedScore',
  championScore: 'admin.copilotAdoptionSettings.field.championScore',
};

/** The same rules, and the same codes, as `CopilotAdoptionScoreSettings.Validate` on the server. */
export function validateScoreSettings(values: CopilotAdoptionScoreValues, minThreshold = 1, maxThreshold = 100): string[] {
  const errors: string[] = [];
  const weights = WEIGHT_FIELDS.map((f) => values[f]);
  if (weights.some((w) => !Number.isInteger(w) || w < 0 || w > 100)) errors.push('weightOutOfRange');
  else if (weights.reduce((a, b) => a + b, 0) !== 100) errors.push('weightsMustTotal100');

  const thresholds = THRESHOLD_FIELDS.map((f) => values[f]);
  if (thresholds.some((v) => !Number.isInteger(v) || v < minThreshold || v > maxThreshold)) errors.push('thresholdOutOfRange');
  else if (!(thresholds[0] < thresholds[1] && thresholds[1] < thresholds[2])) errors.push('thresholdsNotAscending');
  return errors;
}