import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportSeries } from '../../types/reports';
import { useT } from '../../i18n';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
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
  withheldLabel,
}: {
  series: ReportSeries[];
  valueLabel: string;
  /**
   * Opt-in: a week a series reports as null is withheld rather than zero (#844). It is drawn as a gap in every band, and its
   * tooltip reads this label instead of a total. Without it, a null counts as zero, as a missing week does.
   */
  withheldLabel?: string;
}) {
  const t = useT();
  const styles = useStyles();

  const withPoints = series.filter((s) => s.points.length > 0);
  if (withPoints.length === 0) {
    return <div className={styles.empty}>{t('charts.empty.noDataForPeriod')}</div>;
  }

  // Series can have different week coverage, so build a shared spine and treat a missing week as
  // zero. Interpolating instead would invent activity that did not happen. Nulls are treated the
  // same way for the same reason - a gap in the import must not be smoothed over - unless the caller
  // says a null is a withheld figure, which is never drawn as a zero.
  const weeks = Array.from(new Set(withPoints.flatMap((s) => s.points.map((p) => p.weekStart)))).sort();

  const valueAt = (s: ReportSeries, week: string) =>
    s.points.find((p) => p.weekStart === week)?.value ?? 0;

  const withheldWeeks = new Set(
    withheldLabel
      ? weeks.filter((week) => withPoints.some((s) => s.points.some((p) => p.weekStart === week && p.value === null)))
      : [],
  );

  const totals = weeks.map((week) =>
    withheldWeeks.has(week) ? 0 : withPoints.reduce((sum, s) => sum + valueAt(s, week), 0),
  );
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

  // The runs of consecutive weeks drawn: every week, unless some are withheld. A run of a single week between two
  // withheld ones is drawn a few pixels wide, so it can be seen.
  const runs: number[][] = [];
  let run: number[] = [];
  weeks.forEach((week, i) => {
    if (withheldWeeks.has(week)) {
      if (run.length > 0) runs.push(run);
      run = [];
    } else {
      run.push(i);
    }
  });
  if (run.length > 0) runs.push(run);
  const pad = withheldWeeks.size > 0 ? 3 : 0;

  const labelEvery = Math.max(1, Math.ceil(weeks.length / 12));

  return (
    <div className={styles.root}>
      <svg viewBox={`0 0 ${W} ${H}`} className={styles.svg} role="img" aria-label={t('charts.stackedArea.ariaLabel', { valueLabel })}>
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

        {bands.map((band, i) =>
          runs.map((indexes, r) => {
            const single = indexes.length === 1 ? pad : 0;
            const forward = indexes.flatMap((idx) =>
              single > 0
                ? [`${xOf(idx) - single},${yOf(band.upper[idx])}`, `${xOf(idx) + single},${yOf(band.upper[idx])}`]
                : [`${xOf(idx)},${yOf(band.upper[idx])}`],
            );
            const back = indexes
              .flatMap((idx) =>
                single > 0
                  ? [`${xOf(idx) - single},${yOf(band.lower[idx])}`, `${xOf(idx) + single},${yOf(band.lower[idx])}`]
                  : [`${xOf(idx)},${yOf(band.lower[idx])}`],
              )
              .reverse();
            return (
              <polygon
                key={runs.length === 1 ? band.series.name : `${band.series.name}-${r}`}
                points={[...forward, ...back].join(' ')}
                fill={`url(#area-grad-${i})`}
                stroke={seriesColor(i)}
                strokeWidth={1}
              >
                <title>{serverPlaceholderText(t, band.series.name)}</title>
              </polygon>
            );
          }),
        )}

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
              {withheldWeeks.has(week)
                ? t('charts.stackedArea.weekOf', { week: formatWeek(week) }) + '\n' + withheldLabel
                : t('charts.stackedArea.weekOf', { week: formatWeek(week) }) + '\n' +
                  withPoints.map((s) => `${serverPlaceholderText(t, s.name)}: ${formatValue(valueAt(s, week))}`).join('\n') +
                  `\n${t('charts.stackedArea.total', { value: formatValue(totals[i]) })}`}
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
              {serverPlaceholderText(t, band.series.name)}
            </Text>
          </span>
        ))}
      </div>
    </div>
  );
}
