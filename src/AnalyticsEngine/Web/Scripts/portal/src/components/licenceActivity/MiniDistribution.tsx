import { makeStyles, tokens, Text } from '@fluentui/react-components';
import type { LicenceActivityDistribution } from '../../types/licenceActivity';
import { ACTIVITY_BANDS, distributionTotal } from './bands';
import { formatCount } from './format';

const useStyles = makeStyles({
  bar: {
    display: 'flex',
    width: '96px',
    height: '12px',
    borderRadius: tokens.borderRadiusSmall,
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground3,
  },
  seg: {
    height: '100%',
  },
  legend: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '4px 14px',
  },
  legendItem: {
    display: 'flex',
    alignItems: 'center',
    gap: '6px',
  },
  swatch: {
    width: '10px',
    height: '10px',
    borderRadius: '2px',
    flexShrink: 0,
  },
  legendLabel: {
    color: tokens.colorNeutralForeground2,
  },
});

/**
 * A compact stacked bar for one distribution, grey full-width when nothing was measured (total 0).
 *
 * The aria-label and title carry the exact per-band counts, so the bar is never colour-only - a
 * screen reader (and a hover) get the numbers, satisfying "accessible legend + text evidence".
 */
export function MiniDistribution({ distribution }: { distribution: LicenceActivityDistribution }) {
  const styles = useStyles();
  const total = distributionTotal(distribution);
  const label = ACTIVITY_BANDS.map((b) => `${b.label} ${formatCount(distribution[b.key])}`).join(', ');

  return (
    <div className={styles.bar} role="img" aria-label={label} title={label}>
      {total <= 0 ? (
        <div className={styles.seg} style={{ width: '100%', backgroundColor: '#8a8886' }} />
      ) : (
        ACTIVITY_BANDS.map((b) => {
          const count = distribution[b.key];
          return count > 0 ? (
            <div
              key={b.key}
              className={styles.seg}
              style={{ width: `${(count / total) * 100}%`, backgroundColor: b.colour }}
            />
          ) : null;
        })
      )}
    </div>
  );
}

/** An accessible legend of the band colours, shown once per table so the mini-bars are readable. */
export function BandLegend() {
  const styles = useStyles();
  return (
    <div className={styles.legend}>
      {ACTIVITY_BANDS.map((b) => (
        <span key={b.key} className={styles.legendItem}>
          <span className={styles.swatch} style={{ backgroundColor: b.colour }} aria-hidden />
          <Text size={100} className={styles.legendLabel}>
            {b.label}
          </Text>
        </span>
      ))}
    </div>
  );
}
