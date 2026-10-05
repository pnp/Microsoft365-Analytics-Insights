import { useEffect, useId, useState } from 'react';
import { makeStyles, tokens, Button, Input, Label, Select, Text } from '@fluentui/react-components';
import { CalendarLtr16Regular } from '@fluentui/react-icons';
import type { DateRange } from '../../types/licenceActivity';
import { useT, type TFunction, type TranslationKey } from '../../i18n';
import { MIN_SUPPORTED_DATE, addDays, latestEndString, toDateString, validateRange } from '../licenceActivity/dateRange';
import { formatDate } from '../shared/KpiGrid';

/** The rolling windows, which end today. The only periods with named action lists. */
export const ROLLING_WINDOWS: { value: number; labelKey: TranslationKey }[] = [
  { value: 7, labelKey: 'copilotAdoption.page.window.last7Days' },
  { value: 28, labelKey: 'copilotAdoption.page.window.last28Days' },
  { value: 90, labelKey: 'copilotAdoption.page.window.last90Days' },
  { value: 180, labelKey: 'copilotAdoption.page.window.last180Days' },
];

/** A period with explicit dates. Each one ends before today, so the server treats it as a past range. */
export type FixedPeriodKind = 'lastMonth' | 'lastQuarter' | 'custom';

export interface FixedPeriod {
  kind: FixedPeriodKind;
  range: DateRange;
}

export const FIXED_PERIOD_LABEL_KEYS: Record<FixedPeriodKind, TranslationKey> = {
  lastMonth: 'copilotAdoption.page.window.lastCalendarMonth',
  lastQuarter: 'copilotAdoption.page.window.lastCalendarQuarter',
  custom: 'copilotAdoption.page.window.customRange',
};

const FIXED_PERIOD_KINDS: FixedPeriodKind[] = ['lastMonth', 'lastQuarter', 'custom'];

export function lastCalendarMonthRange(now = new Date()): DateRange {
  const firstThisMonth = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1));
  const to = addDays(toDateString(firstThisMonth), -1);
  const end = new Date(`${to}T00:00:00Z`);
  return { from: toDateString(new Date(Date.UTC(end.getUTCFullYear(), end.getUTCMonth(), 1))), to };
}

export function lastCalendarQuarterRange(now = new Date()): DateRange {
  const currentQuarter = Math.floor(now.getUTCMonth() / 3);
  const startMonth = currentQuarter === 0 ? 9 : (currentQuarter - 1) * 3;
  const year = currentQuarter === 0 ? now.getUTCFullYear() - 1 : now.getUTCFullYear();
  const from = toDateString(new Date(Date.UTC(year, startMonth, 1)));
  const to = addDays(toDateString(new Date(Date.UTC(year, startMonth + 3, 1))), -1);
  return { from, to };
}

/** The name of a period, as the control shows it and the printout states it. */
export function periodLabel(t: TFunction, windowDays: number, fixed: FixedPeriod | null): string {
  if (fixed) return t(FIXED_PERIOD_LABEL_KEYS[fixed.kind]);
  const rolling = ROLLING_WINDOWS.find((w) => w.value === windowDays);
  return rolling ? t(rolling.labelKey) : t('copilotAdoption.page.print.lastDays', { v0: windowDays });
}

/** The dates a rolling window would cover if it were pinned to dates: it, ending yesterday. */
function rollingAsRange(windowDays: number, now?: Date): DateRange {
  const to = latestEndString(now);
  return { from: addDays(to, -(Math.max(1, windowDays) - 1)), to };
}

function sameRange(a: DateRange, b: DateRange): boolean {
  return a.from === b.from && a.to === b.to;
}

const useStyles = makeStyles({
  // Top-aligned rather than centred, so a validation message under the date fields grows the control
  // downwards without moving the drop-down, or the buttons beside the control, off their line.
  root: {
    display: 'flex',
    alignItems: 'flex-start',
    flexWrap: 'wrap',
    gap: '8px 12px',
  },
  group: {
    display: 'flex',
    alignItems: 'center',
    gap: '8px',
    minHeight: '32px',
  },
  label: {
    color: tokens.colorNeutralForeground2,
  },
  select: {
    minWidth: '200px',
  },
  caption: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
    minHeight: '32px',
    color: tokens.colorNeutralForeground2,
  },
  customWrap: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
  },
  custom: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '8px',
  },
  dateInput: {
    width: '160px',
  },
  // As wide as the fields above it and no wider: a long message wraps rather than widening the control.
  error: {
    width: 0,
    minWidth: '100%',
    color: tokens.colorPaletteRedForeground1,
  },
});

/**
 * The Copilot Adoption reporting period: one drop-down, and the dates only when the reader picks
 * their own.
 *
 * Two kinds of period, grouped as such because they behave differently. A rolling window ends today,
 * and is the only kind with named action lists. A fixed period - last calendar month, last calendar
 * quarter, or a custom range - ends before today, so the server scores it from the licence history
 * and withholds the per-person lists. The calendar periods state their dates beside the drop-down,
 * because "last quarter" means a different quarter depending on when the report is read.
 *
 * A custom range is chosen in two steps: picking it opens the date fields on the range in effect,
 * and only Apply changes the report. A new range is a new analysis on the server, so it is not
 * started on every keystroke in a date field.
 */
