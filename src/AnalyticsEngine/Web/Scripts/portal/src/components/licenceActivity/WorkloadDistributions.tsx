import { memo } from 'react';
import { makeStyles, tokens, Card, Text } from '@fluentui/react-components';
import type { LicenceActivityDistribution, WorkloadKey } from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import {
  ACTIVITY_BANDS,
  activeCount,
  activeRatePct,
  BAND_METHOD,
  bandCount,
  distributionTotal,
  measuredCount,
} from './bands';
import { formatCount, formatPct } from './format';

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))',
    gap: '16px',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
  },
  head: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '8px',
  },
  muted: {
    color: tokens.colorNeutralForeground3,
  },
  bar: {
    display: 'flex',
    height: '18px',
    width: '100%',
    borderRadius: tokens.borderRadiusSmall,
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground3,
  },
  segment: {
    height: '100%',
  },
  legend: {
    display: 'flex',
    flexWrap: 'wrap',
    gap: '4px 14px',
    marginTop: '2px',
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

/** One workload's five-band stacked bar plus a legend of counts. */
function DistributionCard({ distribution }: { distribution: LicenceActivityDistribution }) {
  const styles = useStyles();
  const total = distributionTotal(distribution);
  const measured = measuredCount(distribution);
  const rate = activeRatePct(distribution);
  const label = WORKLOADS.find((w) => w.key === (distribution.workload as WorkloadKey))?.label ?? distribution.workload;

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <Text weight="semibold" size={400}>
          {label}
        </Text>
        <Text size={200} className={styles.muted}>
          {rate == null
            ? 'Not measured'
            : `${formatCount(activeCount(distribution))} of ${formatCount(measured)} active (${formatPct(rate)})`}
        </Text>
      </div>

      <div className={styles.bar} role="img" aria-label={`${label} activity distribution`}>
        {ACTIVITY_BANDS.map((band) => {
          const count = bandCount(distribution, band.key);
          if (count <= 0 || total <= 0) return null;
          return (
            <div
              key={band.key}
              className={styles.segment}
              style={{ width: `${(count / total) * 100}%`, backgroundColor: band.colour }}
              title={`${band.label}: ${formatCount(count)}`}
            />
          );
        })}
      </div>

      <div className={styles.legend}>
        {ACTIVITY_BANDS.map((band) => (
          <span key={band.key} className={styles.legendItem}>
            <span className={styles.swatch} style={{ backgroundColor: band.colour }} aria-hidden />
            <Text size={100} className={styles.legendLabel}>
              {band.label} {formatCount(bandCount(distribution, band.key))}
            </Text>
          </span>
        ))}
      </div>
    </Card>
  );
}

interface WorkloadDistributionsProps {
  workloads: LicenceActivityDistribution[];
}

/**
 * The five workloads' activity distributions for a licence, side by side and DELIBERATELY separate -
 * no blended "productivity" or "ROI" score across them, because Teams messages and SharePoint edits
 * are not commensurable and one number would invite exactly the false comparison the separate charts
 * avoid.
 *
 * Each bar keeps "No activity" (measured zero, red) and "Unknown" (not measured, grey) as distinct
 * segments, so a workload with no import reads as a grey bar - visibly unmeasured - rather than as an
 * all-zero one that would imply nobody used it.
 */
function WorkloadDistributions({ workloads }: WorkloadDistributionsProps) {
  const styles = useStyles();

  // Present in a stable workload order regardless of how the backend ordered them. Only ever the five
  // workloads of the ONE selected licence - never all 50 SKUs' distributions at once.
  const ordered = WORKLOADS.map((w) => workloads.find((d) => d.workload === w.key)).filter(
    (d): d is LicenceActivityDistribution => d != null,
  );

  if (ordered.length === 0) {
    return <Text className={styles.muted}>No workload activity is available for this licence.</Text>;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
      <Text size={100} className={styles.muted}>
        {BAND_METHOD} Active counts are users with any measured activity, out of those with complete coverage.
      </Text>
      <div className={styles.grid}>
        {ordered.map((distribution) => (
          <DistributionCard key={distribution.workload} distribution={distribution} />
        ))}
      </div>
    </div>
  );
}

// Memoised: renders only when the selected licence's workloads change, not on every page re-render.
export default memo(WorkloadDistributions);
