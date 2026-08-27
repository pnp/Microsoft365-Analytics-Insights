import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type {
  AdoptionCombinedSegmentRow,
  AdoptionConcentrationBand,
} from '../../types/copilotAdoption';
import { formatCount, formatPct } from './KpiGrid';
import { useAdoptionTableStyles } from './adoptionShared';

/** Heaviest cohort darkest, so the shape of the power law reads left to right. */
const COHORT_COLOUR = ['#0b3d6b', '#1f6cb0', '#5b9bd5', '#c7dbef'];

/** Ends of the heat-table colour ramp, as the fraction of the hue mixed onto the page background. */
const RAMP_MIN_ALPHA = 0.12;
const RAMP_MAX_ALPHA = 0.65;

/** `#rrggbb` to an sRGB triple. */
function hexToRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.replace('#', ''), 16);
  return [(n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff];
}

/**
 * Mixes `hex` into the (white) page background at `alpha`, returning a solid sRGB triple.
 *
 * Heat ramps are built this way instead of with CSS `opacity` because opacity is applied to the
 * whole element - including the number printed on it - so the low end of the ramp fades the value
 * out of legibility.
 */
function blendOnWhite(hex: string, alpha: number): [number, number, number] {
  return hexToRgb(hex).map((channel) => Math.round(255 + alpha * (channel - 255))) as [number, number, number];
}

/**
 * White or near-black text, whichever has more contrast on the given fill.
 *
 * The crossover is where WCAG contrast against white equals contrast against Fluent's light-theme
 * body colour (#242424), which lands at a relative luminance of ~0.218.
 */
function readableForeground(r: number, g: number, b: number): string {
  const toLinear = (channel: number) => {
    const s = channel / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  const luminance = 0.2126 * toLinear(r) + 0.7152 * toLinear(g) + 0.0722 * toLinear(b);
  return luminance < 0.218 ? '#ffffff' : tokens.colorNeutralForeground1;
}

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    width: '100%',
    height: '30px',
    borderRadius: tokens.borderRadiusSmall,
    overflow: 'hidden',
    marginTop: '8px',
  },
  slice: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    fontSize: '12px',
    fontVariantNumeric: 'tabular-nums',
    minWidth: '2px',
    // A hairline between slices so two adjacent steps of the ramp stay separable in greyscale, on a
    // projector, or to a reader who cannot distinguish the hues at all.
    borderRightWidth: '1px',
    borderRightStyle: 'solid',
    borderRightColor: '#ffffff',
    ':last-child': {
      borderRightWidth: '0',
    },
  },
  legend: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '14px',
    marginTop: '10px',
  },
  legendItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
  },
  swatch: {
    width: '16px',
    height: '16px',
    borderRadius: '3px',
    display: 'inline-block',
    flexShrink: 0,
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
  heat: {
    fontVariantNumeric: 'tabular-nums',
    textAlign: 'right',
    padding: '6px 10px',
    borderBottomWidth: '1px',
    borderBottomStyle: 'solid',
    borderBottomColor: tokens.colorNeutralStroke3,
  },
});

/**
 * How concentrated Copilot usage is across the people who use it.
 *
 * The chart that distinguishes a working programme from one propped up by enthusiasts. "40% adoption
 * spread evenly" and "40% adoption where a tenth of them do most of it" produce the same adoption
 * percentage and are completely different situations - the second collapses the moment those people
 * change team, and no headline rate will warn you.
 */
export function ConcentrationBar({ bands }: { bands: AdoptionConcentrationBand[] }) {
  const styles = useStyles();

  if (bands.length === 0) {
    return <div className={styles.empty}>No active users to rank.</div>;
  }

  return (
    <div>
      <div className={styles.bar}>
        {bands.map((b, i) => {
          const colour = COHORT_COLOUR[i % COHORT_COLOUR.length];
          return (
            <div
              key={b.label}
              className={styles.slice}
              style={{
                width: `${Math.max(0.5, b.sharePct)}%`,
                backgroundColor: colour,
                // The ramp runs from near-black to very pale, so a fixed white label is unreadable on
                // the last step or two. Pick the label colour from the fill's luminance instead.
                color: readableForeground(...hexToRgb(colour)),
              }}
              title={`${b.label}: ${formatCount(b.users)} users, ${formatCount(b.interactions)} interactions (${formatPct(
                b.sharePct,
              )} of all activity), ${b.interactionsPerUser} each`}
            >
              {b.sharePct >= 8 ? formatPct(b.sharePct) : ''}
            </div>
          );
        })}
      </div>

      <div className={styles.legend}>
        {bands.map((b, i) => (
          <div key={b.label} className={styles.legendItem}>
            <span
              className={styles.swatch}
              style={{ backgroundColor: COHORT_COLOUR[i % COHORT_COLOUR.length] }}
              aria-hidden="true"
            />
            <Text size={200}>
              <strong>{b.label}</strong>{' '}
              <span className={styles.muted}>
                ({formatCount(b.users)} users, {b.interactionsPerUser} interactions each)
              </span>
            </Text>
          </div>
        ))}
      </div>
    </div>
  );
}