export default function AdoptionPeriodControl({
  windowDays,
  fixed,
  onRollingChange,
  onFixedChange,
  now,
}: {
  windowDays: number;
  fixed: FixedPeriod | null;
  onRollingChange: (days: number) => void;
  onFixedChange: (period: FixedPeriod) => void;
  /** Injectable "now", so the calendar periods and the latest allowed date are deterministic in tests. */
  now?: Date;
}) {
  const styles = useStyles();
  const t = useT();
  const fromId = useId();
  const toId = useId();

  // Picking "Custom range" opens the date fields without changing the report, which keeps showing the
  // period in effect until Apply.
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState<DateRange>(() => fixed?.range ?? rollingAsRange(windowDays, now));
  const [error, setError] = useState<string | null>(null);

  const appliedCustom = fixed?.kind === 'custom' ? fixed.range : null;

  // A custom range applied - or replaced - from outside the fields is the one they show.
  useEffect(() => {
    if (appliedCustom) {
      setDraft(appliedCustom);
      setError(null);
    }
  }, [appliedCustom?.from, appliedCustom?.to]);

  const customShown = editing || appliedCustom !== null;
  const selected = customShown ? 'custom' : fixed ? fixed.kind : String(windowDays);
  const latestEnd = latestEndString(now);
  const pending = editing || (appliedCustom !== null && !sameRange(draft, appliedCustom));

  const choose = (value: string) => {
    setError(null);
    if (value === 'custom') {
      setDraft(fixed?.range ?? rollingAsRange(windowDays, now));
      setEditing(true);
      return;
    }
    setEditing(false);
    if (value === 'lastMonth') onFixedChange({ kind: 'lastMonth', range: lastCalendarMonthRange(now) });
    else if (value === 'lastQuarter') onFixedChange({ kind: 'lastQuarter', range: lastCalendarQuarterRange(now) });
    else onRollingChange(Number(value));
  };

  const apply = () => {
    const result = validateRange(draft, { now, t });
    if (!result.ok) {
      setError(result.error);
      return;
    }
    setError(null);
    setEditing(false);
    onFixedChange({ kind: 'custom', range: draft });
  };

  return (
    <div className={styles.root}>
      <div className={styles.group}>
        <Text size={200} className={styles.label}>
          {t('copilotAdoption.page.controls.periodLabel')}
        </Text>
        <Select
          className={styles.select}
          value={selected}
          onChange={(_e: unknown, d: { value: string }) => choose(d.value)}
          aria-label={t('copilotAdoption.page.controls.reportingPeriodAria')}
        >
          <optgroup label={t('copilotAdoption.page.period.group.upToToday')}>
            {ROLLING_WINDOWS.map((w) => (
              <option key={w.value} value={String(w.value)}>
                {t(w.labelKey)}
              </option>
            ))}
          </optgroup>
          <optgroup label={t('copilotAdoption.page.period.group.past')}>
            {FIXED_PERIOD_KINDS.map((kind) => (
              <option key={kind} value={kind}>
                {t(FIXED_PERIOD_LABEL_KEYS[kind])}
              </option>
            ))}
          </optgroup>
        </Select>
      </div>

      {fixed && !customShown && (
        <Text size={200} className={styles.caption}>
          <CalendarLtr16Regular aria-hidden="true" />
          {t('copilotAdoption.page.period.dates', { from: formatDate(fixed.range.from), to: formatDate(fixed.range.to) })}
        </Text>
      )}

      {customShown && (
        <div className={styles.customWrap}>
          <div className={styles.custom} role="group" aria-label={t('copilotAdoption.page.window.customRange')}>
            <Label htmlFor={fromId} size="small" className={styles.label}>
              {t('copilotAdoption.page.period.from')}
            </Label>
            <Input
              id={fromId}
              className={styles.dateInput}
              type="date"
              value={draft.from}
              min={MIN_SUPPORTED_DATE}
              max={draft.to || latestEnd}
              onChange={(_e: unknown, d: { value: string }) => setDraft((prev) => ({ ...prev, from: d.value }))}
              onKeyDown={(e: { key: string }) => {
                if (e.key === 'Enter') apply();
              }}
            />
            <Label htmlFor={toId} size="small" className={styles.label}>
              {t('copilotAdoption.page.period.to')}
            </Label>
            <Input
              id={toId}
              className={styles.dateInput}
              type="date"
              value={draft.to}
              min={draft.from || MIN_SUPPORTED_DATE}
              max={latestEnd}
              onChange={(_e: unknown, d: { value: string }) => setDraft((prev) => ({ ...prev, to: d.value }))}
              onKeyDown={(e: { key: string }) => {
                if (e.key === 'Enter') apply();
              }}
            />
            <Button appearance="primary" disabled={!pending} onClick={apply}>
              {t('copilotAdoption.page.period.apply')}
            </Button>
          </div>
          {error && (
            <Text size={200} role="alert" className={styles.error}>
              {error}
            </Text>
          )}
        </div>
      )}
    </div>
  );
}
