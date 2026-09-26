import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { AdoptionIntensityPoint, CopilotAdoptionOptions } from '../../types/copilotAdoption';
import { useT, type TranslationKey } from '../../i18n';
import { formatValue } from '../charts/chartCommon';
import { serverPlaceholderText } from '../shared/serverPlaceholder';
import { scoreColour } from './adoptionShared';

/**
 * A second, non-colour encoding of the engagement band.
 *
 * Colour alone fails for roughly one man in twelve, and fails completely when this page is printed,
 * pasted into a deck in greyscale, or read on a projector with the contrast turned down - all of
 * which are normal for a chart whose whole audience is people in meetings. The initial is drawn
 * inside the bubble wherever it fits, and the legend below repeats the pairing.
 */
function bandInitial(
  score: number,
  bands: { champion: number; established: number; developing: number },
): string {
  if (score >= bands.champion) return 'C';
  if (score >= bands.established) return 'E';
  if (score >= bands.developing) return 'D';
  if (score > 0) return 'T';
  return '-';
}

const BAND_KEY: Array<{ initial: string; labelKey: TranslationKey; score: (b: ScatterBands) => number }> = [
  { initial: 'C', labelKey: 'copilotAdoption.intensityScatter.band.champion', score: (b) => b.champion },
  { initial: 'E', labelKey: 'copilotAdoption.intensityScatter.band.established', score: (b) => b.established },
  { initial: 'D', labelKey: 'copilotAdoption.intensityScatter.band.developing', score: (b) => b.developing },
  { initial: 'T', labelKey: 'copilotAdoption.intensityScatter.band.trialling', score: () => 1 },
];

type ScatterBands = { champion: number; established: number; developing: number };

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

