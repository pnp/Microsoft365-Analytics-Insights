import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { CoworkQuadrantPoint, CopilotAdoptionOptions } from '../../types/copilotAdoption';

const WIDTH = 720;
const HEIGHT = 380;
const PAD = { top: 30, right: 30, bottom: 52, left: 62 };
const MAX_RADIUS = 26;
const MIN_RADIUS = 6;

/** Colour per quadrant. Kept distinct in greyscale too - see `quadrantInitial`. */
const QUADRANT_COLOURS = {
  ready: '#107c10',
  coach: '#f7a800',
  lowLoad: '#8a8886',
  neither: '#c8c6c4',
};

const useStyles = makeStyles({
  root: {
    width: '100%',
    paddingTop: '4px',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
  caption: {
    color: tokens.colorNeutralForeground3,
    marginTop: '6px',
    display: 'block',
  },
  legend: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '14px',
    alignItems: 'center',
    marginTop: '8px',
  },
  legendItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
    color: tokens.colorNeutralForeground2,
    fontSize: '12px',
  },
  legendSwatch: {
    width: '16px',
    height: '16px',
    borderRadius: '50%',
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#ffffff',
    fontSize: '10px',
    fontWeight: 700,
  },
});

type Quadrant = 'ready' | 'coach' | 'lowLoad' | 'neither';

function quadrantOf(point: CoworkQuadrantPoint, loadBar: number, fluencyBar: number): Quadrant {
  const loaded = point.coordinationLoadScore >= loadBar;
  const fluent = point.fluencyScore >= fluencyBar;

  if (loaded && fluent) return 'ready';
  if (loaded) return 'coach';
  if (fluent) return 'lowLoad';
  return 'neither';
}

/**
 * A second, non-colour encoding of the quadrant.
 *
 * Colour alone fails for roughly one man in twelve, and fails completely in greyscale - which is
 * normal for a chart whose entire audience is people looking at a deck in a meeting. Same reasoning
 * as the band initials on the intensity scatter.
 */
const QUADRANT_KEY: Array<{ id: Quadrant; initial: string; label: string; meaning: string }> = [
  { id: 'ready', initial: 'R', label: 'Ready now', meaning: 'Fluent and loaded - enable Cowork here first' },
  { id: 'coach', initial: 'C', label: 'Coach first', meaning: 'Has the work, needs the Copilot habit' },
  { id: 'lowLoad', initial: 'L', label: 'Low load', meaning: 'Fluent, but little to delegate' },
  { id: 'neither', initial: '-', label: 'Not indicated', meaning: 'Below both bars' },
];

/**
 * Departments plotted as coordination load (how much delegable, multi-step work they carry) against
 * Copilot fluency (whether they are practised enough to hand it over), with the bubble sized by
 * Copilot seats.
 *
 * This is the chart the whole Cowork case rests on, and it exists because neither axis means anything
 * alone. A department full of Copilot experts with no coordination load has nothing for Cowork to
 * absorb; a department drowning in meetings that has never formed a Copilot habit will not delegate
 * to an agent just because you switched one on. Only the top-right corner is both, and that is the
 * only corner where spending credits is likely to pay back.
 *
 * <b>Every point here is a prediction except where a department already has Cowork users</b>, which is
 * why the tooltip reports the observed count separately from the inferred position.
 */
