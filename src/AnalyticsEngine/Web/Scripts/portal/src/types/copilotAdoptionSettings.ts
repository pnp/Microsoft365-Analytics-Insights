/** The administrator's Copilot Adoption score settings (#683 weights, #684 band thresholds). */
export interface CopilotAdoptionScoreValues {
  frequencyWeightPercent: number;
  depthWeightPercent: number;
  breadthWeightPercent: number;
  developingScore: number;
  establishedScore: number;
  championScore: number;
}

export type CopilotAdoptionScoreField = keyof CopilotAdoptionScoreValues;

/** Which rules a report was scored with, echoed on its options. Never names who changed them. */
export interface CopilotAdoptionScoreSettingsInfo {
  version: number;
  customised: boolean;
  customisedFields: CopilotAdoptionScoreField[];
  updatedUtc?: string | null;
  defaults: CopilotAdoptionScoreValues;
}

export interface CopilotAdoptionScoreFieldChange {
  field: CopilotAdoptionScoreField;
  oldValue: number;
  newValue: number;
}

export interface CopilotAdoptionScoreSettingsChange {
  version: number;
  action: 'save' | 'reset';
  changedBy: string | null;
  changedUtc: string;
  changes: CopilotAdoptionScoreFieldChange[];
}

/** `GET api/CopilotAdoptionSettings`, and the answer to a save or reset. Administration only. */
export interface CopilotAdoptionSettingsModel {
  durable: boolean;
  version: number;
  settings: CopilotAdoptionScoreValues;
  defaults: CopilotAdoptionScoreValues;
  customisedFields: CopilotAdoptionScoreField[];
  updatedBy: string | null;
  updatedUtc: string | null;
  history: CopilotAdoptionScoreSettingsChange[];
  propagationSeconds: number;
  minThreshold: number;
  maxThreshold: number;
}