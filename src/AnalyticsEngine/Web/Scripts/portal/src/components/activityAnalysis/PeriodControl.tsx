import { useEffect, useState } from 'react';
import { Button, Input, Text, makeStyles, tokens } from '@fluentui/react-components';
import { formatNumber, plural, useT } from '../../i18n';
import type { ActivityAnalysisAvailability } from '../../types/activityAnalysis';
import {
  PERIOD_PRESETS,
  PRESET_LABEL_KEYS,
  addDays,
  allIsLimited,
  formatWeekDate,
  validateCustomPeriod,
  weekCount,
  type PeriodPreset,
  type WeekPeriod,
} from './period';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  presets: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    flexWrap: 'wrap',
  },
  summary: {
    color: tokens.colorNeutralForeground2,
    fontVariantNumeric: 'tabular-nums',
  },
  custom: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: '8px',
    flexWrap: 'wrap',
  },
  field: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
  },
  fieldLabel: {
    color: tokens.colorNeutralForeground3,
  },
  hint: {
    color: tokens.colorNeutralForeground3,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
  },
});

export type PeriodChoice = PeriodPreset | 'custom';

interface PeriodControlProps {
  availability: ActivityAnalysisAvailability;
  choice: PeriodChoice;
  period: WeekPeriod;
  onPreset: (preset: PeriodPreset) => void;
  onCustom: (period: WeekPeriod) => void;
  /** The id of the visible label naming the control. */
  labelId: string;
}

/**
 * The period: four one-click presets ending on the latest week with data, or a custom first and last
 * week. Any date picked is moved to the Monday of its week and kept within the weeks that have data,
 * which is what the server would do anyway - doing it here means the summary beside the control shows
 * the period the figures will actually cover.
 */
export default function PeriodControl({ availability, choice, period, onPreset, onCustom, labelId }: PeriodControlProps) {
  const styles = useStyles();
  const t = useT();
  const [customOpen, setCustomOpen] = useState(choice === 'custom');
  const [draft, setDraft] = useState<WeekPeriod>(period);
  const [error, setError] = useState<string | null>(null);

  // The editor always opens on the period in force, wherever it was last set from.
  useEffect(() => {
    setDraft(period);
    setError(null);
  }, [period.from, period.to]);

  const weeks = weekCount(period);
  const earliest = availability.earliestWeek ?? undefined;
  // The last week runs to its Sunday, so any day of it can be picked.
  const latest = availability.latestWeek ? addDays(availability.latestWeek, 6) : undefined;

  const applyCustom = () => {
    const result = validateCustomPeriod(draft, availability, t);
    if (!result.ok) {
      setError(result.error);
      return;
    }
    setError(null);
    onCustom(result.period);
  };

  return (
    <div className={styles.root}>
      <div className={styles.presets} role="group" aria-labelledby={labelId}>
        {PERIOD_PRESETS.map((preset) => {
          const active = choice === preset && !customOpen;
          return (
            <Button
              key={preset}
              size="small"
              appearance={active ? 'primary' : 'secondary'}
              aria-pressed={active}
              onClick={() => {
                setCustomOpen(false);
                setError(null);
                onPreset(preset);
              }}
            >
              {t(PRESET_LABEL_KEYS[preset])}
            </Button>
          );
        })}
        <Button
          size="small"
          appearance={customOpen ? 'primary' : 'secondary'}
          aria-pressed={customOpen}
          onClick={() => {
            setDraft(period);
            setError(null);
            setCustomOpen(true);
          }}
        >
          {t('activityAnalysis.period.custom')}
        </Button>
        <Text size={200} className={styles.summary}>
          {t(plural(weeks, 'activityAnalysis.period.summary.one', 'activityAnalysis.period.summary.other'), {
            from: formatWeekDate(period.from),
            to: formatWeekDate(period.to),
            weeks: formatNumber(weeks),
          })}
        </Text>
      </div>

      {choice === 'all' && !customOpen && allIsLimited(availability) && (
        <Text size={200} className={styles.hint}>
          {t('activityAnalysis.period.allLimited', { weeks: formatNumber(availability.maximumWeeks) })}
        </Text>
      )}

      {customOpen && (
        <div>
          <div className={styles.custom}>
            <label className={styles.field}>
              <Text size={200} className={styles.fieldLabel}>
                {t('activityAnalysis.period.from')}
              </Text>
              <Input
                type="date"
                size="small"
                value={draft.from}
                min={earliest}
                max={latest}
                onChange={(_e, data) => setDraft((prev) => ({ ...prev, from: data.value }))}
              />
            </label>
            <label className={styles.field}>
              <Text size={200} className={styles.fieldLabel}>
                {t('activityAnalysis.period.to')}
              </Text>
              <Input
                type="date"
                size="small"
                value={draft.to}
                min={earliest}
                max={latest}
                onChange={(_e, data) => setDraft((prev) => ({ ...prev, to: data.value }))}
              />
            </label>
            <Button appearance="primary" size="small" onClick={applyCustom}>
              {t('activityAnalysis.period.apply')}
            </Button>
          </div>
          {error ? (
            <Text size={200} role="alert" className={styles.error} block style={{ marginTop: '4px' }}>
              {error}
            </Text>
          ) : (
            <Text size={200} className={styles.hint} block style={{ marginTop: '4px' }}>
              {t('activityAnalysis.period.weekHint')}{' '}
              {availability.earliestWeek && availability.latestWeek
                ? t('activityAnalysis.period.available', {
                    from: formatWeekDate(availability.earliestWeek),
                    to: formatWeekDate(availability.latestWeek),
                  })
                : null}
            </Text>
          )}
        </div>
      )}
    </div>
  );
}
