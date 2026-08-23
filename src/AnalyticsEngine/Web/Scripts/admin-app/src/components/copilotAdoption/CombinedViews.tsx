import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type {
  AdoptionCombinedSegmentRow,
  AdoptionConcentrationBand,
} from '../../types/copilotAdoption';
import { formatCount, formatPct } from './KpiGrid';
import { useAdoptionTableStyles } from './adoptionShared';

/** Heaviest cohort darkest, so the shape of the power law reads left to right. */
const COHORT_COLOUR = ['#0b3d6b', '#1f6cb0', '#5b9bd5', '#c7dbef'];

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
    color: '#ffffff',
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
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    color: '#ffffff',
    fontSize: '10px',
    fontWeight: 700,
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
        {bands.map((b, i) => (
          <div
            key={b.label}
            className={styles.slice}
            style={{ width: `${Math.max(0.5, b.sharePct)}%`, backgroundColor: COHORT_COLOUR[i % COHORT_COLOUR.length] }}
            title={`${b.label}: ${formatCount(b.users)} users, ${formatCount(b.interactions)} interactions (${formatPct(
              b.sharePct,
            )} of all activity), ${b.interactionsPerUser} each`}
          >
            {b.sharePct >= 8 ? `${i + 1}. ${formatPct(b.sharePct)}` : ''}
          </div>
        ))}
      </div>

      <div className={styles.legend}>
        {bands.map((b, i) => (
          <div key={b.label} className={styles.legendItem}>
            <span
              className={styles.swatch}
              style={{ backgroundColor: COHORT_COLOUR[i % COHORT_COLOUR.length] }}
              aria-hidden="true"
            >
              {i + 1}
            </span>
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
  const shade = (value: number, max: number, hue: string) =>
    value <= 0 ? undefined : { backgroundColor: hue, opacity: 0.15 + 0.85 * (value / max) };

  return (
    <table className={table.table}>
      <thead>
        <tr>
          <th className={table.th}>Department</th>
          <th className={`${table.th} ${table.thNumeric}`}>Seats</th>
          <th className={`${table.th} ${table.thNumeric}`}>Active seats</th>
          <th className={`${table.th} ${table.thNumeric}`}>Interactions per seat</th>
          <th className={`${table.th} ${table.thNumeric}`}>Seats using agents</th>
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
