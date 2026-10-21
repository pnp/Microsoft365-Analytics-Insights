import { MessageBar, MessageBarBody, Text } from '@fluentui/react-components';
import { formatNumber, useT, type TFunction } from '../../i18n';
import type { CopilotAdoptionOptions } from '../../types/copilotAdoption';
import type { CopilotAdoptionScoreValues } from '../../types/copilotAdoptionSettings';
import { DEFAULT_SCORE_VALUES, SCORE_FIELD_LABEL_KEYS } from './scoreSettings';

/** The effective settings a report was scored with, as whole percentages and band floors. */
export function effectiveScoreValues(o: CopilotAdoptionOptions): CopilotAdoptionScoreValues {
  const sum = o.frequencyWeight + o.depthWeight + o.breadthWeight || 1;
  return {
    ...Object.fromEntries(Object.keys(DEFAULT_SCORE_VALUES).map((field) =>
      [field, o[field as keyof CopilotAdoptionOptions] ?? DEFAULT_SCORE_VALUES[field as keyof CopilotAdoptionScoreValues]])) as unknown as CopilotAdoptionScoreValues,
    frequencyWeightPercent: Math.round((o.frequencyWeight / sum) * 100),
    depthWeightPercent: Math.round((o.depthWeight / sum) * 100),
    breadthWeightPercent: Math.round((o.breadthWeight / sum) * 100),
    developingScore: o.developingScore,
    establishedScore: o.establishedScore,
    championScore: o.championScore,
  };
}

function describe(t: TFunction, v: CopilotAdoptionScoreValues): string {
  return t('copilotAdoption.scoreSettings.values', {
    frequency: formatNumber(v.frequencyWeightPercent),
    depth: formatNumber(v.depthWeightPercent),
    breadth: formatNumber(v.breadthWeightPercent),
    developing: formatNumber(v.developingScore),
    established: formatNumber(v.establishedScore),
    champion: formatNumber(v.championScore),
  });
}

function customisedText(t: TFunction, o: CopilotAdoptionOptions): string | null {
  const info = o.scoreSettings;
  if (!info?.customised) return null;
  const values = effectiveScoreValues(o);
  return t('copilotAdoption.scoreSettings.customised', {
    current: describe(t, effectiveScoreValues(o)),
    defaults: describe(t, info.defaults),
  }) + ' ' + info.customisedFields.map((field) => t('copilotAdoption.scoreSettings.fieldValue', {
    field: t(SCORE_FIELD_LABEL_KEYS[field]),
    value: formatNumber(values[field]),
    defaultValue: formatNumber(info.defaults[field] ?? DEFAULT_SCORE_VALUES[field]),
  })).join('; ');
}

/** Shown above every tab while an administrator has changed the score weights or adoption levels. */
export function ScoreSettingsBanner({ options }: { options: CopilotAdoptionOptions }) {
  const t = useT();
  const text = customisedText(t, options);
  if (!text) return null;
  return (
    <MessageBar intent="info" data-testid="score-settings-banner">
      <MessageBarBody>
        <strong>{t('copilotAdoption.scoreSettings.heading')}</strong> {text}
      </MessageBarBody>
    </MessageBar>
  );
}

/** The Method tab says which settings it is documenting, customised or not. */
export function ScoreSettingsMethodNote({ options }: { options: CopilotAdoptionOptions }) {
  const t = useT();
  const values = effectiveScoreValues(options);
  return (
    <div><Text data-testid="score-settings-method-note">
      {customisedText(t, options) ?? t('copilotAdoption.scoreSettings.defaults', { current: describe(t, effectiveScoreValues(options)) })}
    </Text>
      <details>
        <summary>{t('copilotAdoption.scoreSettings.effectiveValues')}</summary>
        <ul>{(Object.keys(DEFAULT_SCORE_VALUES) as (keyof CopilotAdoptionScoreValues)[]).map((field) => <li key={field}>
          {t('copilotAdoption.scoreSettings.fieldValue', {
            field: t(SCORE_FIELD_LABEL_KEYS[field]),
            value: formatNumber(values[field]),
            defaultValue: formatNumber(options.scoreSettings?.defaults[field] ?? DEFAULT_SCORE_VALUES[field]),
          })}
        </li>)}</ul>
      </details>
    </div>
  );
}