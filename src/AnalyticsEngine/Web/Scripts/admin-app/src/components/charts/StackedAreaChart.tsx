import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportSeries } from '../../types/reports';
import { formatCompact, formatValue, formatWeek, niceTicks, seriesColor } from './chartCommon';

const W = 960;
const H = 300;
const MARGIN = { top: 14, right: 18, bottom: 42, left: 56 };

const useStyles = makeStyles({
  root: {
    width: '100%',
  },
  svg: {
    width: '100%',
    height: 'auto',
    display: 'block',
    overflow: 'visible',
  },
  legend: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '4px 16px',
    marginTop: '8px',
  },
  legendItem: {
    display: 'inline-flex',
    alignItems: 'center',
    gap: '6px',
  },
  swatch: {
    width: '12px',
    height: '12px',
    borderRadius: '2px',
    flexShrink: 0,
  },
  label: {
    color: tokens.colorNeutralForeground2,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

/**
 * A stacked area chart, for showing how the *composition* of a total changes over time.
 *
 * Separate from the line chart because it answers a different question. Lines compare series against
 * each other ("are licensed users growing faster than unlicensed ones?"); a stacked area shows the
 * total and its make-up at once ("how much Copilot is happening here, and who is doing it?"). For an
 * executive reading population mix, the second is usually the question.
 *
 * The trade-off is real and worth stating: only the bottom band is easy to read precisely, because
 * every band above it sits on a moving baseline. That is acceptable when the message is the mix and
 * the total, and it is why the plain line chart is kept alongside rather than replaced.
 */
export default function StackedAreaChart({
  series,
  valueLabel,
}: {
  series: ReportSeries[];
  valueLabel: string;
}) {
  const styles = useStyles();

  const withPoints = series.filter((s) => s.points.length > 0);
  if (withPoints.length === 0) {
    return <div className={styles.empty}>No data for this period.</div>;
  }

  // Series can have different week coverage, so build a shared spine and treat a missing week as
  // zero. Interpolating instead would invent activity that did not happen. Nulls are treated the
  // same way for the same reason - a gap in the import must not be smoothed over.
  const weeks = Array.from(new Set(withPoints.flatMap((s) => s.points.map((p) => p.weekStart)))).sort();

  const valueAt = (s: ReportSeries, week: string) =>
    s.points.find((p) => p.weekStart === week)?.value ?? 0;

  const totals = weeks.map((week) => withPoints.reduce((sum, s) => sum + valueAt(s, week), 0));
  const { max, ticks } = niceTicks(Math.max(...totals, 1));

  const plotW = W - MARGIN.left - MARGIN.right;
  const plotH = H - MARGIN.top - MARGIN.bottom;

  const xOf = (i: number) => MARGIN.left + (weeks.length === 1 ? plotW / 2 : (i / (weeks.length - 1)) * plotW);
  const yOf = (v: number) => MARGIN.top + plotH - (v / max) * plotH;

  // Cumulative baselines, bottom band first.
  let baseline = weeks.map(() => 0);
  const bands = withPoints.map((s) => {
    const lower = [...baseline];
    const upper = weeks.map((week, i) => lower[i] + valueAt(s, week));
    baseline = upper;
    return { series: s, lower, upper };
  });

  const labelEvery = Math.max(1, Math.ceil(weeks.length / 12));

  return (
    <div className={styles.root}>
      <svg viewBox={`0 0 ${W} ${H}`} className={styles.svg} role="img" aria-label={`${valueLabel} over time by population`}>
        <defs>
          {bands.map((band, i) => (
            <linearGradient key={band.series.name} id={`area-grad-${i}`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={seriesColor(i)} stopOpacity={0.95} />
              <stop offset="100%" stopColor={seriesColor(i)} stopOpacity={0.55} />
            </linearGradient>
          ))}
        </defs>

        {ticks.map((t) => (
          <g key={t}>
            <line
              x1={MARGIN.left}
              x2={W - MARGIN.right}
              y1={yOf(t)}
              y2={yOf(t)}
              stroke={tokens.colorNeutralStroke2}
              strokeDasharray="3 3"
            />
            <text x={MARGIN.left - 8} y={yOf(t) + 4} textAnchor="end" fontSize={12} fill={tokens.colorNeutralForeground3}>
              {formatCompact(t)}
            </text>
          </g>
        ))}

        {bands.map((band, i) => {
          const forward = band.upper.map((v, idx) => `${xOf(idx)},${yOf(v)}`);
          const back = band.lower.map((v, idx) => `${xOf(idx)},${yOf(v)}`).reverse();
          return (
            <polygon
              key={band.series.name}
              points={[...forward, ...back].join(' ')}
              fill={`url(#area-grad-${i})`}
              stroke={seriesColor(i)}
              strokeWidth={1}
            >
              <title>{band.series.name}</title>
            </polygon>
          );
        })}

        {weeks.map((week, i) =>
          i % labelEvery === 0 ? (
            <text
              key={week}
              x={xOf(i)}
              y={H - MARGIN.bottom + 18}
              textAnchor="middle"
              fontSize={12}
              fill={tokens.colorNeutralForeground3}
            >
              {formatWeek(week)}
            </text>
          ) : null,
        )}

        {/* Invisible hit targets: one per week, carrying the whole week's breakdown as a tooltip. */}
        {weeks.map((week, i) => (
          <rect
            key={`hit-${week}`}
            x={xOf(i) - plotW / Math.max(1, weeks.length) / 2}
            y={MARGIN.top}
            width={plotW / Math.max(1, weeks.length)}
            height={plotH}
            fill="transparent"
          >
            <title>
              {`Week of ${formatWeek(week)}\n` +
                withPoints.map((s) => `${s.name}: ${formatValue(valueAt(s, week))}`).join('\n') +
                `\nTotal: ${formatValue(totals[i])}`}
            </title>
          </rect>
        ))}

        <line x1={MARGIN.left} x2={W - MARGIN.right} y1={yOf(0)} y2={yOf(0)} stroke={tokens.colorNeutralStroke1} />
      </svg>

      <div className={styles.legend}>
        {bands.map((band, i) => (
          <span key={band.series.name} className={styles.legendItem}>
            <span className={styles.swatch} style={{ backgroundColor: seriesColor(i) }} />
            <Text size={200} className={styles.label}>
              {band.series.name}
            </Text>
          </span>
        ))}
      </div>
    </div>
  );
}
