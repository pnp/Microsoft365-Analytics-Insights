import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { AdoptionHabitBucket } from '../../types/copilotAdoption';
import DonutChart from '../charts/DonutChart';
import { formatCount, formatPct } from './KpiGrid';

/**
 * Cold-to-warm with frequency, and deliberately grey for "Infrequent" - on most tenants that tile
 * holds the majority of users, and colouring the majority case as a success is how an adoption
 * report ends up saying nothing.
 */
const BUCKET_COLOUR: Record<string, string> = {
  Infrequent: '#8a8886',
  Moderate: '#5b9bd5',
  Frequent: '#2b7cc4',
  Daily: '#0b3d6b',
};

const useStyles = makeStyles({
  root: {
    display: 'flex',
    gap: '20px',
    flexWrap: 'wrap',
    alignItems: 'center',
    paddingTop: '4px',
  },
  strip: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))',
    gap: '12px',
    flexGrow: 1,
    minWidth: '320px',
  },
  tile: {
    borderRadius: tokens.borderRadiusMedium,
    padding: '12px 14px',
    color: '#ffffff',
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    minHeight: '108px',
  },
  bucket: {
    fontWeight: tokens.fontWeightSemibold,
  },
  range: {
    opacity: 0.85,
  },
  users: {
    fontSize: '28px',
    lineHeight: '34px',
    fontWeight: tokens.fontWeightSemibold,
    fontVariantNumeric: 'tabular-nums',
    marginTop: 'auto',
  },
  share: {
    opacity: 0.9,
  },
  donut: {
    minWidth: '260px',
    flexGrow: 1,
  },
  empty: {
    color: tokens.colorNeutralForeground3,
    padding: '24px 0',
    textAlign: 'center',
  },
});

/**
 * The habit strip: how many active users open Copilot Infrequently, Moderately, Frequently or Daily,
 * with the same split shown as a ring.
 *
 * Kept separate from the engagement bands on purpose. A band is a weighted judgement combining
 * frequency, depth and breadth, which is the right thing to act on but the wrong thing to open
 * with - the first figure a sceptical reader wants is the unweighted one: how many days a month do
 * these people actually open it? This answers exactly that, and nothing else.
 *
 * The tiles and the ring carry identical numbers on purpose rather than by oversight. The tiles are
 * for reading a single bucket precisely; the ring is for seeing the balance between them in one
 * glance, which is the thing an executive takes away.
 */
export default function HabitStrip({ buckets }: { buckets: AdoptionHabitBucket[] }) {
  const styles = useStyles();

  const total = buckets.reduce((sum, b) => sum + b.users, 0);
  if (total === 0) {
    return (
      <div className={styles.empty}>
        No user was active in this period, so there is no habit to measure. The reclaimable-licence figure is
        the one that matters here.
      </div>
    );
  }

  return (
    <div className={styles.root}>
      <div className={styles.strip}>
        {buckets.map((b) => (
          <div key={b.label} className={styles.tile} style={{ backgroundColor: BUCKET_COLOUR[b.label] ?? '#605e5c' }}>
            <Text size={300} className={styles.bucket}>
              {b.label}
            </Text>
            <Text size={200} className={styles.range}>
              {b.rangeLabel}
            </Text>
            <span className={styles.users}>{formatCount(b.users)}</span>
            <Text size={200} className={styles.share}>
              {formatPct(b.sharePct)} of active users
            </Text>
          </div>
        ))}
      </div>

      <div className={styles.donut}>
        <DonutChart
          categories={buckets.map((b) => ({ label: b.label, value: b.users }))}
          colours={buckets.map((b) => BUCKET_COLOUR[b.label] ?? '#605e5c')}
          centreValue={formatCount(total)}
          centreLabel="active users"
          size={150}
        />
      </div>
    </div>
  );
}

export { BUCKET_COLOUR as HABIT_BUCKET_COLOUR };
