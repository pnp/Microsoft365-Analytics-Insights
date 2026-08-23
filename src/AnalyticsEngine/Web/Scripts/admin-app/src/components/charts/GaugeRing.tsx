import { makeStyles, tokens, Text } from '@fluentui/react-components';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '2px',
  },
  svg: {
    display: 'block',
    overflow: 'visible',
  },
  caption: {
    color: tokens.colorNeutralForeground3,
    textAlign: 'center',
    maxWidth: '220px',
  },
});

export type GaugeBand = { upTo: number; colour: string; label: string };

/**
 * The default judgement scale for an adoption-style percentage. Deliberately not a smooth gradient:
 * a continuous ramp implies the difference between 41% and 43% means something, and it does not.
 * Three bands say what the number actually is - a problem, a work in progress, or fine.
 */
export const ADOPTION_BANDS: GaugeBand[] = [
  { upTo: 40, colour: '#d13438', label: 'Needs attention' },
  { upTo: 70, colour: '#c19c00', label: 'Progressing' },
  { upTo: 100, colour: '#107c10', label: 'Healthy' },
];

/**
 * A 240-degree arc gauge for a single 0-100 percentage.
 *
 * Used for the two rates an executive reads first. A bare number gives no sense of whether it is
 * good; a gauge answers "compared to what?" in the same glance, because the coloured track carries
 * the judgement scale with it. The threshold ticks are drawn on the arc so the bands are visible
 * rather than implied by the needle's colour alone.
 */
export default function GaugeRing({
  value,
  label,
  sublabel,
  bands = ADOPTION_BANDS,
  size = 168,
}: {
  /** 0-100. Values outside that range are clamped rather than drawn off the arc. */
  value: number;
  label: string;
  sublabel?: string;
  bands?: GaugeBand[];
  size?: number;
}) {
  const styles = useStyles();

  const clamped = Math.max(0, Math.min(100, value));
  const radius = size / 2;
  const stroke = Math.max(12, size * 0.1);
  const r = radius - stroke / 2 - 2;
  // The band scale sits just inside the main arc, concentric with it. Drawing it at the same radius
  // and nudging it with a transform - which is what this used to do - shifts it off the arc centre,
  // so the colour boundaries no longer line up with the value they are supposed to mark.
  const bandRadius = r - stroke / 2 - 4;

  // A 240-degree sweep starting bottom-left. The open bottom is what makes it read as a gauge
  // rather than as a pie with a bite taken out of it.
  const START = 150;
  const SWEEP = 240;

  const pointOnArc = (pct: number, atRadius: number) => {
    const angle = ((START + (pct / 100) * SWEEP) * Math.PI) / 180;
    return { x: radius + atRadius * Math.cos(angle), y: radius + atRadius * Math.sin(angle) };
  };

  const arcPath = (fromPct: number, toPct: number, atRadius: number) => {
    const from = pointOnArc(fromPct, atRadius);
    const to = pointOnArc(toPct, atRadius);
    const large = ((toPct - fromPct) / 100) * SWEEP > 180 ? 1 : 0;
    return `M ${from.x} ${from.y} A ${atRadius} ${atRadius} 0 ${large} 1 ${to.x} ${to.y}`;
  };

  const activeBand = bands.find((b) => clamped <= b.upTo) ?? bands[bands.length - 1];
  const needle = pointOnArc(clamped, r);

  return (
    <div className={styles.root}>
      <svg width={size} height={size * 0.82} viewBox={`0 0 ${size} ${size * 0.82}`} className={styles.svg} role="img" aria-label={`${label}: ${clamped}%`}>
        <path d={arcPath(0, 100, r)} fill="none" stroke={tokens.colorNeutralBackground3} strokeWidth={stroke} strokeLinecap="round" />

        {bands.map((band, i) => {
          const from = i === 0 ? 0 : bands[i - 1].upTo;
          return (
            <path
              key={band.label}
              d={arcPath(from, band.upTo, bandRadius)}
              fill="none"
              stroke={band.colour}
              strokeWidth={4}
              strokeOpacity={0.45}
            />
          );
        })}

        <path
          d={arcPath(0, Math.max(0.6, clamped), r)}
          fill="none"
          stroke={activeBand.colour}
          strokeWidth={stroke}
          strokeLinecap="round"
        />

        <circle cx={needle.x} cy={needle.y} r={stroke * 0.42} fill="#ffffff" stroke={activeBand.colour} strokeWidth={3} />

        <text
          x={radius}
          y={radius + 2}
          textAnchor="middle"
          fontSize={size * 0.2}
          fontWeight={600}
          fill={tokens.colorNeutralForeground1}
          style={{ fontVariantNumeric: 'tabular-nums' }}
        >
          {Math.round(clamped)}%
        </text>
        <text x={radius} y={radius + size * 0.15} textAnchor="middle" fontSize={size * 0.075} fill={activeBand.colour}>
          {activeBand.label}
        </text>
      </svg>

      <Text size={300} weight="semibold">
        {label}
      </Text>
      {sublabel && (
        <Text size={200} className={styles.caption}>
          {sublabel}
        </Text>
      )}
    </div>
  );
}
