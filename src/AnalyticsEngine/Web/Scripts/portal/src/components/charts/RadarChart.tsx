import { Fragment } from 'react';
import { makeStyles, tokens, Text } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    alignItems: 'center',
    gap: '20px',
    flexWrap: 'wrap',
    paddingTop: '4px',
  },
  svg: {
    display: 'block',
    overflow: 'visible',
  },
  legend: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    minWidth: '250px',
    flexGrow: 1,
  },
  table: {
    display: 'grid',
    columnGap: '16px',
    rowGap: '5px',
    alignItems: 'center',
  },
  headCell: {
    color: tokens.colorNeutralForeground3,
    textAlign: 'right',
    whiteSpace: 'nowrap',
  },
  headCellKey: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    justifyContent: 'flex-end',
    whiteSpace: 'nowrap',
  },
  axisCell: {
    color: tokens.colorNeutralForeground2,
    whiteSpace: 'nowrap',
  },
  value: {
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
  },
  gap: {
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    color: tokens.colorNeutralForeground3,
  },
  swatch: {
    width: '14px',
    height: '3px',
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

export type RadarAxis = { label: string; value: number };
export type RadarSeries = { name: string; colour: string; values: number[] };

/**
 * A radar (spider) chart over a small fixed set of axes, all on the same 0-100 scale.
 *
 * Used for the engagement score's three components. Its job is to show the *shape* of adoption
 * rather than its level: two tenants can average the same score while one is frequent-but-shallow
 * and the other deep-but-narrow, and those need completely different programmes. Three bars would
 * carry the same numbers but not the shape, which is the thing worth recognising at a glance.
 *
 * Deliberately limited to a handful of axes on one shared scale. A radar with a dozen axes, or with
 * axes on different units, is decorative rather than informative - it is very easy to make one that
 * looks impressive and says nothing.
 *
 * Three specific things keep it readable, because the naive version of this chart is a mess:
 *
 * 1. **Only the first series is filled.** Two translucent polygons overlapping produce a third,
 *    muddy colour exactly where the reader is trying to compare them. The benchmark series is drawn
 *    as a dashed outline instead - which also means the two are told apart by line style and not by
 *    colour alone, so it survives being printed or read by someone colour-blind.
 * 2. **The rings are labelled.** An unlabelled web of gridlines gives no sense of whether the shape
 *    is big or small; the reader cannot tell 40 from 80 without a number to anchor against.
 * 3. **Every value is printed in the adjacent table, per axis, with the gap.** Radar area grows with
 *    the square of the values, so a series twice as good looks four times as big - reading the size
 *    of the shape systematically overstates the difference. The shape is there to be *recognised*;
 *    the table is there to be *read*. No conclusion depends on estimating an area.
 */
export default function RadarChart({
  axes,
  series,
  size = 260,
  maxValue = 100,
}: {
  axes: string[];
  series: RadarSeries[];
  size?: number;
  maxValue?: number;
}) {
  const styles = useStyles();

  if (axes.length < 3 || series.length === 0) {
    return <div className={styles.empty}>Not enough data to plot.</div>;
  }

  const centre = size / 2;
  const radius = centre - 42;
  const rings = 4;

  // First axis points straight up: an unrotated radar starts at 3 o'clock, which makes the shape
  // read as tilted and costs the reader a moment working out which spoke is which.
  const angleFor = (i: number) => (i / axes.length) * 2 * Math.PI - Math.PI / 2;

  /**
   * Position at an arbitrary fraction of the radius, NOT clamped to the plot area.
   *
   * Separate from pointFor because pointFor clamps to [0, 1] - correct for a data point, which must
   * never be drawn outside the outermost ring, but fatal for the axis labels: asking pointFor for
   * 1.19x the radius silently returned exactly 1x, so every axis label was drawn on its own outer
   * vertex. On the vertical axis that put the label directly underneath the "100" ring label.
   */
  const pointAtRatio = (i: number, ratio: number) => {
    const angle = angleFor(i);
    return { x: centre + radius * ratio * Math.cos(angle), y: centre + radius * ratio * Math.sin(angle) };
  };

  const pointFor = (i: number, value: number) =>
    pointAtRatio(i, Math.max(0, Math.min(1, value / maxValue)));

  const polygonFor = (values: number[]) =>
    values.map((v, i) => { const p = pointFor(i, v); return `${p.x},${p.y}`; }).join(' ');

  // Two series is the case this chart is built for (typical user against the benchmark), and it is
  // the only case where a per-axis difference is meaningful rather than ambiguous.
  const showGap = series.length === 2;
  const columns = `1fr repeat(${series.length + (showGap ? 1 : 0)}, auto)`;

  return (
    <div className={styles.root}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} className={styles.svg} role="img" aria-label="Engagement component profile">
        {Array.from({ length: rings }, (_, ring) => {
          const ratio = (ring + 1) / rings;
          return (
            <polygon
              key={ring}
              points={axes.map((_, i) => { const p = pointFor(i, maxValue * ratio); return `${p.x},${p.y}`; }).join(' ')}
              fill="none"
              stroke={tokens.colorNeutralStroke2}
              strokeDasharray={ring === rings - 1 ? undefined : '3 3'}
            />
          );
        })}

        {axes.map((axis, i) => {
          const outer = pointFor(i, maxValue);
          const labelPoint = pointAtRatio(i, 1.19);
          // The ring labels run up the vertical axis from the centre, so a label sitting on the top
          // spoke has to clear them: it is nudged further out and anchored to the end of its text.
          const onVerticalAxis = Math.abs(labelPoint.x - centre) < 4;
          return (
            <g key={axis}>
              <line x1={centre} y1={centre} x2={outer.x} y2={outer.y} stroke={tokens.colorNeutralStroke2} />
              <text
                x={labelPoint.x}
                y={onVerticalAxis && labelPoint.y < centre ? labelPoint.y - 4 : labelPoint.y + 4}
                textAnchor={labelPoint.x > centre + 4 ? 'start' : labelPoint.x < centre - 4 ? 'end' : 'middle'}
                fontSize={12}
                fill={tokens.colorNeutralForeground2}
              >
                {axis}
              </text>
            </g>
          );
        })}

        {/* Ring values. Without these the gridlines say only "there are rings", and the reader has
            no anchor for whether a shape is big or small. Painted with a background-coloured halo so
            they stay legible where they sit on top of a gridline or a series outline. */}
        {Array.from({ length: rings }, (_, ring) => {
          const value = (maxValue * (ring + 1)) / rings;
          return (
            <text
              key={ring}
              x={centre + 5}
              y={centre - (radius * (ring + 1)) / rings + 3.5}
              fontSize={9.5}
              fill={tokens.colorNeutralForeground3}
              stroke={tokens.colorNeutralBackground1}
              strokeWidth={3}
              paintOrder="stroke"
            >
              {Math.round(value)}
            </text>
          );
        })}

        {series.map((s, si) => {
          // Only the first series is filled - see the note on the component. Later series are dashed
          // outlines, which reads as "the line to reach" and never muddies the colour underneath.
          const filled = si === 0;
          return (
            <g key={s.name}>
              <polygon
                points={polygonFor(s.values)}
                fill={filled ? s.colour : 'none'}
                fillOpacity={filled ? 0.2 : 0}
                stroke={s.colour}
                strokeWidth={2}
                strokeDasharray={filled ? undefined : '5 3'}
                strokeLinejoin="round"
              />
              {s.values.map((v, i) => {
                const p = pointFor(i, v);
                return (
                  <circle key={i} cx={p.x} cy={p.y} r={3.5} fill={filled ? s.colour : tokens.colorNeutralBackground1} stroke={s.colour} strokeWidth={filled ? 0 : 2}>
                    <title>{`${s.name} - ${axes[i]}: ${Math.round(v)}`}</title>
                  </circle>
                );
              })}
            </g>
          );
        })}
      </svg>

      <div className={styles.legend}>
        <div className={styles.table} style={{ gridTemplateColumns: columns }}>
          <span />
          {series.map((s, si) => (
            <div key={s.name} className={styles.headCellKey}>
              <span
                className={styles.swatch}
                style={{
                  backgroundColor: si === 0 ? s.colour : 'transparent',
                  backgroundImage: si === 0 ? undefined : `repeating-linear-gradient(to right, ${s.colour} 0 5px, transparent 5px 8px)`,
                }}
              />
              <Text size={200} className={styles.label}>
                {s.name}
              </Text>
            </div>
          ))}
          {showGap && (
            <Text size={200} className={styles.headCell}>
              Gap
            </Text>
          )}

          {axes.map((axis, i) => (
            <Fragment key={axis}>
              <Text size={200} className={styles.axisCell}>
                {axis}
              </Text>
              {series.map((s) => (
                <Text key={s.name} size={200} weight="semibold" className={styles.value}>
                  {Math.round(s.values[i] ?? 0)}
                </Text>
              ))}
              {showGap && (
                <Text size={200} className={styles.gap}>
                  {formatGap(Math.round(series[1].values[i] ?? 0) - Math.round(series[0].values[i] ?? 0))}
                </Text>
              )}
            </Fragment>
          ))}
        </div>

        <Text size={100} className={styles.label}>
          All {axes.length} components are 0-{maxValue} and share one scale, so the two outlines are directly
          comparable. Read the table for the sizes - the area of a radar grows with the square of its
          values, so the shape overstates the difference.
        </Text>
      </div>
    </div>
  );
}

/** Signed so a reader can see at a glance which way round the difference runs. */
function formatGap(value: number) {
  if (value === 0) return '0';
  return value > 0 ? `+${value}` : `${value}`;
}
