import { makeStyles, tokens, Card, Text, Badge } from '@fluentui/react-components';
import type { LicenceActivityCoverage, WorkloadKey } from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import { DASH, formatAge, formatDate, formatDateTime, formatMaybeCount } from './format';
import { statusMeta } from './statuses';

const useStyles = makeStyles({
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '10px',
    padding: '14px 16px',
  },
  head: {
    display: 'flex',
    alignItems: 'baseline',
    justifyContent: 'space-between',
    gap: '12px',
    flexWrap: 'wrap',
  },
  title: {
    textTransform: 'uppercase',
    letterSpacing: '0.04em',
    color: tokens.colorNeutralForeground3,
  },
  generated: {
    color: tokens.colorNeutralForeground3,
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(230px, 1fr))',
    gap: '10px',
  },
  item: {
    display: 'flex',
    flexDirection: 'column',
    gap: '3px',
    padding: '10px 12px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  itemHead: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
  },
  line: {
    color: tokens.colorNeutralForeground2,
  },
  sub: {
    color: tokens.colorNeutralForeground3,
  },
  message: {
    color: tokens.colorNeutralForeground2,
    marginTop: '2px',
  },
});

function workloadLabel(workload: string): string {
  return WORKLOADS.find((w) => w.key === (workload as WorkloadKey))?.label ?? workload;
}

/**
 * The status vocabulary is fixed (LicenceActivityCoverage.Status): available | partial |
 * missingCoverage | unmatchableIdentity | notImported | disabled. Tone/label come from the shared
 * status helper so the coverage panel and the per-user evidence detail describe a status identically.
 */
interface CoveragePanelProps {
  generatedUtc: string;
  expiresUtc: string;
  coverage: LicenceActivityCoverage[];
  /** Injectable "now" for deterministic "generated N days ago" captions in tests. */
  now?: Date;
}

/**
 * States, per workload, exactly which import fed the figures and how fresh and complete it is.
 *
 * This is load-bearing, not decoration: a workload's distribution can legitimately be all "Unknown"
 * because its source was not imported or did not cover some users, and the reader needs to see that
 * cause here rather than mistake it for an absence of activity. Every field the backend provides -
 * source, measure, freshness, sample counts, unmatched users, and the raw status - is shown.
 */
export default function CoveragePanel({ generatedUtc, expiresUtc, coverage, now }: CoveragePanelProps) {
  const styles = useStyles();

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <Text size={200} weight="semibold" className={styles.title}>
          Data sources &amp; coverage
        </Text>
        <Text size={200} className={styles.generated}>
          Snapshot generated {formatDateTime(generatedUtc)} ({formatAge(generatedUtc, now)}); expires{' '}
          {formatDateTime(expiresUtc)}
        </Text>
      </div>

      {coverage.length === 0 ? (
        <Text size={200} className={styles.sub}>
          No per-workload coverage was reported for this snapshot.
        </Text>
      ) : (
        <div className={styles.grid}>
          {coverage.map((entry) => (
            <div key={entry.workload} className={styles.item}>
              <div className={styles.itemHead}>
                <Text size={300} weight="semibold">
                  {workloadLabel(entry.workload)}
                </Text>
                <Badge appearance="tint" color={statusMeta(entry.status).tone} size="small">
                  {statusMeta(entry.status).label}
                </Badge>
              </div>

              <Text size={200} className={styles.line}>
                Source: {entry.source || DASH}
                {entry.measure ? ` \u00b7 ${entry.measure}` : ''}
                {entry.granularity ? ` (${entry.granularity})` : ''}
              </Text>

              <Text size={100} className={styles.sub}>
                Imported {formatDate(entry.latestImportUtc)}
                {entry.lagDays > 0 ? ` \u00b7 ${entry.lagDays}d lag` : ''}
                {entry.reportPeriodDays ? ` \u00b7 ${entry.reportPeriodDays}d period` : ''}
              </Text>

              <Text size={100} className={styles.sub}>
                Covers {formatDate(entry.effectiveFromUtc)}
                {' \u2013 '}
                {formatDate(entry.effectiveToUtc)}
              </Text>

              <Text size={100} className={styles.sub}>
                {formatMaybeCount(entry.observedSamples)} / {formatMaybeCount(entry.expectedSamples)} samples
                {entry.unmatchedUsers > 0 ? ` \u00b7 ${entry.unmatchedUsers.toLocaleString()} unmatched` : ''}
              </Text>

              {entry.message && (
                <Text size={100} className={styles.message}>
                  {entry.message}
                </Text>
              )}
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}
