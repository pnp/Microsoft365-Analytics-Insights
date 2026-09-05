import { useEffect, useState } from 'react';
import { makeStyles, tokens, Button, Input, Text } from '@fluentui/react-components';
import type { DateRange } from '../../types/licenceActivity';
import {
  PRESETS,
  PRESET_LABELS,
  diffDaysInclusive,
  latestEndString,
  matchPreset,
  presetRange,
  validateRange,
  type PresetDays,
} from './dateRange';

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

interface DateRangeControlProps {
  value: DateRange;
  onChange: (range: DateRange) => void;
  /** Injectable "now" so the preset maths and the future-date rule are deterministic in tests. */
  now?: Date;
  /** Allowed window bounds, from the availability payload (backend 7..180). */
  minDays?: number;
  maxDays?: number;
  disabled?: boolean;
}

/**
 * The reporting-window control: four one-click presets (7 / 28 / 90 / 180 days) plus an explicit,
 * validated custom range. Ranges end yesterday (the server reports whole past days only) and must be
 * within the allowed span.
 *
 * The selected range lives in the page above this component, so it is preserved when the user changes
 * a demographic filter or the drilled-into licence - only this control (or a preset click) ever
 * changes it. Custom input is validated on Apply and the offending draft is kept on screen with the
 * reason, rather than being silently discarded.
 */
export default function DateRangeControl({
  value,
  onChange,
  now,
  minDays,
  maxDays,
  disabled,
}: DateRangeControlProps) {
  const styles = useStyles();
  const latestEnd = latestEndString(now);

  const activePreset = matchPreset(value, now);
  const [customOpen, setCustomOpen] = useState<boolean>(() => activePreset == null);
  const [draft, setDraft] = useState<DateRange>(value);
  const [error, setError] = useState<string | null>(null);

  // Follow the range when it changes from outside this control (a preset click, or the page resetting
  // it), so the custom editor always opens on the range currently in effect.
  useEffect(() => {
    setDraft(value);
    setError(null);
  }, [value.from, value.to]);

  const applyPreset = (p: PresetDays): void => {
    setError(null);
    setCustomOpen(false);
    onChange(presetRange(p, now));
  };

  const openCustom = (): void => {
    setDraft(value);
    setError(null);
    setCustomOpen(true);
  };

  const applyCustom = (): void => {
    const result = validateRange(draft, { now, minDays, maxDays });
    if (!result.ok) {
      setError(result.error);
      return;
    }
    setError(null);
    onChange(draft);
  };

  return (
    <div className={styles.root}>
      <div className={styles.presets}>
        {PRESETS.map((p) => (
          <Button
            key={p}
            size="small"
            appearance={activePreset === p && !customOpen ? 'primary' : 'secondary'}
            disabled={disabled}
            aria-pressed={activePreset === p && !customOpen}
            onClick={() => applyPreset(p)}
          >
            {PRESET_LABELS[p]}
          </Button>
        ))}
        <Button
          size="small"
          appearance={customOpen ? 'primary' : 'secondary'}
          disabled={disabled}
          aria-pressed={customOpen}
          onClick={openCustom}
        >
          Custom range
        </Button>
        {!customOpen && (
          <Text size={200} className={styles.hint}>
            {diffDaysInclusive(value.from, value.to).toLocaleString()} days ending {value.to}
          </Text>
        )}
      </div>

      {customOpen && (
        <div>
          <div className={styles.custom}>
            <label className={styles.field}>
              <Text size={200} className={styles.fieldLabel}>
                From
              </Text>
              <Input
                type="date"
                value={draft.from}
                max={draft.to || latestEnd}
                disabled={disabled}
                aria-label="Start date"
                onChange={(_e, d) => setDraft((prev) => ({ ...prev, from: d.value }))}
              />
            </label>
            <label className={styles.field}>
              <Text size={200} className={styles.fieldLabel}>
                To
              </Text>
              <Input
                type="date"
                value={draft.to}
                min={draft.from || undefined}
                max={latestEnd}
                disabled={disabled}
                aria-label="End date"
                onChange={(_e, d) => setDraft((prev) => ({ ...prev, to: d.value }))}
              />
            </label>
            <Button appearance="primary" size="small" disabled={disabled} onClick={applyCustom}>
              Apply
            </Button>
          </div>
          {error && (
            <Text size={200} role="alert" className={styles.error} style={{ marginTop: '4px' }} block>
              {error}
            </Text>
          )}
        </div>
      )}
    </div>
  );
}
