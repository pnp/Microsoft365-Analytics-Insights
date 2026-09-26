import { makeStyles, tokens, Text } from '@fluentui/react-components';
import { formatDateParts, useT } from '../../i18n';
import { formatValue } from './chartCommon';

/** One cell: a day (0 = Monday) and hour, with its value. */
export type HeatmapCell = {
  dayOfWeek: number;
  hour: number;
  value: number;
};

type HeatmapChartProps = {
  cells: HeatmapCell[];
  /** Unit for the tooltip, e.g. "calls". */
  valueLabel: string;
  /** Optional note rendered under the grid, e.g. the timezone caveat. */
  footnote?: string;
};

/**
 * The seven row labels, Monday first, in the portal language - "Mon" in English, "lun" in Spanish.
 *
 * Formatted from a known Monday (1 January 2024) rather than typed out: the English list that used
 * to be here rendered on every Spanish heatmap, and the untranslated-text gate cannot see a
 * three-letter word in an array.
 */
function weekdayLabels(): string[] {
  return Array.from({ length: 7 }, (_, day) =>
    formatDateParts(new Date(Date.UTC(2024, 0, 1 + day)), { weekday: 'short', timeZone: 'UTC' }));
}

/** Hour labels are thinned to every third hour; a label per column is unreadable at any width. */
const HOUR_LABEL_EVERY = 3;

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: '6px',
    width: '100%',
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: '38px repeat(24, minmax(0, 1fr))',
    gap: '2px',
    alignItems: 'center',
  },
  dayLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: '11px',
    textAlign: 'right',
    paddingInlineEnd: '6px',
  },
  hourLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: '10px',
    textAlign: 'center',
    overflow: 'hidden',
  },
  cell: {
    aspectRatio: '1 / 1',
    minHeight: '14px',
    borderRadius: '3px',
  },
  legend: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    marginTop: '4px',
  },
  swatch: {
    width: '14px',
    height: '14px',
    borderRadius: '3px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

/**
 * A day-of-week x hour heatmap.
 *
 * This is the one shape in the library that answers "when does this happen?" - a question a bar
 * chart genuinely cannot answer, because the two dimensions that matter (which day, which hour) are
 * both categorical and both need to be visible at once. Flattening either one into a bar chart
 * hides exactly the pattern the reader is looking for: a Monday-morning meeting wall and a Friday
 * afternoon that is dead look identical once you average over the week.
 *
 * Three deliberate choices keep it honest:
 *
 * 1. **Intensity is scaled by the square root of the value, not linearly.** Meeting activity is
 *    heavily concentrated in a handful of working hours, so a linear ramp leaves every out-of-hours
 *    cell indistinguishable from empty - and out-of-hours activity is precisely what this chart is
 *    here to surface.
 * 2. **A zero cell is drawn in the neutral background, never in the faintest shade of the ramp.**
 *    "Nothing happened" and "almost nothing happened" are different findings, and a continuous ramp
 *    from zero makes them look the same.
 * 3. **Single hue.** Colour encodes magnitude only. A multi-hue ramp invites the reader to think the
 *    colours mean categories, which they do not.
 */
export default function HeatmapChart({ cells, valueLabel, footnote }: HeatmapChartProps) {
  const t = useT();
  const styles = useStyles();
  // formatDateParts follows the portal language, which the provider sets before this renders.
  const days = weekdayLabels();

  const values = new Map<string, number>();
  let max = 0;
  let total = 0;

  for (const cell of cells) {
    if (cell.dayOfWeek < 0 || cell.dayOfWeek > 6 || cell.hour < 0 || cell.hour > 23) continue;
    const key = `${cell.dayOfWeek}:${cell.hour}`;
    const next = (values.get(key) ?? 0) + cell.value;
    values.set(key, next);
    if (next > max) max = next;
    total += cell.value;
  }

  if (total <= 0) {
    return <div className={styles.empty}>{t('charts.empty.noDataForPeriod')}</div>;
  }

  return (
    <div className={styles.root}>
      <div className={styles.grid}>
        <span />
        {Array.from({ length: 24 }, (_, hour) => (
          <span key={`h${hour}`} className={styles.hourLabel}>
            {hour % HOUR_LABEL_EVERY === 0 ? hour : ''}
          </span>
        ))}

        {days.map((day, dayIndex) => (
          <Row
            key={dayIndex}
            day={day}
            dayIndex={dayIndex}
            values={values}
            max={max}
            valueLabel={valueLabel}
            t={t}
            dayLabelClass={styles.dayLabel}
            cellClass={styles.cell}
          />
        ))}
      </div>

      <div className={styles.legend}>
        <Text size={100} className={styles.muted}>
          {t('charts.legend.none')}
        </Text>
        <span className={styles.swatch} style={{ backgroundColor: shade(0, 1) }} />
        {[0.25, 0.5, 0.75, 1].map((fraction) => (
          <span key={fraction} className={styles.swatch} style={{ backgroundColor: shade(fraction, 1) }} />
        ))}
        <Text size={100} className={styles.muted}>
          {t('charts.legend.maxValue', { value: formatValue(max), valueLabel })}
        </Text>
      </div>

      {footnote && (
        <Text size={100} className={styles.muted}>
          {footnote}
        </Text>
      )}
    </div>
  );
}

/**
 * One day's row. Extracted so the 24 cells of a row are produced by a single map rather than a
 * nested one inside the grid, which keeps the flat grid children in document order - CSS grid
 * places children in source order, so a wrapper element per row would break the layout.
 */
function Row({
  day,
  dayIndex,
  values,
  max,
  valueLabel,
  t,
  dayLabelClass,
  cellClass,
}: {
  day: string;
  dayIndex: number;
  values: Map<string, number>;
  max: number;
  valueLabel: string;
  t: ReturnType<typeof useT>;
  dayLabelClass: string;
  cellClass: string;
}) {
  return (
    <>
      <span className={dayLabelClass}>{day}</span>
      {Array.from({ length: 24 }, (_, hour) => {
        const value = values.get(`${dayIndex}:${hour}`) ?? 0;
        return (
          <div
            key={`${dayIndex}:${hour}`}
            className={cellClass}
            style={{ backgroundColor: shade(value, max) }}
            title={t('charts.heatmap.cellTitle', { day, hour: String(hour).padStart(2, '0'), value: formatValue(value), valueLabel })}
          />
        );
      })}
    </>
  );
}

/**
 * The fill for a cell. Zero is the neutral surface; everything else is a single-hue ramp on the
 * brand blue, scaled by the square root of the value so light activity stays visible.
 */
function shade(value: number, max: number): string {
  if (value <= 0) return tokens.colorNeutralBackground3;

  const intensity = Math.sqrt(Math.min(1, value / Math.max(max, 1)));
  // 0.15 floor: the lightest non-zero cell must still read as "something happened".
  const alpha = 0.15 + intensity * 0.85;
  return `rgba(15, 108, 189, ${alpha.toFixed(3)})`;
}
