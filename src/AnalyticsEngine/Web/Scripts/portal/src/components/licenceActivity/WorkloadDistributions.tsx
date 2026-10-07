import { memo } from 'react';
import { makeStyles, tokens, Card, Text } from '@fluentui/react-components';
import type { LicenceActivityDistribution, WorkloadKey } from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import { useT } from '../../i18n';
import {
  ACTIVITY_BANDS,
  activeCount,
  activeRatePct,
  bandDescription,
  bandLabel,
  bandCount,
  distributionTotal,
  measuredCount,
} from './bands';
import { formatCount, formatPct, formatPoints } from './format';
import ActivityCoverageHelp from './ActivityCoverageHelp';
import type { Rank } from './scope';

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
  comparison: {
    display: 'flex',
    flexDirection: 'column',
    gap: '4px',
    paddingTop: '2px',
  },
  comparisonBar: {
    display: 'flex',
    height: '8px',
    width: '100%',
    borderRadius: tokens.borderRadiusSmall,
    overflow: 'hidden',
    backgroundColor: tokens.colorNeutralBackground3,
    opacity: 0.75,
  },
  comparisonLine: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '8px',
    flexWrap: 'wrap',
  },
  delta: {
    fontVariantNumeric: 'tabular-nums',
    color: tokens.colorNeutralForeground1,
  },
});

/** Another population to read a distribution against: everyone holding a licence, or another licence. */
export interface DistributionComparison {
  /** Already in the reader's language, or tenant data (a licence name). */
  label: string;
  workloads: LicenceActivityDistribution[];
}

function StackedBar({
  distribution,
  className,
  ariaLabel,
}: {
  distribution: LicenceActivityDistribution;
  className: string;
  ariaLabel: string;
}) {
  const styles = useStyles();
  const t = useT();
  const total = distributionTotal(distribution);
  return (
    <div className={className} role="img" aria-label={ariaLabel}>
      {ACTIVITY_BANDS.map((band) => {
        const count = bandCount(distribution, band.key);
        if (count <= 0 || total <= 0) return null;
        return (
          <div
            key={band.key}
            className={styles.segment}
            style={{ width: `${(count / total) * 100}%`, backgroundColor: band.colour }}
            title={t('licenceActivity.distribution.bandTitle', {
              label: bandLabel(t, band.key),
              count: formatCount(count),
              description: bandDescription(t, band.key) ?? '',
            })}
          />
        );
      })}
    </div>
  );
}

/** One workload's five-band stacked bar plus a legend of counts, optionally read against another population. */
function DistributionCard({
  distribution,
  comparison,
  comparisonLabel,
  rank,
}: {
  distribution: LicenceActivityDistribution;
  comparison?: LicenceActivityDistribution | null;
  comparisonLabel?: string;
  rank?: Rank | null;
}) {
  const styles = useStyles();
  const t = useT();
  const measured = measuredCount(distribution);
  const rate = activeRatePct(distribution);
  const label = WORKLOADS.find((w) => w.key === (distribution.workload as WorkloadKey))?.label ?? distribution.workload;
  const comparisonRate = comparison ? activeRatePct(comparison) : null;

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <Text weight="semibold" size={400}>
          {label}
        </Text>
        <Text size={200} className={styles.muted}>
          {rate == null
            ? t('licenceActivity.common.notMeasured')
            : t('licenceActivity.distribution.activeOfMeasured', {
                active: formatCount(activeCount(distribution)),
                measured: formatCount(measured),
                rate: formatPct(rate),
              })}
        </Text>
      </div>

      <StackedBar
        distribution={distribution}
        className={styles.bar}
        ariaLabel={t('licenceActivity.distribution.aria', { label })}
      />

      {comparison && comparisonLabel && (
        <div className={styles.comparison}>
          <StackedBar
            distribution={comparison}
            className={styles.comparisonBar}
            ariaLabel={t('licenceActivity.compare.comparisonBarAria', { label, comparison: comparisonLabel })}
          />
          <div className={styles.comparisonLine}>
            <Text size={200} className={styles.muted}>
              {comparisonRate == null
                ? t('licenceActivity.compare.comparisonNotMeasured', { comparison: comparisonLabel })
                : t('licenceActivity.compare.comparisonRate', { comparison: comparisonLabel, rate: formatPct(comparisonRate) })}
            </Text>
            {rate != null && comparisonRate != null && (
              <Text size={200} weight="semibold" className={styles.delta}>
                {t('licenceActivity.compare.points', { points: formatPoints(rate - comparisonRate) })}
              </Text>
            )}
          </div>
          {rank && (
            <Text size={200} className={styles.muted}>
              {t('licenceActivity.compare.rank', { rank: formatCount(rank.rank), of: formatCount(rank.of), service: label })}
            </Text>
          )}
        </div>
      )}

      <div className={styles.legend}>
        {ACTIVITY_BANDS.map((band) => (
          <span key={band.key} className={styles.legendItem} title={bandDescription(t, band.key) ?? undefined}>
            <span className={styles.swatch} style={{ backgroundColor: band.colour }} aria-hidden />
            <Text size={100} className={styles.legendLabel}>
              {t('licenceActivity.distribution.bandLegendLabel', {
                label: bandLabel(t, band.key),
                count: formatCount(bandCount(distribution, band.key)),
              })}
            </Text>
          </span>
        ))}
      </div>
      <ActivityCoverageHelp showCopilot={distribution.workload === 'copilot'} />
    </Card>
  );
}

interface WorkloadDistributionsProps {
  workloads: LicenceActivityDistribution[];
  /** Another population to read each service against, e.g. everyone holding a licence. */
  comparison?: DistributionComparison | null;
  /** Where the licence ranks among the licences people hold, per service. */
  ranks?: Partial<Record<string, Rank | null>>;
}

/**
 * The five services' activity distributions for one population, side by side. Each service has its own
 * card: Teams messages and SharePoint edits are not commensurable, so the per-service picture is kept
 * apart from the licence's overall adoption score rather than replaced by it.
 *
 * Each bar keeps "No activity" (measured zero, red) and "Unknown" (not measured, grey) as distinct
 * segments, so a workload with no import reads as a grey bar - visibly unmeasured - rather than as an
 * all-zero one that would imply nobody used it.
 *
 * With a `comparison`, each card also shows the other population's bar and active share, and the
 * difference in percentage points, so a licence tier can be read against everyone else.
 */
function WorkloadDistributions({ workloads, comparison, ranks }: WorkloadDistributionsProps) {
  const styles = useStyles();
  const t = useT();

  // Present in a stable workload order regardless of how the backend ordered them.
  const ordered = WORKLOADS.map((w) => workloads.find((d) => d.workload === w.key)).filter(
    (d): d is LicenceActivityDistribution => d != null,
  );

  if (ordered.length === 0) {
    return <Text className={styles.muted}>{t('licenceActivity.distribution.noActivity')}</Text>;
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '10px' }}>
      <Text size={100} className={styles.muted}>
        {t('licenceActivity.band.method')} {t('licenceActivity.distribution.activeCountExplanation')}
      </Text>
      <div className={styles.grid}>
        {ordered.map((distribution) => (
          <DistributionCard
            key={distribution.workload}
            distribution={distribution}
            comparison={comparison?.workloads.find((d) => d.workload === distribution.workload) ?? null}
            comparisonLabel={comparison?.label}
            rank={ranks?.[distribution.workload] ?? null}
          />
        ))}
      </div>
    </div>
  );
}

// Memoised: renders only when the selected scope's workloads (or its comparison) change.
export default memo(WorkloadDistributions);