/**
 * Licensed and unlicensed Copilot use per department, side by side.
 *
 * The comparison is the whole point. A department with idle seats and heavy unlicensed Chat use is
 * not an adoption problem, it is a seat-allocation problem - and that is invisible in any view that
 * reports one population at a time.
 */
export function CombinedSegmentTable({ rows }: { rows: AdoptionCombinedSegmentRow[] }) {
  const styles = useStyles();
  const table = useAdoptionTableStyles();

  if (rows.length === 0) {
    return (
      <div className={styles.empty}>
        No department has enough licensed or unlicensed Copilot users to compare reliably.
      </div>
    );
  }

  const maxLicensed = Math.max(...rows.map((r) => r.interactionsPerLicensedUser), 1);
  const maxUnlicensed = Math.max(...rows.map((r) => r.interactionsPerUnlicensedUser), 1);

  // Conditional shading rather than a bar: this table is read by scanning for the outliers, and a
  // colour ramp finds them faster than eight columns of numbers.
  //
  // The ramp is baked into a solid background colour rather than applied with `opacity`. Element
  // opacity fades the digits along with the fill, so the palest cells became unreadable while the
  // darkest ones were left with dark text on a dark fill.
  //
  // It also stops at RAMP_MAX_ALPHA rather than running to a solid fill. Past roughly two thirds
  // strength these hues stop clearing WCAG AA 4.5:1 against the body text, and flipping the label to
  // white does not help - the flip point is itself the lowest-contrast spot on the whole ramp
  // (~3.9:1). Capping keeps one text colour, stays legible end to end, and still separates the
  // outliers plainly (contrast runs 13:1 at the pale end to ~4.9:1 at the strong end).
  const shade = (value: number, max: number, hue: string) => {
    if (value <= 0) return undefined;
    const alpha = RAMP_MIN_ALPHA + (RAMP_MAX_ALPHA - RAMP_MIN_ALPHA) * (value / max);
    const [r, g, b] = blendOnWhite(hue, alpha);
    return {
      backgroundColor: `rgb(${r}, ${g}, ${b})`,
      color: tokens.colorNeutralForeground1,
    };
  };

  return (
    <table className={table.table}>
      <thead>
        <tr>
          <th className={table.th}>Department</th>
          <th className={`${table.th} ${table.thNumeric}`}>Licences</th>
          <th className={`${table.th} ${table.thNumeric}`}>Active licences</th>
          <th className={`${table.th} ${table.thNumeric}`}>Interactions per licence</th>
          <th className={`${table.th} ${table.thNumeric}`}>Licences using agents</th>
          <th className={`${table.th} ${table.thNumeric}`}>Unlicensed users</th>
          <th className={`${table.th} ${table.thNumeric}`}>Interactions per unlicensed user</th>
          <th className={`${table.th} ${table.thNumeric}`}>Unlicensed using agents</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.segment}>
            <td className={table.td}>{r.segment}</td>
            <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(r.licensedUsers)}</td>
            <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(r.licensedActiveUsers)}</td>
            <td className={styles.heat}>
              <span
                style={{
                  ...shade(r.interactionsPerLicensedUser, maxLicensed, '#0f6cbd'),
                  padding: '2px 6px',
                  borderRadius: '3px',
                }}
              >
                {r.interactionsPerLicensedUser}
              </span>
            </td>
            <td className={`${table.td} ${table.tdNumeric}`}>{formatPct(r.licensedAgentUserPct)}</td>
            <td className={`${table.td} ${table.tdNumeric}`}>{formatCount(r.unlicensedActiveUsers)}</td>
            <td className={styles.heat}>
              <span
                style={{
                  ...shade(r.interactionsPerUnlicensedUser, maxUnlicensed, '#a4373a'),
                  padding: '2px 6px',
                  borderRadius: '3px',
                }}
              >
                {r.interactionsPerUnlicensedUser}
              </span>
            </td>
            <td className={`${table.td} ${table.tdNumeric}`}>{formatPct(r.unlicensedAgentUserPct)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
