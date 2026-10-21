import type { TranslationKey } from '../../i18n';
import type { CopilotAdoptionScoreField, CopilotAdoptionScoreValues } from '../../types/copilotAdoptionSettings';

export const WEIGHT_FIELDS: readonly CopilotAdoptionScoreField[] = ['frequencyWeightPercent', 'depthWeightPercent', 'breadthWeightPercent'];
export const THRESHOLD_FIELDS: readonly CopilotAdoptionScoreField[] = ['developingScore', 'establishedScore', 'championScore'];

export const OPPORTUNITY_WEIGHT_FIELDS: readonly CopilotAdoptionScoreField[] = ['opportunityUnlicensedCopilotWeight', 'opportunityCollaborationWeight', 'opportunityEmailWeight', 'opportunityDocumentWeight'];
export const COWORK_WEIGHT_FIELDS: readonly CopilotAdoptionScoreField[] = ['coworkCollaborationWeight', 'coworkMeetingWeight', 'coworkEmailWeight', 'coworkDocumentWeight'];
export const SETTING_GROUPS: readonly { key: TranslationKey; help: TranslationKey; fields: readonly CopilotAdoptionScoreField[] }[] = [
  { key: 'admin.copilotAdoptionSettings.targets.title', help: 'admin.copilotAdoptionSettings.targets.help', fields: ['frequencyTargetRatio', 'workingDaysPerWeek', 'depthTargetInteractionsPerActiveDay', 'depthMinActiveDays', 'breadthTargetApps'] },
  { key: 'admin.copilotAdoptionSettings.habit.title', help: 'admin.copilotAdoptionSettings.habit.help', fields: ['habitModerateMinDays', 'habitFrequentMinDays', 'habitDailyMinDays'] },
  { key: 'admin.copilotAdoptionSettings.opportunity.title', help: 'admin.copilotAdoptionSettings.opportunity.help', fields: ['opportunityRecommendScore', 'opportunityProvenDemandMinActiveDays', ...OPPORTUNITY_WEIGHT_FIELDS] },
  { key: 'admin.copilotAdoptionSettings.cowork.title', help: 'admin.copilotAdoptionSettings.cowork.help', fields: ['coworkLoadMinScore', 'coworkFluencyMinScore', 'coworkRegularMinActiveDays', ...COWORK_WEIGHT_FIELDS] },
  { key: 'admin.copilotAdoptionSettings.agent.title', help: 'admin.copilotAdoptionSettings.agent.help', fields: ['agentReviewInactiveDays', 'agentRetireInactiveDays', 'agentNewDays', 'agentMinUsers'] },
  { key: 'admin.copilotAdoptionSettings.seats.title', help: 'admin.copilotAdoptionSettings.seats.help', fields: ['reclaimGraceDays'] },
  { key: 'admin.copilotAdoptionSettings.segment.title', help: 'admin.copilotAdoptionSettings.segment.help', fields: ['minSeatsPerSegment'] },
];

export const DEFAULT_SCORE_VALUES: CopilotAdoptionScoreValues = {
  frequencyWeightPercent: 50, depthWeightPercent: 30, breadthWeightPercent: 20,
  developingScore: 25, establishedScore: 50, championScore: 75,
  frequencyTargetRatio: 0.6, workingDaysPerWeek: 5, depthTargetInteractionsPerActiveDay: 5, depthMinActiveDays: 3, breadthTargetApps: 3,
  habitModerateMinDays: 6, habitFrequentMinDays: 11, habitDailyMinDays: 20,
  opportunityRecommendScore: 50, opportunityProvenDemandMinActiveDays: 3,
  opportunityUnlicensedCopilotWeight: 35, opportunityCollaborationWeight: 25, opportunityEmailWeight: 20, opportunityDocumentWeight: 20,
  coworkLoadMinScore: 50, coworkFluencyMinScore: 50, coworkRegularMinActiveDays: 3,
  coworkCollaborationWeight: 20, coworkMeetingWeight: 35, coworkEmailWeight: 25, coworkDocumentWeight: 20,
  agentReviewInactiveDays: 30, agentRetireInactiveDays: 90, agentNewDays: 30, agentMinUsers: 3,
  reclaimGraceDays: 30, minSeatsPerSegment: 5,
};