const WIDTH = 720;
const HEIGHT = 340;
const PAD = { top: 34, right: 40, bottom: 44, left: 56 };
const MAX_RADIUS = 24;

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
  const t = useT();

  if (points.length === 0) {
    return (
      <div className={styles.empty}>
        {t('copilotAdoption.intensityScatter.empty')}
      </div>
    );
  }

  // Axes are fitted to the data rather than anchored at zero. These are positions for comparison,
  // not magnitudes, so forcing a zero origin wastes most of the plot: a tenant whose departments all
  // sit between 4 and 7 active days gets every bubble squashed into one corner, which is exactly the
  // shape this chart exists to spread out. The padding keeps points off the axis lines.
  const fitAxis = (values: number[], maxRadiusUnits: number) => {
    const lo = Math.min(...values);
    const hi = Math.max(...values);
    const span = hi - lo;
    // A single point, or several identical ones, has no span to fit - fall back to a window around
    // the value so it lands mid-plot instead of on a degenerate axis.
    const pad = span > 0 ? span * 0.35 + maxRadiusUnits : Math.max(1, Math.abs(hi) * 0.5);
    return { min: Math.max(0, lo - pad), max: hi + pad };
  };

  const xAxis = fitAxis(points.map((p) => p.activeDaysPerUser), 0.5);
  const yAxis = fitAxis(points.map((p) => p.actionsPerActiveDay), 0.5);
  const maxSeats = Math.max(...points.map((p) => p.licensedUsers), 1);

  const plotW = WIDTH - PAD.left - PAD.right;
  const plotH = HEIGHT - PAD.top - PAD.bottom;

  const xOf = (v: number) => PAD.left + ((v - xAxis.min) / (xAxis.max - xAxis.min || 1)) * plotW;
  const yOf = (v: number) => PAD.top + plotH - ((v - yAxis.min) / (yAxis.max - yAxis.min || 1)) * plotH;

  const axisTicks = (axis: { min: number; max: number }) => {
    const step = (axis.max - axis.min) / 4;
    return Array.from({ length: 5 }, (_, i) => axis.min + step * i);
  };

  const xTickValues = axisTicks(xAxis);
  const yTickValues = axisTicks(yAxis);

  // Area-proportional: the radius is the square root of the seat share with no constant added, so a
  // department with four times the seats is drawn at four times the area. Adding a fixed minimum
  // radius to every bubble - the usual shortcut - would visually inflate the smallest departments
  // and make the chart overstate them.
  const rOf = (seats: number) => Math.max(3, MAX_RADIUS * Math.sqrt(seats / maxSeats));

  const bands: ScatterBands = {
    champion: options.championScore,
    established: options.establishedScore,
    developing: options.developingScore,
  };

  const medianOf = (values: number[]) => {
    const sorted = [...values].sort((a, b) => a - b);
    const mid = Math.floor(sorted.length / 2);
    return sorted.length % 2 === 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
  };

  const xMedian = medianOf(points.map((p) => p.activeDaysPerUser));
  const yMedian = medianOf(points.map((p) => p.actionsPerActiveDay));
  const showQuadrants = points.length >= 4;

  // Labels are placed one at a time, largest bubble first, and any that cannot find a free slot is
  // dropped. Overlapping department names are worse than missing ones: an unreadable pile of text in
  // the middle of the plot obscures the bubbles as well as itself, and every name is still on the
  // hover tooltip. Largest-first means the departments that matter most keep their labels.
  const placed: { x: number; y: number; w: number; h: number }[] = [];
  const LABEL_H = 13;

  const tryPlace = (x: number, y: number, w: number) => {
    const box = { x: x - w / 2, y: y - LABEL_H, w, h: LABEL_H };
    const clashes = placed.some(
      (p) => box.x < p.x + p.w && box.x + box.w > p.x && box.y < p.y + p.h && box.y + box.h > p.y,
    );
    if (clashes) return false;
    placed.push(box);
    return true;
  };

  const ordered = [...points].sort((a, b) => b.licensedUsers - a.licensedUsers);

  return (
    <div className={styles.root}>
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} width="100%" role="img" aria-label={t('copilotAdoption.intensityScatter.ariaLabel')}>
        {yTickValues.map((t) => (
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
              {formatValue(Math.round(t * 10) / 10)}
            </text>
          </g>
        ))}

        {xTickValues.map((t) => (
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
          {t('copilotAdoption.intensityScatter.axis.activeDays')}
        </text>
        <text
          x={-(PAD.top + plotH / 2)}
          y={14}
          transform="rotate(-90)"
          textAnchor="middle"
          fontSize={11}
          fill={tokens.colorNeutralForeground3}
        >
          {t('copilotAdoption.intensityScatter.axis.interactionsPerDay')}
        </text>

        {showQuadrants && (
          <g>
            <line
              x1={xOf(xMedian)}
              x2={xOf(xMedian)}
              y1={PAD.top}
              y2={HEIGHT - PAD.bottom}
              stroke={tokens.colorPaletteRedBorderActive}
              strokeDasharray="4 4"
              strokeOpacity={0.55}
            />
            <line
              x1={PAD.left}
              x2={WIDTH - PAD.right}
              y1={yOf(yMedian)}
              y2={yOf(yMedian)}
              stroke={tokens.colorPaletteRedBorderActive}
              strokeDasharray="4 4"
              strokeOpacity={0.55}
            />

            <text x={xOf(xMedian) + 5} y={PAD.top - 20} fontSize={10} fill={tokens.colorNeutralForeground3}>
              {t('copilotAdoption.intensityScatter.median.frequency')}
            </text>
            <text x={PAD.left + 4} y={yOf(yMedian) - 5} fontSize={10} fill={tokens.colorNeutralForeground3}>
              {t('copilotAdoption.intensityScatter.median.intensity')}
            </text>

            <text x={PAD.left + 6} y={PAD.top + 12} fontSize={11} fontWeight={600} fill={tokens.colorNeutralForeground3}>
              {t('copilotAdoption.intensityScatter.quadrant.deepOccasional')}
            </text>
            <text
              x={WIDTH - PAD.right - 6}
              y={PAD.top + 12}
              textAnchor="end"
              fontSize={11}
              fontWeight={600}
              fill={tokens.colorPaletteGreenForeground1}
            >
              {t('copilotAdoption.intensityScatter.quadrant.embedded')}
            </text>
            <text
              x={PAD.left + 6}
              y={HEIGHT - PAD.bottom - 8}
              fontSize={11}
              fontWeight={600}
              fill={tokens.colorPaletteRedForeground1}
            >
              {t('copilotAdoption.intensityScatter.quadrant.barelyStarted')}
            </text>
            <text
              x={WIDTH - PAD.right - 6}
              y={HEIGHT - PAD.bottom - 8}
              textAnchor="end"
              fontSize={11}
              fontWeight={600}
              fill={tokens.colorNeutralForeground3}
            >
              {t('copilotAdoption.intensityScatter.quadrant.frequentShallow')}
            </text>
          </g>
        )}

        {ordered.map((p) => {
          const cx = xOf(p.activeDaysPerUser);
          const cy = yOf(p.actionsPerActiveDay);
          const r = rOf(p.licensedUsers);
          const segment = serverPlaceholderText(t, p.segment);

          // Roughly 6px per character at this font size - close enough to reserve a sensible box
          // without measuring text, which would need a DOM round trip on every render.
          const labelW = segment.length * 6;
          const above = tryPlace(cx, cy - r - 5, labelW);
          const below = above ? false : tryPlace(cx, cy + r + 16, labelW);

          return (
            <g key={p.segment}>
              <circle
                cx={cx}
                cy={cy}
                r={r}
                fill={scoreColour(p.activeUserAverageScore, bands)}
                fillOpacity={0.55}
                stroke={scoreColour(p.activeUserAverageScore, bands)}
              >
                <title>
                  {t('copilotAdoption.intensityScatter.pointTitle', { segment, licences: formatValue(p.licensedUsers), active: formatValue(p.activeUsers), activeDays: formatValue(p.activeDaysPerUser), interactions: formatValue(p.actionsPerActiveDay), engagement: formatValue(p.activeUserAverageScore) })}
                </title>
              </circle>

              {r >= 9 && (
                <text
                  x={cx}
                  y={cy + 4}
                  textAnchor="middle"
                  fontSize={11}
                  fontWeight={700}
                  fill="#ffffff"
                  style={{ pointerEvents: 'none' }}
                >
                  {bandInitial(p.activeUserAverageScore, bands)}
                </text>
              )}

              {(above || below) && (
                <text
                  x={cx}
                  y={above ? cy - r - 5 : cy + r + 16}
                  textAnchor="middle"
                  fontSize={11}
                  fill={tokens.colorNeutralForeground2}
                  style={{ pointerEvents: 'none' }}
                >
                  {segment}
                </text>
              )}
            </g>
          );
        })}
      </svg>

      <div className={styles.legend}>
        {BAND_KEY.map((k) => (
          <span key={k.initial} className={styles.legendItem}>
            <span
              className={styles.legendSwatch}
              style={{ backgroundColor: scoreColour(k.score(bands), bands) }}
              aria-hidden="true"
            >
              {k.initial}
            </span>
            {t(k.labelKey)}
          </span>
        ))}
      </div>

      <Text size={200} className={styles.caption}>
        {t('copilotAdoption.intensityScatter.caption')}
      </Text>
    </div>
  );
}
