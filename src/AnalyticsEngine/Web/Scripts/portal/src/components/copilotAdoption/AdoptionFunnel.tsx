import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { ReportCategory } from '../../types/reports';
import type { CopilotAdoptionOptions } from '../../types/copilotAdoption';
import { formatCount, formatPct } from './KpiGrid';

const useStyles = makeStyles({
  root: {
    width: '100%',
    paddingTop: '4px',
  },
  svg: {
    width: '100%',
    height: 'auto',
    display: 'block',
    overflow: 'visible',
  },
  caption: {
    color: tokens.colorNeutralForeground3,
    display: 'block',
    marginTop: '6px',
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

const W = 1030;
const STAGE_H = 74;
const GAP = 6;
const LABEL_W = 190;
const DROP_W = 170;
/**
 * The gutter kept clear between the funnel's widest possible edge and the drop-off column.
 *
 * A stage too narrow to hold its own labels moves them outside the shape, to the right - and the top
 * stage is by definition full width, so without a reserved gutter its labels were drawn straight on
 * top of the "baseline" text in the drop column. Deriving the funnel width from this constant makes
 * the collision impossible by construction rather than by a clamp that has to be kept in step.
 */
const OUTSIDE_LABEL_W = 120;

/**
 * Progressively deeper blues down the funnel, so the shape reads as one narrowing pipeline rather
 * than as five unrelated shapes.
 */
const STAGE_COLOURS = ['#8ec3ea', '#5aa6dd', '#2f86cc', '#1466ad', '#0a4a80'];

const EDGE_ALPHA = 0.8;
const WHITE_LUMINANCE = 1;
// Fluent exposes tokens as CSS variables at runtime; this is light-theme colorNeutralForeground1 (#242424).
const FLUENT_LIGHT_NEUTRAL_FOREGROUND1_LUMINANCE = 0.017641954488384078;

function srgbToLinear(value: number): number {
  const c = value / 255;
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function relativeLuminance(hexColour: string): number | null {
  const match = /^#(?<r>[0-9a-f]{2})(?<g>[0-9a-f]{2})(?<b>[0-9a-f]{2})$/i.exec(hexColour);
  if (!match?.groups) return null;

  const r = srgbToLinear(parseInt(match.groups.r, 16));
  const g = srgbToLinear(parseInt(match.groups.g, 16));
  const b = srgbToLinear(parseInt(match.groups.b, 16));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrastRatio(a: number, b: number): number {
  const light = Math.max(a, b);
  const dark = Math.min(a, b);
  return (light + 0.05) / (dark + 0.05);
}

function compositeHexOverWhite(hexColour: string, alpha: number): string {
  const match = /^#(?<r>[0-9a-f]{2})(?<g>[0-9a-f]{2})(?<b>[0-9a-f]{2})$/i.exec(hexColour);
  if (!match?.groups) return hexColour;
  const groups = match.groups;

  const channel = (name: 'r' | 'g' | 'b') => {
    const value = parseInt(groups[name], 16);
    return Math.round(value * alpha + 255 * (1 - alpha));
  };

  return `#${[channel('r'), channel('g'), channel('b')]
    .map((value) => value.toString(16).padStart(2, '0'))
    .join('')}`;
}

export function funnelLabelFillForStage(stageColour: string, darkFill: string = tokens.colorNeutralForeground1): string {
  const paintedLuminance = relativeLuminance(compositeHexOverWhite(stageColour, EDGE_ALPHA));
  if (paintedLuminance === null) return darkFill;

  const darkFillLuminance = relativeLuminance(darkFill) ?? FLUENT_LIGHT_NEUTRAL_FOREGROUND1_LUMINANCE;
  const whiteContrast = contrastRatio(WHITE_LUMINANCE, paintedLuminance);
  const darkContrast = contrastRatio(darkFillLuminance, paintedLuminance);

  return whiteContrast >= darkContrast ? '#ffffff' : darkFill;
}

/**
 * What each stage name actually means, with the live thresholds substituted in.
 *
 * The stage labels are decided server-side (`BuildFunnel`), so this is keyed by label rather than by
 * position: if a stage is ever renamed or reordered the tooltip simply stops appearing, which is far
 * better than confidently explaining the wrong stage. The membership rule is stated first, because
 * "how is this counted?" is the question the reader has when they hover a name they did not choose.
 */
function stageHelp(label: string, options: CopilotAdoptionOptions): string | null {
  switch (label) {
    case 'Licensed':
      return (
        'The licensed users this analysis scored - holders of a Copilot licence SKU, taken from the '
        + 'imported licence assignments. It counts seats, not use: nobody at this stage has to have '
        + 'touched Copilot. This is the 100% baseline every other stage is measured against, and it '
        + 'is the scored population rather than the raw seat count, so a run that hits its row cap '
        + 'does not open with a drop that is really an artefact of how many users were read.'
      );
    case 'Ever used Copilot':
      return (
        'Everyone counted at "Active this period", plus everyone banded Dormant - not counted as '
        + 'active in the period, but with earlier use on record. Those two groups are exactly this '
        + 'stage, so the drop to "Active this period" is the Dormant population. Earlier use is read '
        + `from the audit import\u2019s history, which goes back at least ${options.historyDays} `
        + 'days, and from Microsoft\u2019s per-user usage report.'
      );
    case 'Active this period':
      return (
        'Licensed users counted as having at least one Copilot interaction inside the selected '
        + `reporting period (${options.windowDays} days). Users counted at "Ever used Copilot" but `
        + 'not here are the Dormant group - earlier use on record, but not counted as active in the '
        + 'period - and they need a conversation rather than onboarding. Where the audit import has '
        + 'nothing for a user but Microsoft\u2019s per-user usage report does, that report is used '
        + 'instead, and it covers Microsoft\u2019s own window rather than exactly the period '
        + 'selected here.'
      );
    case 'Habitual users':
      return (
        `Licensed users whose engagement score reaches ${options.establishedScore} out of 100 - the `
        + 'Established and Champion bands, which are the two this report counts as a formed habit. '
        + 'The score is a weighted blend of how often they use Copilot, how much they do on each '
        + 'active day, and how many Copilot surfaces they use; the Method tab sets out the weights '
        + 'and the exact formula.'
      );
    case 'Champions':
      return (
        `Licensed users whose engagement score reaches ${options.championScore} out of 100 - the top `
        + 'band. Champions are your advocate pool for enablement, not a target the whole population '
        + 'is expected to reach.'
      );
    default:
      return null;
  }
}

/**
 * The adoption funnel: licensed -> ever used -> active -> habitual -> champion.
 *
 * Drawn as an actual tapering funnel rather than as a bar chart, because the audience for this page
 * is an executive one and the funnel is a shape they already read fluently: the narrowing *is* the
 * message, and it lands before a single number has been read. The bar-chart version this replaced
 * made the reader compare five lengths to reach the same conclusion.
 *
 * Each stage is a trapezoid whose bottom edge matches the next stage's top edge, so the outline is
 * continuous. A "funnel" drawn as disconnected bars of decreasing length is just a bar chart wearing
 * a funnel's name. The drop-off between stages is called out on the right, because that is the
 * number that decides where the effort goes - except on the final step, which is a top tier rather
 * than a target for the whole population and is therefore reported neutrally. See the note on
 * isTopTier below.
 */
export default function AdoptionFunnel({
  stages,
  options,
}: {
  stages: ReportCategory[];
  options: CopilotAdoptionOptions;
}) {
  const styles = useStyles();

  if (stages.length === 0) {
    return <div className={styles.empty}>No licensed users to chart.</div>;
  }

  const top = Math.max(stages[0]?.value ?? 0, 1);
  const height = stages.length * STAGE_H + (stages.length - 1) * GAP;
  // The widest stage can only ever reach LABEL_W + funnelW, which leaves OUTSIDE_LABEL_W clear before
  // the drop column starts - so labels pushed outside a narrow stage always have somewhere to go.
  const funnelW = W - LABEL_W - DROP_W - OUTSIDE_LABEL_W;
  const centre = LABEL_W + funnelW / 2;

  // A near-empty stage still has to be visible, so the width is floored - but low enough that
  // "almost nobody" still reads as almost nobody.
  const halfWidthAt = (value: number) => Math.max(10, (value / top) * funnelW) / 2;

  return (
    <div className={styles.root}>
      <svg viewBox={`0 0 ${W} ${height}`} className={styles.svg} role="img" aria-label="Copilot adoption funnel">
        <defs>
          {STAGE_COLOURS.map((colour, i) => (
            <linearGradient key={i} id={`funnel-grad-${i}`} x1="0" y1="0" x2="1" y2="0">
              <stop offset="0%" stopColor={colour} stopOpacity={0.8} />
              <stop offset="45%" stopColor={colour} stopOpacity={1} />
              <stop offset="100%" stopColor={colour} stopOpacity={0.8} />
            </linearGradient>
          ))}
        </defs>

        {stages.map((stage, index) => {
          const y = index * (STAGE_H + GAP);
          const next = stages[index + 1];
          const previous = index === 0 ? null : stages[index - 1].value;
          const stageColour = STAGE_COLOURS[index % STAGE_COLOURS.length];

          const halfTop = halfWidthAt(stage.value);
          // Taper towards the next stage so the profile is continuous. The final stage keeps a
          // slight taper so the funnel closes instead of ending in a blunt rectangle - clamped so it
          // can never exceed the top edge, which is what a bare minimum width would do whenever the
          // last stage is tiny or empty, drawing a funnel that widens at the bottom.
          const halfBottom = next
            ? halfWidthAt(next.value)
            : Math.min(halfTop, Math.max(4, halfTop * 0.82));

          const lost = previous === null ? 0 : previous - stage.value;
          // A zero-to-zero step is not a 100% conversion, it is an absence of anything to convert.
          // Reporting it as 100% gave a tenant with no usage a funnel of apparently perfect steps.
          const conversion = previous === null || previous === 0 ? null : (stage.value / previous) * 100;
          const sharePct = (stage.value / top) * 100;

          // The bottom stage is a top tier, not a target for the whole population. Everyone who
          // reaches the stage above it has already formed a habit - the action catalogue explicitly
          // says those users need no action and are paying for their seat. Printing "N lost here" in
          // red against them contradicts that advice and points enablement budget at the people who
          // are already succeeding, so the final step is reported as a neutral "not yet" instead.
          const isTopTier = index === stages.length - 1;
          const help = stageHelp(stage.label, options);
          const previousLabel = previous === null ? null : stages[index - 1].label;
          const dropText = isTopTier
            ? `${formatCount(lost)} not yet ${stage.label}`
            : `${formatCount(lost)} lost here`;
          const dropColour = isTopTier
            ? tokens.colorNeutralForeground3
            : tokens.colorPaletteRedForeground1;
          const dropHelp = previous === null
            ? ''
            : isTopTier
              ? `${formatCount(lost)} of the ${formatCount(previous)} users at "${previousLabel}" have not reached "${stage.label}". `
                + `Reaching "${stage.label}" is the top tier of engagement, not a target for everyone: users at `
                + `"${previousLabel}" have already formed a habit and need no action. Read this as the size of your `
                + `advocate pool, not as a loss.`
              : `${formatCount(lost)} of the ${formatCount(previous)} users at "${previousLabel}" did not reach "${stage.label}". `
                + `The percentage is the conversion from the stage immediately above, not from the top of the funnel. `
                + `The biggest single drop is where enablement effort should go.`;

          // The narrowest point decides whether the labels fit. A stage holding a handful of users is
          // only a few pixels wide, and white text centred on it lands on the page background where
          // it is invisible - so once the shape is too narrow, the labels move outside it and switch
          // to the foreground colour. Measured against the narrow end, not the wide one, because the
          // text is centred vertically and would otherwise straddle the taper.
          const narrowest = Math.min(halfTop, halfBottom) * 2;
          const labelsFitInside = narrowest >= 150;
          const labelX = labelsFitInside ? centre : centre + halfTop + 12;
          const inShapeLabelFill = funnelLabelFillForStage(stageColour);
          const inShapeCaptionOpacity = inShapeLabelFill === '#ffffff' ? 0.85 : 1;

          return (
            <g key={stage.label}>
              <polygon
                points={[
                  `${centre - halfTop},${y}`,
                  `${centre + halfTop},${y}`,
                  `${centre + halfBottom},${y + STAGE_H}`,
                  `${centre - halfBottom},${y + STAGE_H}`,
                ].join(' ')}
                fill={`url(#funnel-grad-${index % STAGE_COLOURS.length})`}
              >
                <title>{`${stage.label}: ${formatCount(stage.value)} (${formatPct(sharePct)} of licensed)`}</title>
              </polygon>

              <text
                x={LABEL_W - 14}
                y={y + STAGE_H / 2 + 5}
                textAnchor="end"
                fontSize={15}
                fill={tokens.colorNeutralForeground1}
                style={{ pointerEvents: 'none' }}
              >
                {stage.label}
              </text>
              {/* The stage name is the thing a reader questions first ("active by what rule?"), so
                  the whole left gutter is a hit area for its definition rather than just the glyphs.
                  The text opts out of pointer events so this rect keeps the hover. */}
              {help && (
                <rect
                  x={0}
                  y={y}
                  width={LABEL_W}
                  height={STAGE_H}
                  fill="transparent"
                  style={{ pointerEvents: 'all', cursor: 'help' }}
                >
                  <title>{`${stage.label}\n\n${help}`}</title>
                </rect>
              )}

              <text
                x={labelX}
                y={y + STAGE_H / 2 - 2}
                textAnchor={labelsFitInside ? 'middle' : 'start'}
                fontSize={20}
                fontWeight={600}
                fill={labelsFitInside ? inShapeLabelFill : tokens.colorNeutralForeground1}
                style={{ pointerEvents: 'none' }}
              >
                {formatCount(stage.value)}
              </text>
              <text
                x={labelX}
                y={y + STAGE_H / 2 + 18}
                textAnchor={labelsFitInside ? 'middle' : 'start'}
                fontSize={12}
                fill={labelsFitInside ? inShapeLabelFill : tokens.colorNeutralForeground3}
                fillOpacity={labelsFitInside ? inShapeCaptionOpacity : 1}
                style={{ pointerEvents: 'none' }}
              >
                {formatPct(sharePct)} of licensed
              </text>

              {previous === null ? (
                <text
                  x={W - DROP_W + 14}
                  y={y + STAGE_H / 2 + 5}
                  fontSize={13}
                  fill={tokens.colorNeutralForeground3}
                >
                  baseline
                </text>
              ) : (
                <>
                  {/* Transparent hit area so the whole right-hand column is hoverable, not just the
                      glyphs. The labels below opt out of pointer events so this rect keeps the hover. */}
                  <rect
                    x={W - DROP_W}
                    y={y}
                    width={DROP_W}
                    height={STAGE_H}
                    fill="transparent"
                    style={{ pointerEvents: 'all' }}
                  >
                    <title>{dropHelp}</title>
                  </rect>
                  <text
                    x={W - DROP_W + 14}
                    y={y + STAGE_H / 2 - 3}
                    fontSize={15}
                    fontWeight={600}
                    fill={conversion === null ? tokens.colorNeutralForeground3 : tokens.colorNeutralForeground1}
                    style={{ pointerEvents: 'none' }}
                  >
                    {conversion === null ? '\u2014' : formatPct(conversion)}
                  </text>
                  {lost > 0 && (
                    <text
                      x={W - DROP_W + 14}
                      y={y + STAGE_H / 2 + 16}
                      fontSize={12}
                      fill={dropColour}
                      style={{ pointerEvents: 'none' }}
                    >
                      {dropText}
                    </text>
                  )}
                </>
              )}
            </g>
          );
        })}
      </svg>

      <Text size={200} className={styles.caption}>
        Each stage is a subset of the one above it. The figure on the right is the conversion from the stage
        immediately above, not from the top - the biggest single drop is where the effort should go. The last
        step is shown in grey rather than red because it is not a loss: everyone who reaches the stage above it
        has already formed a habit. Hover any stage name for how it is counted, or any figure for the detail.
      </Text>
    </div>
  );
}
