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
    gap: '8px',
    minWidth: '180px',
    flexGrow: 1,
  },
  legendRow: {
    display: 'grid',
    gridTemplateColumns: 'auto 1fr auto',
    gap: '8px',
    alignItems: 'center',
  },
  swatch: {
    width: '10px',
    height: '10px',
    borderRadius: '2px',
  },
  label: {
    color: tokens.colorNeutralForeground2,
  },
  value: {
    fontVariantNumeric: 'tabular-nums',
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
 */
export default function RadarChart({
  axes,
  series,
  size = 250,
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
  const radius = centre - 34;
  const rings = 4;

  // First axis points straight up: an unrotated radar starts at 3 o'clock, which makes the shape
  // read as tilted and costs the reader a moment working out which spoke is which.
  const angleFor = (i: number) => (i / axes.length) * 2 * Math.PI - Math.PI / 2;

  const pointFor = (i: number, value: number) => {
    const ratio = Math.max(0, Math.min(1, value / maxValue));
    const angle = angleFor(i);
    return { x: centre + radius * ratio * Math.cos(angle), y: centre + radius * ratio * Math.sin(angle) };
  };

  const polygonFor = (values: number[]) =>
    values.map((v, i) => { const p = pointFor(i, v); return `${p.x},${p.y}`; }).join(' ');

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
          const labelPoint = pointFor(i, maxValue * 1.2);
          return (
            <g key={axis}>
              <line x1={centre} y1={centre} x2={outer.x} y2={outer.y} stroke={tokens.colorNeutralStroke2} />
              <text
                x={labelPoint.x}
                y={labelPoint.y + 4}
                textAnchor={labelPoint.x > centre + 4 ? 'start' : labelPoint.x < centre - 4 ? 'end' : 'middle'}
                fontSize={12}
                fill={tokens.colorNeutralForeground2}
              >
                {axis}
              </text>
            </g>
          );
        })}

        {series.map((s) => (
          <g key={s.name}>
            <polygon points={polygonFor(s.values)} fill={s.colour} fillOpacity={0.22} stroke={s.colour} strokeWidth={2} />
            {s.values.map((v, i) => {
              const p = pointFor(i, v);
              return (
                <circle key={i} cx={p.x} cy={p.y} r={3.5} fill={s.colour}>
                  <title>{`${s.name} - ${axes[i]}: ${Math.round(v)}`}</title>
                </circle>
              );
            })}
          </g>
        ))}
      </svg>

      <div className={styles.legend}>
        {series.map((s) => (
          <div key={s.name} className={styles.legendRow}>
            <span className={styles.swatch} style={{ backgroundColor: s.colour }} />
            <Text size={200} className={styles.label}>
              {s.name}
            </Text>
            <Text size={200} weight="semibold" className={styles.value}>
              {s.values.map((v) => Math.round(v)).join(' / ')}
            </Text>
          </div>
        ))}
        <Text size={100} className={styles.label}>
          Values in axis order: {axes.join(' / ')}. All three are 0-100 and share one scale, so the shape is
          directly comparable.
        </Text>
      </div>
    </div>
  );
}
