import { Tag, Text, makeStyles, tokens } from '@fluentui/react-components';
import { useT } from '../../i18n';
import type { ActivityAnalysisLicence, ActivityAnalysisMetric } from '../../types/activityAnalysis';
import { describeAppliedFilters, type AppliedFilters } from './filters';

const useStyles = makeStyles({
  row: {
    display: 'flex',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: '6px',
  },
  label: {
    color: tokens.colorNeutralForeground3,
    marginRight: '2px',
  },
  tag: {
    maxWidth: '100%',
  },
});

/**
 * The filters in force, in words, beside the figures they narrow - so a reader (or a printout) never
 * has to open the panel to know whether the numbers are the whole organisation.
 */
export default function AppliedFiltersSummary({
  applied,
  metrics,
  licences,
  echoNames,
}: {
  applied: AppliedFilters;
  metrics: ReadonlyMap<string, ActivityAnalysisMetric>;
  licences: readonly ActivityAnalysisLicence[];
  echoNames?: Record<string, string> | null;
}) {
  const styles = useStyles();
  const t = useT();
  const items = describeAppliedFilters(t, applied, metrics, licences, { names: echoNames });
  if (items.length === 0) return null;

  return (
    <div className={styles.row}>
      <Text size={200} weight="semibold" className={styles.label}>
        {t('activityAnalysis.filters.summary.label')}
      </Text>
      {items.map((text) => (
        <Tag key={text} size="small" shape="rounded" appearance="brand" className={styles.tag}>
          {text}
        </Tag>
      ))}
    </div>
  );
}
