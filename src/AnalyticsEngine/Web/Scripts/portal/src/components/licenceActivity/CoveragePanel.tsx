import { makeStyles, tokens, Card, Text, Badge } from '@fluentui/react-components';
import type { LicenceActivityCoverage, WorkloadKey } from '../../types/licenceActivity';
import { WORKLOADS } from '../../types/licenceActivity';
import { useT } from '../../i18n';
import { DASH, formatAge, formatCount, formatDate, formatDateTime, formatMaybeCount } from './format';
import { statusMeta } from './statuses';
import { coverageMessage, granularityLabel, measureLabel, sourceLabel } from './sources';

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
 * States, per Microsoft 365 service, exactly where the figures came from and how fresh and complete
 * they are.
 *
 * This is load-bearing, not decoration: a service's chart can legitimately be all "Unknown" because
 * its source was never imported or did not cover some people, and the reader needs to see that cause
 * here rather than mistake it for an absence of activity. Every field the backend provides - source,
 * measure, freshness, reading counts, unmatched people and the status - is shown.
 */
export default function CoveragePanel({ generatedUtc, expiresUtc, coverage, now }: CoveragePanelProps) {
  const styles = useStyles();
  const t = useT();
  const unknown = t('licenceActivity.common.unknown');

  return (
    <Card className={styles.card}>
      <div className={styles.head}>
        <Text size={200} weight="semibold" className={styles.title}>
          {t('licenceActivity.coverage.title')}
        </Text>
        <Text size={200} className={styles.generated}>
          {t('licenceActivity.coverage.preparedHeld', {
            prepared: formatDateTime(generatedUtc),
            age: formatAge(generatedUtc, t, now),
            expires: formatDateTime(expiresUtc),
          })}
        </Text>
      </div>

      {coverage.length === 0 ? (
        <Text size={200} className={styles.sub}>
          {t('licenceActivity.coverage.noSourceInfo')}
        </Text>
      ) : (
        <div className={styles.grid}>
          {coverage.map((entry) => (
            <div key={entry.workload} className={styles.item}>
              <div className={styles.itemHead}>
                <Text size={300} weight="semibold">
                  {workloadLabel(entry.workload)}
                </Text>
                <Badge appearance="tint" color={statusMeta(entry.status, t).tone} size="small">
                  {statusMeta(entry.status, t).label}
                </Badge>
              </div>

              <Text size={200} className={styles.line}>
                {t('licenceActivity.common.source')} {sourceLabel(entry.source, t) || DASH}
                {measureLabel(entry.measureKey, entry.measure, t) ? ` \u00b7 ${measureLabel(entry.measureKey, entry.measure, t)}` : ''}
                {entry.granularity ? ` \u00b7 ${granularityLabel(entry.granularity, t)}` : ''}
              </Text>

              <Text size={100} className={styles.sub}>
                {t('licenceActivity.common.lastImported', { date: formatDate(entry.latestImportUtc) })}
                {entry.lagDays > 0
                  ? ` \u00b7 ${t('licenceActivity.coverage.mostRecentDataDaysOld', { days: formatCount(entry.lagDays) })}`
                  : ''}
                {entry.reportPeriodDays
                  ? ` \u00b7 ${t('licenceActivity.coverage.coversDays', { days: formatCount(entry.reportPeriodDays) })}`
                  : ''}
              </Text>

              <Text size={100} className={styles.sub}>
                {t('licenceActivity.common.dataFrom', {
                  from: formatDate(entry.effectiveFromUtc),
                  to: formatDate(entry.effectiveToUtc),
                })}
              </Text>

              <Text size={100} className={styles.sub}>
                {t('licenceActivity.common.measurementsTaken', {
                  observed: formatMaybeCount(entry.observedSamples, unknown),
                  expected: formatMaybeCount(entry.expectedSamples, unknown),
                })}
                {entry.unmatchedUsers > 0
                  ? ` \u00b7 ${t('licenceActivity.common.peopleCouldNotBeMatched', {
                      count: formatCount(entry.unmatchedUsers),
                    })}`
                  : ''}
              </Text>

              {coverageMessage(entry.messageKey, entry.message, t) && (
                <Text size={100} className={styles.message}>
                  {coverageMessage(entry.messageKey, entry.message, t)}
                </Text>
              )}
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}
