import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { AdoptionIntensityPoint, CopilotAdoptionOptions } from '../../types/copilotAdoption';
import { formatValue, niceTicks } from '../charts/chartCommon';
import { scoreColour } from './adoptionShared';

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
});

const WIDTH = 720;
const HEIGHT = 340;
const PAD = { top: 34, right: 40, bottom: 44, left: 56 };
const MAX_RADIUS = 24;

/**
 * Headroom added to each axis before the ticks are chosen.
 *
 * Without it the largest bubble is centred exactly on the axis maximum, so its top half and the
 * label above it fall outside the viewBox and are clipped away - silently hiding the department the
 * reader most needs to see.
 */
const AXIS_HEADROOM = 1.2;

/**
 * Departments plotted as frequency (how many days a month they open Copilot) against intensity
 * (how much they do on each of those days), with the bubble sized by seats held.
 *
 * This is the chart that a single adoption percentage cannot replace. Two departments both showing
 * "70% adopted" can sit in opposite corners here: bottom-right is a department that opens Copilot
 * every day and asks it one thing (they need richer scenarios), top-left is a department that does
 * a lot with it but only occasionally (they need a reason to come back tomorrow). The recommended
 * intervention is different in each case, and the percentage on its own points to neither.
 */
export default function IntensityScatter({
  points,
  options,
}: {
  points: AdoptionIntensityPoint[];
  /** The band thresholds actually in use, so bubble colours agree with the bands everywhere else. */
  options: CopilotAdoptionOptions;
}) {
  const styles = useStyles();

  if (points.length === 0) {
    return (
      <div className={styles.empty}>
        Not enough licensed users in any department to plot. Departments need at least the minimum seat
        count to appear.
      </div>
    );
  }

  const xTicks = niceTicks(Math.max(...points.map((p) => p.activeDaysPerUser), 1) * AXIS_HEADROOM, 4);
  const yTicks = niceTicks(Math.max(...points.map((p) => p.actionsPerActiveDay), 1) * AXIS_HEADROOM, 4);
  const maxSeats = Math.max(...points.map((p) => p.licensedUsers), 1);

  const plotW = WIDTH - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;

  const xOf = (v: number) => PAD.left + (v / xTicks.max) * plotW;
  const yOf = (v: number) => PAD.top + plotH - (v / yTicks.max) * plotH;
  // Area-proportional: the radius is the square root of the seat share with no constant added, so a
  // department with four times the seats is drawn at four times the area. Adding a fixed minimum
  // radius to every bubble - the usual shortcut - would visually inflate the smallest departments
  // and make the chart overstate them.
  const rOf = (seats: number) => Math.max(3, MAX_RADIUS * Math.sqrt(seats / maxSeats));

  const bands = {
    champion: options.championScore,
    established: options.establishedScore,
    developing: options.developingScore,
  };

  return (
    <div className={styles.root}>
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} width="100%" role="img" aria-label="Copilot usage frequency against intensity by department">
        {yTicks.ticks.map((t) => (
          <g key={`y${t}`}>
            <line
              x1={PAD.left}
              x2={WIDTH - PAD.right}
              y1={yOf(t)}
              y2={yOf(t)}
              stroke={tokens.colorNeutralStroke2}
              strokeDasharray="3 3"
            />
            <text x={PAD.left - 8} y={yOf(t) + 4} textAnchor="end" fontSize={11} fill={tokens.colorNeutralForeground3}>
              {formatValue(t)}
            </text>
          </g>
        ))}

        {xTicks.ticks.map((t) => (
          <text
            key={`x${t}`}
            x={xOf(t)}
            y={HEIGHT - PAD.bottom + 16}
            textAnchor="middle"
            fontSize={11}
            fill={tokens.colorNeutralForeground3}
          >
            {formatValue(t)}
          </text>
        ))}

        <line
          x1={PAD.left}
          x2={WIDTH - PAD.right}
          y1={HEIGHT - PAD.bottom}
          y2={HEIGHT - PAD.bottom}
          stroke={tokens.colorNeutralStroke1}
        />
        <line x1={PAD.left} x2={PAD.left} y1={PAD.top} y2={HEIGHT - PAD.bottom} stroke={tokens.colorNeutralStroke1} />

        <text
          x={PAD.left + plotW / 2}
          y={HEIGHT - 6}
          textAnchor="middle"
          fontSize={11}
          fill={tokens.colorNeutralForeground3}
        >
          Active days per user, per month
        </text>
        <text
          x={-(PAD.top + plotH / 2)}
          y={14}
          transform="rotate(-90)"
          textAnchor="middle"
          fontSize={11}
          fill={tokens.colorNeutralForeground3}
        >
          Interactions per active day
        </text>

        {[...points]
          .sort((a, b) => b.licensedUsers - a.licensedUsers)
          .map((p) => (
            <g key={p.segment}>
              <circle
                cx={xOf(p.activeDaysPerUser)}
                cy={yOf(p.actionsPerActiveDay)}
                r={rOf(p.licensedUsers)}
                fill={scoreColour(p.activeUserAverageScore, bands)}
                fillOpacity={0.55}
                stroke={scoreColour(p.activeUserAverageScore, bands)}
              >
                <title>
                  {`${p.segment}: ${formatValue(p.licensedUsers)} seats, ${formatValue(p.activeUsers)} active. ` +
                    `${formatValue(p.activeDaysPerUser)} active days a month, ` +
                    `${formatValue(p.actionsPerActiveDay)} interactions per active day. ` +
                    `Average engagement of its active users ${formatValue(p.activeUserAverageScore)}.`}
                </title>
              </circle>
              <text
                x={xOf(p.activeDaysPerUser)}
                y={yOf(p.actionsPerActiveDay) - rOf(p.licensedUsers) - 5}
                textAnchor="middle"
                fontSize={11}
                fill={tokens.colorNeutralForeground2}
                style={{ pointerEvents: 'none' }}
              >
                {p.segment}
              </text>
            </g>
          ))}
      </svg>

      <Text size={200} className={styles.caption}>
        Bubble area is proportional to the number of seats the department holds; colour is the average
        engagement score of its <em>active</em> users, using the same bands as the rest of the page. Only
        users who were active at least once are averaged, so a department is not dragged towards the origin
        by seats that were never used - those are counted in the reclaimable-seat figure instead.
      </Text>
    </div>
  );
}