export const SCORE_FIELD_LABEL_KEYS: Record<CopilotAdoptionScoreField, TranslationKey> = {
  frequencyWeightPercent: 'admin.copilotAdoptionSettings.field.frequencyWeightPercent',
  depthWeightPercent: 'admin.copilotAdoptionSettings.field.depthWeightPercent',
  breadthWeightPercent: 'admin.copilotAdoptionSettings.field.breadthWeightPercent',
  developingScore: 'admin.copilotAdoptionSettings.field.developingScore',
  establishedScore: 'admin.copilotAdoptionSettings.field.establishedScore',
  championScore: 'admin.copilotAdoptionSettings.field.championScore',
  frequencyTargetRatio: 'admin.copilotAdoptionSettings.field.frequencyTargetRatio',
  workingDaysPerWeek: 'admin.copilotAdoptionSettings.field.workingDaysPerWeek',
  depthTargetInteractionsPerActiveDay: 'admin.copilotAdoptionSettings.field.depthTargetInteractionsPerActiveDay',
  depthMinActiveDays: 'admin.copilotAdoptionSettings.field.depthMinActiveDays',
  breadthTargetApps: 'admin.copilotAdoptionSettings.field.breadthTargetApps',
  habitModerateMinDays: 'admin.copilotAdoptionSettings.field.habitModerateMinDays',
  habitFrequentMinDays: 'admin.copilotAdoptionSettings.field.habitFrequentMinDays',
  habitDailyMinDays: 'admin.copilotAdoptionSettings.field.habitDailyMinDays',
  opportunityRecommendScore: 'admin.copilotAdoptionSettings.field.opportunityRecommendScore',
  opportunityProvenDemandMinActiveDays: 'admin.copilotAdoptionSettings.field.opportunityProvenDemandMinActiveDays',
  opportunityUnlicensedCopilotWeight: 'admin.copilotAdoptionSettings.field.opportunityUnlicensedCopilotWeight',
  opportunityCollaborationWeight: 'admin.copilotAdoptionSettings.field.opportunityCollaborationWeight',
  opportunityEmailWeight: 'admin.copilotAdoptionSettings.field.opportunityEmailWeight',
  opportunityDocumentWeight: 'admin.copilotAdoptionSettings.field.opportunityDocumentWeight',
  coworkLoadMinScore: 'admin.copilotAdoptionSettings.field.coworkLoadMinScore',
  coworkFluencyMinScore: 'admin.copilotAdoptionSettings.field.coworkFluencyMinScore',
  coworkRegularMinActiveDays: 'admin.copilotAdoptionSettings.field.coworkRegularMinActiveDays',
  coworkCollaborationWeight: 'admin.copilotAdoptionSettings.field.coworkCollaborationWeight',
  coworkMeetingWeight: 'admin.copilotAdoptionSettings.field.coworkMeetingWeight',
  coworkEmailWeight: 'admin.copilotAdoptionSettings.field.coworkEmailWeight',
  coworkDocumentWeight: 'admin.copilotAdoptionSettings.field.coworkDocumentWeight',
  agentReviewInactiveDays: 'admin.copilotAdoptionSettings.field.agentReviewInactiveDays',
  agentRetireInactiveDays: 'admin.copilotAdoptionSettings.field.agentRetireInactiveDays',
  agentNewDays: 'admin.copilotAdoptionSettings.field.agentNewDays',
  agentMinUsers: 'admin.copilotAdoptionSettings.field.agentMinUsers',
  reclaimGraceDays: 'admin.copilotAdoptionSettings.field.reclaimGraceDays',
  minSeatsPerSegment: 'admin.copilotAdoptionSettings.field.minSeatsPerSegment',
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
  const range = (v: number, min: number, max: number, integer = true) =>
    Number.isFinite(v) && (!integer || Number.isInteger(v)) && v >= min && v <= max;
  if (!range(values.frequencyTargetRatio, Number.MIN_VALUE, 1, false) || !range(values.workingDaysPerWeek, 1, 7)
    || !range(values.depthTargetInteractionsPerActiveDay, Number.MIN_VALUE, 1000000, false)
    || !range(values.depthMinActiveDays, 1, 365) || !range(values.breadthTargetApps, 1, 100)) errors.push('targetOutOfRange');
  if (!(range(values.habitModerateMinDays, 1, 28) && range(values.habitFrequentMinDays, 1, 28)
    && range(values.habitDailyMinDays, 1, 28) && values.habitModerateMinDays < values.habitFrequentMinDays
    && values.habitFrequentMinDays < values.habitDailyMinDays)) errors.push('habitDaysNotAscending');
  if ([OPPORTUNITY_WEIGHT_FIELDS, COWORK_WEIGHT_FIELDS].some((fields) =>
    fields.some((f) => !range(values[f], 0, 100)) || fields.reduce((sum, f) => sum + values[f], 0) !== 100)) errors.push('decisionWeightsInvalid');
  if (![values.opportunityRecommendScore, values.coworkLoadMinScore, values.coworkFluencyMinScore].every((v) => range(v, 1, 100))
    || !range(values.opportunityProvenDemandMinActiveDays, 1, 365) || !range(values.coworkRegularMinActiveDays, 1, 365)) errors.push('decisionThresholdInvalid');
  if (!range(values.agentReviewInactiveDays, 1, 120) || !range(values.agentRetireInactiveDays, 1, 120)
    || values.agentReviewInactiveDays >= values.agentRetireInactiveDays || !range(values.agentNewDays, 1, 118)
    || values.agentNewDays >= values.agentRetireInactiveDays
    || !range(values.agentMinUsers, 3, 200000)) errors.push('agentSettingsInvalid');
  if (!range(values.reclaimGraceDays, 1, 365)) errors.push('seatWindowsInvalid');
  if (!range(values.minSeatsPerSegment, 5, 200000)) errors.push('segmentMinimumInvalid');
  return errors;
}