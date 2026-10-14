import { MessageBar, MessageBarBody, Text } from '@fluentui/react-components';
import { formatNumber, useT, type TFunction } from '../../i18n';
import type { CopilotAdoptionOptions } from '../../types/copilotAdoption';
import type { CopilotAdoptionScoreValues } from '../../types/copilotAdoptionSettings';

/** The effective settings a report was scored with, as whole percentages and band floors. */
export function effectiveScoreValues(o: CopilotAdoptionOptions): CopilotAdoptionScoreValues {
  const sum = o.frequencyWeight + o.depthWeight + o.breadthWeight || 1;
  return {
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
  return t('copilotAdoption.scoreSettings.customised', {
    current: describe(t, effectiveScoreValues(o)),
    defaults: describe(t, info.defaults),
  });
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
  return (
    <Text data-testid="score-settings-method-note">
      {customisedText(t, options) ?? t('copilotAdoption.scoreSettings.defaults', { current: describe(t, effectiveScoreValues(options)) })}
    </Text>
  );
}