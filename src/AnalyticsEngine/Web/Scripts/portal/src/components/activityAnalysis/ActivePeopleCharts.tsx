import { Card, Text, makeStyles, tokens } from '@fluentui/react-components';
import { formatNumber, plural, useT, type TFunction } from '../../i18n';
import type { ActivityAnalysisGroupBreakdown, ActivityAnalysisGroupRow } from '../../types/activityAnalysis';
import type { ReportCategory } from '../../types/reports';
import CategoryBarChart from '../charts/CategoryBarChart';
import TreemapChart from '../charts/TreemapChart';
import InfoTip from '../shared/InfoTip';

const useStyles = makeStyles({
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))',
    gap: '16px',
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: '8px',
    padding: '14px 16px',
    minWidth: 0,
  },
  head: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: '8px',
  },
  // A long company list scrolls inside its card rather than pushing the page down by 50 bars.
  barScroll: {
    maxHeight: '300px',
    overflowY: 'auto',
    paddingRight: '4px',
    '@media print': {
      maxHeight: 'none',
      overflow: 'visible',
    },
  },
  note: {
    color: tokens.colorNeutralForeground3,
  },
});

export type GroupKind = 'company' | 'department';

/**
 * A breakdown row's name as shown: the tenant's own name verbatim, "(no department)" for people with
 * none, and the roll-up of small groups as "Other (n)".
 */
export function groupName(t: TFunction, row: Pick<ActivityAnalysisGroupRow, 'name' | 'other'>, kind: GroupKind, otherGroups: number): string {
  if (row.other) return t('activityAnalysis.chart.other', { count: formatNumber(otherGroups) });
  if (row.name == null || row.name === '') {
    return kind === 'company' ? t('activityAnalysis.group.noCompany') : t('activityAnalysis.group.noDepartment');
  }
  return row.name;
}

function toCategories(t: TFunction, breakdown: ActivityAnalysisGroupBreakdown, kind: GroupKind): ReportCategory[] {
  return breakdown.rows.map((row) => ({ label: groupName(t, row, kind, breakdown.otherGroups), value: row.activePeople }));
}

function OtherNote({ count }: { count: number }) {
  const styles = useStyles();
  const t = useT();
  if (count <= 0) return null;
  return (
    <Text size={200} className={styles.note}>
      {t(plural(count, 'activityAnalysis.chart.otherNote.one', 'activityAnalysis.chart.otherNote.other'), {
        count: formatNumber(count),
      })}
    </Text>
  );
}

/** Active people by company (bars) and by department (treemap) - the two Power BI breakdown visuals. */
export default function ActivePeopleCharts({
  byCompany,
  byDepartment,
}: {
  byCompany: ActivityAnalysisGroupBreakdown;
  byDepartment: ActivityAnalysisGroupBreakdown;
}) {
  const styles = useStyles();
  const t = useT();
  const valueLabel = t('activityAnalysis.chart.activePeople');
  const info = {
    what: t('activityAnalysis.chart.byGroup.info.what'),
    how: t('activityAnalysis.chart.byGroup.info.how'),
  };

  return (
    <div className={styles.grid}>
      <Card className={styles.card}>
        <div className={styles.head}>
          <Text as="h2" weight="semibold" size={400} style={{ margin: 0 }}>
            {t('activityAnalysis.chart.byCompany.title')}
          </Text>
          <InfoTip title={t('activityAnalysis.chart.byCompany.title')} content={info} />
        </div>
        <div className={styles.barScroll}>
          <CategoryBarChart categories={toCategories(t, byCompany, 'company')} valueLabel={valueLabel} />
        </div>
        <OtherNote count={byCompany.otherGroups} />
      </Card>

      <Card className={styles.card}>
        <div className={styles.head}>
          <Text as="h2" weight="semibold" size={400} style={{ margin: 0 }}>
            {t('activityAnalysis.chart.byDepartment.title')}
          </Text>
          <InfoTip title={t('activityAnalysis.chart.byDepartment.title')} content={info} />
        </div>
        <TreemapChart categories={toCategories(t, byDepartment, 'department')} valueLabel={valueLabel} height={300} />
        <OtherNote count={byDepartment.otherGroups} />
      </Card>
    </div>
  );
}