export default function CoworkQuadrant({
  points,
  options,
}: {
  points: CoworkQuadrantPoint[];
  /** The bars actually in use, so the dividing lines match the tiering everywhere else. */
  options: CopilotAdoptionOptions;
}) {
  const styles = useStyles();

  if (points.length === 0) {
    return (
      <div className={styles.empty}>
        Not enough Copilot seats in any one department to plot. Departments need at least the minimum
        seat count to appear, so a small tenant legitimately shows nothing here.
      </div>
    );
  }

  const loadBar = options.coworkLoadMinScore;
  const fluencyBar = options.coworkFluencyMinScore;

  // Both axes are anchored at 0-100 rather than fitted to the data, which is the opposite of the
  // intensity scatter. These are scores against a FIXED bar, and the bar's position is the entire
  // message - fitting the axes would move the dividing lines around between tenants and make two
  // reports incomparable.
  const plotWidth = WIDTH - PAD.left - PAD.right;
  const plotHeight = HEIGHT - PAD.top - PAD.bottom;
  const x = (value: number) => PAD.left + (Math.max(0, Math.min(100, value)) / 100) * plotWidth;
  const y = (value: number) => PAD.top + plotHeight - (Math.max(0, Math.min(100, value)) / 100) * plotHeight;

  const maxSeats = Math.max(...points.map((p) => p.licensedUsers), 1);
  const radius = (seats: number) =>
    MIN_RADIUS + Math.sqrt(Math.max(0, seats) / maxSeats) * (MAX_RADIUS - MIN_RADIUS);

  const barX = x(loadBar);
  const barY = y(fluencyBar);

  // Largest bubbles drawn first so a small department is never hidden behind a big one.
  const ordered = [...points].sort((a, b) => b.licensedUsers - a.licensedUsers);

  return (
    <div className={styles.root}>
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        width="100%"
        role="img"
        aria-label="Departments plotted by coordination load against Copilot fluency"
      >
        {/* Quadrant tint. Only the ready corner is tinted - shading all four would turn the plot into
            a colour field and bury the bubbles that carry the data. */}
        <rect
          x={barX}
          y={PAD.top}
          width={PAD.left + plotWidth - barX}
          height={barY - PAD.top}
          fill={QUADRANT_COLOURS.ready}
          opacity={0.07}
        />

        {/* Axis frame */}
        <line x1={PAD.left} y1={PAD.top} x2={PAD.left} y2={PAD.top + plotHeight} stroke={tokens.colorNeutralStroke2} />
        <line
          x1={PAD.left}
          y1={PAD.top + plotHeight}
          x2={PAD.left + plotWidth}
          y2={PAD.top + plotHeight}
          stroke={tokens.colorNeutralStroke2}
        />

        {/* The bars. Dashed so they read as thresholds rather than as data. */}
        <line
          x1={barX}
          y1={PAD.top}
          x2={barX}
          y2={PAD.top + plotHeight}
          stroke={tokens.colorNeutralForeground3}
          strokeDasharray="4 4"
        />
        <line
          x1={PAD.left}
          y1={barY}
          x2={PAD.left + plotWidth}
          y2={barY}
          stroke={tokens.colorNeutralForeground3}
          strokeDasharray="4 4"
        />

        <text x={barX + 6} y={PAD.top + 12} fontSize="11" fill={tokens.colorNeutralForeground3}>
          Enough work to delegate &#8594;
        </text>
        <text x={PAD.left + 4} y={barY - 6} fontSize="11" fill={tokens.colorNeutralForeground3}>
          Fluent enough to delegate &#8593;
        </text>

        {/* Corner label for the only quadrant that is an instruction. */}
        <text
          x={PAD.left + plotWidth - 6}
          y={PAD.top + 16}
          fontSize="12"
          fontWeight="600"
          textAnchor="end"
          fill={QUADRANT_COLOURS.ready}
        >
          Ready for Cowork
        </text>

        {[0, 25, 50, 75, 100].map((tick) => (
          <g key={`tick-${tick}`}>
            <text x={PAD.left - 8} y={y(tick) + 4} fontSize="10" textAnchor="end" fill={tokens.colorNeutralForeground3}>
              {tick}
            </text>
            <text
              x={x(tick)}
              y={PAD.top + plotHeight + 16}
              fontSize="10"
              textAnchor="middle"
              fill={tokens.colorNeutralForeground3}
            >
              {tick}
            </text>
          </g>
        ))}

        <text
          x={PAD.left + plotWidth / 2}
          y={HEIGHT - 8}
          fontSize="11"
          textAnchor="middle"
          fill={tokens.colorNeutralForeground2}
        >
          Coordination load (0-100)
        </text>
        <text
          x={14}
          y={PAD.top + plotHeight / 2}
          fontSize="11"
          textAnchor="middle"
          fill={tokens.colorNeutralForeground2}
          transform={`rotate(-90 14 ${PAD.top + plotHeight / 2})`}
        >
          Copilot fluency (0-100)
        </text>

        {ordered.map((point) => {
          const quadrant = quadrantOf(point, loadBar, fluencyBar);
          const key = QUADRANT_KEY.find((q) => q.id === quadrant);
          const r = radius(point.licensedUsers);
          const cx = x(point.coordinationLoadScore);
          const cy = y(point.fluencyScore);

          return (
            <g key={point.segment}>
              <title>
                {`${point.segment}\n` +
                  `${point.licensedUsers} Copilot seat(s)\n` +
                  `Coordination load ${Math.round(point.coordinationLoadScore)}/100\n` +
                  `Copilot fluency ${Math.round(point.fluencyScore)}/100\n` +
                  `Already using Cowork regularly (observed): ${point.regularCoworkUsers}\n` +
                  `Prime candidates (predicted): ${point.primeCandidates}`}
              </title>
              <circle
                cx={cx}
                cy={cy}
                r={r}
                fill={QUADRANT_COLOURS[quadrant]}
                opacity={0.68}
                stroke={tokens.colorNeutralBackground1}
                strokeWidth={1.5}
              />
              {r >= 11 && (
                <text
                  x={cx}
                  y={cy + 4}
                  fontSize="11"
                  fontWeight="700"
                  textAnchor="middle"
                  fill="#ffffff"
                  pointerEvents="none"
                >
                  {key?.initial}
                </text>
              )}
            </g>
          );
        })}
      </svg>

      <div className={styles.legend}>
        {QUADRANT_KEY.map((entry) => (
          <span key={entry.id} className={styles.legendItem}>
            <span className={styles.legendSwatch} style={{ backgroundColor: QUADRANT_COLOURS[entry.id] }}>
              {entry.initial}
            </span>
            {entry.label} - {entry.meaning}
          </span>
        ))}
      </div>

      <Text size={200} className={styles.caption}>
        Bubble size is the number of Copilot seats in the department, so a large slow department outranks a
        tiny keen one. Both axes are fixed at 0-100 rather than fitted to your data, so the dividing lines
        sit in the same place in every report and two runs can be compared directly. A department&#8217;s
        position is a <strong>prediction</strong> built from its workload and Copilot use - only the
        &#8220;already using Cowork&#8221; figure in the tooltip is observed.
      </Text>
    </div>
  );
}
